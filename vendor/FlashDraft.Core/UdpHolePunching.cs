using System.Net;
using System.Net.Sockets;
using System.Text;
using FlashDraft.Core;

namespace FlashDraft.Transport;

public enum PunchState
{
    Idle,
    Probing,
    Succeeded,
    Failed,
}

/// <summary>A bound UDP socket whose peer has answered hole-punching probes.</summary>
public sealed record PunchedEndpoint(UdpClient Client, IPEndPoint Remote);

/// <summary>
/// Bidirectional UDP hole punching. The initiator knows the peer's reflexive
/// endpoint from the offer code; the responder starts with a null endpoint and
/// learns it from the first inbound probe. Both sides send probes and answer
/// them until a PROBE-ACK arrives. TURN relay is intentionally not implemented.
/// </summary>
public sealed class UdpHolePunchingService : IHolePunchingService
{
    public PunchState State { get; private set; } = PunchState.Idle;

    /// <summary>Legacy single-sided probe kept for the IHolePunchingService contract.</summary>
    public async Task<HolePunchResult> PunchAsync(
        TransferSession session, string remoteEndpoint, CancellationToken cancellationToken)
    {
        (string host, int port) = ParseEndpoint(remoteEndpoint);
        using var probe = new UdpClient(AddressFamily.InterNetwork);
        byte[] data = Encoding.UTF8.GetBytes("PROBE:" + session.SessionId.ToString("D"));
        for (int i = 0; i < ProtocolConstants.HolePunchProbeCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await probe.SendAsync(data, data.Length, host, port);
            await Task.Delay(ProtocolConstants.HolePunchProbeIntervalMs, cancellationToken);
        }

        State = PunchState.Failed;
        return new HolePunchResult(
            false,
            "Use PunchAsync(int, string?, Guid, CancellationToken) for bidirectional probing. TURN relay is not implemented.",
            null);
    }

    /// <summary>
    /// Binds localPort and punches a hole. Pass a null remoteEndpoint on the
    /// responder side; it learns the peer address from inbound probes.
    /// </summary>
    public async Task<PunchedEndpoint?> PunchAsync(
        int localPort, string? remoteEndpoint, Guid sessionId, CancellationToken cancellationToken)
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));

        string probeText = "PROBE:" + sessionId.ToString("D");
        string ackText = "PROBE-ACK:" + sessionId.ToString("D");
        byte[] probe = Encoding.UTF8.GetBytes(probeText);
        byte[] ack = Encoding.UTF8.GetBytes(ackText);
        IPEndPoint? learned = null;
        if (remoteEndpoint is not null)
        {
            (string host, int port) = ParseEndpoint(remoteEndpoint);
            learned = new IPEndPoint(IPAddress.Parse(host), port);
        }

        State = PunchState.Probing;
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(ProtocolConstants.HolePunchTimeoutMs);
        try
        {
            while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                if (learned is not null)
                {
                    await client.SendAsync(probe, probe.Length, learned);
                }

                using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                window.CancelAfter(ProtocolConstants.HolePunchProbeIntervalMs);
                try
                {
                    UdpReceiveResult reply = await client.ReceiveAsync(window.Token);
                    string text = Encoding.UTF8.GetString(reply.Buffer);
                    if (text == probeText)
                    {
                        await client.SendAsync(ack, ack.Length, reply.RemoteEndPoint);
                        if (remoteEndpoint is null)
                        {
                            // Responder side: the inbound probe already opened
                            // the mapping, so the channel is usable.
                            State = PunchState.Succeeded;
                            return new PunchedEndpoint(client, reply.RemoteEndPoint);
                        }

                        learned = reply.RemoteEndPoint;
                    }
                    else if (text == ackText)
                    {
                        State = PunchState.Succeeded;
                        return new PunchedEndpoint(client, reply.RemoteEndPoint);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Keep probing until the overall deadline.
                }
            }
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }

        State = PunchState.Failed;
        client.Dispose();
        return null;
    }

    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        int separator = endpoint.LastIndexOf(':');
        if (separator < 0)
        {
            throw new FormatException("Endpoint must be host:port.");
        }

        return (endpoint.Substring(0, separator), int.Parse(endpoint.Substring(separator + 1)));
    }
}
