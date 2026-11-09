using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using FlashDraft.Core;

namespace FlashDraft.Transport;

internal readonly record struct Inbound(UdpReceiveResult Result, UdpClient Socket);

/// <summary>
/// Dual-stack WAN hole punching. A random local port is used by default; IPv4
/// and IPv6 sockets are independent, probes are sent to every candidate endpoint
/// in parallel, and both sides transmit at the same time so port-restricted
/// cones open. The offer generator keeps the NAT mapping alive with periodic
/// STUN Binding Requests. TURN relay is intentionally not implemented.
/// </summary>
public sealed class WanPunchService
{
    private const int ProbeBackoffFactor = 2;

    public async Task<PunchOutcome> PunchAsync(PunchOptions options, CancellationToken cancellationToken)
    {
        if (options.Token is null || options.Token.Length == 0)
        {
            throw new ArgumentException("A one-time token is required for authenticated probing.", nameof(options));
        }

        var channel = Channel.CreateUnbounded<Inbound>();
        UdpClient v4 = options.SharedV4
            ?? CreateSocket(AddressFamily.InterNetwork, options.LocalPort ?? 0);
        int boundPort = ((IPEndPoint)v4.Client.LocalEndPoint!).Port;
        UdpClient? v6 = options.SharedV6;
        if (v6 is null && options.EnableIpv6 && Socket.OSSupportsIPv6)
        {
            try
            {
                v6 = CreateSocket(AddressFamily.InterNetworkV6, boundPort);
            }
            catch (SocketException)
            {
                v6 = null;
            }
        }

        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task pump4 = PumpAsync(v4, channel.Writer, pumpCts.Token);
        Task pump6 = v6 is null
            ? Task.CompletedTask
            : PumpAsync(v6, channel.Writer, pumpCts.Token);

        byte[] probe = Encoding.UTF8.GetBytes(
            PunchWire.Frame("PROBE", options.SessionId, "local:" + boundPort));
        byte[] ack = Encoding.UTF8.GetBytes(
            PunchWire.Frame("ACK", options.SessionId, "local:" + boundPort));

        var remotes = new List<(IPEndPoint EndPoint, AddressFamily Family)>();
        foreach (string candidate in options.RemoteEndpoints ?? Array.Empty<string>())
        {
            IPEndPoint? parsed = EndpointSet.Parse(candidate);
            if (parsed is not null)
            {
                remotes.Add((parsed, parsed.AddressFamily));
            }
        }

        IPEndPoint? learned = null;
        TimeSpan window = options.Window ?? TimeSpan.FromSeconds(ProtocolConstants.PunchWindowSeconds);
        DateTime deadline = DateTime.UtcNow + window;
        int interval = options.ProbeIntervalMs;

        try
        {
            while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                await SendProbesAsync(remotes, learned, probe, v4, v6).ConfigureAwait(false);

                using var slice = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                slice.CancelAfter(interval);
                try
                {
                    Inbound inbound = await channel.Reader.ReadAsync(slice.Token).ConfigureAwait(false);
                    (string Kind, string SelfEndpoint)? message =
                        PunchWire.Parse(inbound.Result.Buffer, options.SessionId);
                    if (message is null)
                    {
                        continue;
                    }

                    IPEndPoint peer = inbound.Result.RemoteEndPoint;
                    if (message.Value.Kind == "PROBE")
                    {
                        try
                        {
                            await inbound.Socket.SendAsync(ack, ack.Length, peer).ConfigureAwait(false);
                        }
                        catch (SocketException)
                        {
                            // The peer may have vanished; keep probing.
                        }

                        learned = peer;
                    }
                    else
                    {
                        return Finish(
                            true,
                            new PunchedEndpoint(inbound.Socket, peer),
                            null,
                            options,
                            v4,
                            v6,
                            pumpCts,
                            pump4,
                            pump6);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // No datagram in this slice; keep probing until the deadline.
                }

                interval = Math.Min(interval * ProbeBackoffFactor, options.MaxProbeIntervalMs);
            }
        }
        catch (OperationCanceledException)
        {
            Cleanup(v4, v6, pumpCts, pump4, pump6);
            throw;
        }

        PunchFailure failure = PunchDiagnostics.Build(options.LocalNat, options.RemoteNat, "probe");
        return Finish(false, null, failure, options, v4, v6, pumpCts, pump4, pump6);
    }

    /// <summary>
    /// Keeps the UDP mapping open while an offer code is valid. Call this on the
    /// offer generator until the code expires or the session is established.
    /// </summary>
    public static async Task KeepAliveAsync(
        string host,
        int port,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        byte[] request = StunClient.BuildBindingRequest();
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await udp.SendAsync(request, request.Length, host, port).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // Best effort: a transient DNS or network failure is not fatal.
            }

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task SendProbesAsync(
        List<(IPEndPoint EndPoint, AddressFamily Family)> remotes,
        IPEndPoint? learned,
        byte[] probe,
        UdpClient v4,
        UdpClient? v6)
    {
        if (learned is not null && !remotes.Exists(r => r.EndPoint.Equals(learned)))
        {
            remotes.Add((learned, learned.AddressFamily));
        }

        foreach ((IPEndPoint endpoint, AddressFamily family) in remotes)
        {
            UdpClient? socket = family == AddressFamily.InterNetworkV6 ? v6 : v4;
            if (socket is null) continue;
            try
            {
                await socket.SendAsync(probe, probe.Length, endpoint).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // One unreachable candidate must not stop the others.
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private static async Task PumpAsync(
        UdpClient socket,
        ChannelWriter<Inbound> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result = await socket.ReceiveAsync(cancellationToken)
                    .ConfigureAwait(false);
                writer.TryWrite(new Inbound(result, socket));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Socket closed by the owner.
        }
        catch (SocketException)
        {
            // Unrecoverable socket error; the main loop will time out.
        }
    }

    private static UdpClient CreateSocket(AddressFamily family, int port)
    {
        var client = new UdpClient(family);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        IPAddress address = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        client.Client.Bind(new IPEndPoint(address, port));
        return client;
    }

    private static PunchOutcome Finish(
        bool succeeded,
        PunchedEndpoint? endpoint,
        PunchFailure? failure,
        PunchOptions options,
        UdpClient v4,
        UdpClient? v6,
        CancellationTokenSource pumpCts,
        Task pump4,
        Task pump6)
    {
        try
        {
            pumpCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already shut down.
        }

        // The negotiated socket is owned by the caller; dispose the other one.
        UdpClient? chosen = endpoint?.Client;
        if (!ReferenceEquals(v4, chosen)) v4.Dispose();
        if (v6 is not null && !ReferenceEquals(v6, chosen)) v6.Dispose();

        Observe(pump4);
        Observe(pump6);
        return new PunchOutcome(succeeded, endpoint, failure, options.LocalNat);
    }

    private static void Cleanup(
        UdpClient v4,
        UdpClient? v6,
        CancellationTokenSource pumpCts,
        Task pump4,
        Task pump6)
    {
        try { pumpCts.Cancel(); } catch (ObjectDisposedException) { }
        v4.Dispose();
        v6?.Dispose();
        Observe(pump4);
        Observe(pump6);
    }

    private static void Observe(Task task)
    {
        if (task.IsCompleted) return;
        _ = task.ContinueWith(
            t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
