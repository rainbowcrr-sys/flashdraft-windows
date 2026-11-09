using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FlashDraft.Core;

namespace FlashDraft.Transport;

/// <summary>Obtains a public reflexive endpoint for WAN hole punching.</summary>
public interface IStunClient
{
    Task<string?> GetReflexiveEndpointAsync(CancellationToken cancellationToken);
}

/// <summary>
/// STUN Binding Request client (RFC 5389 / RFC 8489 subset). It keeps the
/// original single-shot API and adds a five-attempt, multi-server, dual-stack
/// query used when an offer is generated. TURN relay is intentionally absent.
/// </summary>
public sealed class StunClient : IStunClient
{
    private readonly string _host;
    private readonly int _port;

    public StunClient(string? host = null, int? port = null)
    {
        _host = host ?? ProtocolConstants.DefaultStunHost;
        _port = port ?? ProtocolConstants.DefaultStunPort;
    }

    /// <summary>Legacy single-shot lookup. Kept for callers that only need a string.</summary>
    public async Task<string?> GetReflexiveEndpointAsync(CancellationToken cancellationToken)
    {
        StunResult result = await QueryAsync(AddressFamily.InterNetwork, cancellationToken)
            .ConfigureAwait(false);
        return result.Endpoint;
    }

    /// <summary>
    /// Queries up to five servers with exponential backoff. Successes from two
    /// different servers are enough for NAT typing, so the round usually ends
    /// early; a fully blocked UDP path takes about twelve seconds.
    /// </summary>
    public async Task<StunResult> QueryAsync(
        AddressFamily family, CancellationToken cancellationToken)
    {
        IReadOnlyList<StunServer> servers = Servers();
        var ports = new List<int>();
        string? endpoint = null;
        string? usedServer = null;
        var seenServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int attempt = 0; attempt < ProtocolConstants.StunMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StunServer server = servers[attempt % servers.Count];
            byte[] transaction = RandomNumberGenerator.GetBytes(12);
            string? mapped = await QueryOnceAsync(server, family, transaction, cancellationToken)
                .ConfigureAwait(false);

            if (mapped is not null)
            {
                IPEndPoint? parsed = EndpointSet.Parse(mapped);
                if (parsed is not null)
                {
                    endpoint ??= EndpointSet.Normalize(mapped);
                    usedServer ??= server.ToString();
                    seenServers.Add(server.Host);
                    ports.Add(parsed.Port);
                }
            }

            if (endpoint is not null && seenServers.Count >= 2)
            {
                break;
            }

            if (attempt + 1 < ProtocolConstants.StunMaxAttempts)
            {
                await Task.Delay(ProtocolConstants.StunBackoffMs[attempt], cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new StunResult(endpoint, ports, usedServer, family == AddressFamily.InterNetworkV6);
    }

    /// <summary>
    /// Runs one Binding Request through an already bound socket. Using the same
    /// socket for STUN discovery and hole punching keeps the reflexive port
    /// identical to the port the peer will dial.
    /// </summary>
    public static async Task<string?> QueryBoundAsync(
        UdpClient socket,
        StunServer server,
        AddressFamily family,
        CancellationToken cancellationToken)
    {
        IPAddress? target;
        try
        {
            target = Array.Find(
                Dns.GetHostAddresses(server.Host),
                address => address.AddressFamily == family);
        }
        catch (SocketException)
        {
            return null;
        }

        if (target is null) return null;

        byte[] transaction = RandomNumberGenerator.GetBytes(12);
        byte[] request = BuildBindingRequestWithTransaction(transaction);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProtocolConstants.StunReceiveTimeoutMs);
        try
        {
            await socket.SendAsync(request, request.Length, new IPEndPoint(target, server.Port))
                .ConfigureAwait(false);
            UdpReceiveResult reply = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            return ParseBindingResponse(reply.Buffer, transaction);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private async Task<string?> QueryOnceAsync(
        StunServer server,
        AddressFamily family,
        byte[] transaction,
        CancellationToken cancellationToken)
    {
        IPAddress? target;
        try
        {
            target = Array.Find(
                Dns.GetHostAddresses(server.Host),
                address => address.AddressFamily == family);
        }
        catch (SocketException)
        {
            return null;
        }

        if (target is null) return null;

        byte[] request = BuildBindingRequestWithTransaction(transaction);
        using var udp = new UdpClient(family);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProtocolConstants.StunReceiveTimeoutMs);
        try
        {
            await udp.SendAsync(request, request.Length, new IPEndPoint(target, server.Port))
                .ConfigureAwait(false);
            UdpReceiveResult reply = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            return ParseBindingResponse(reply.Buffer, transaction);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static IReadOnlyList<StunServer> Servers()
    {
        var list = new List<StunServer>();
        foreach (string entry in ProtocolConstants.StunServerEndpoints)
        {
            int colon = entry.LastIndexOf(':');
            if (colon > 0 && int.TryParse(entry.Substring(colon + 1), out int port))
            {
                list.Add(new StunServer(entry.Substring(0, colon), port));
            }
        }

        if (list.Count == 0)
        {
            list.Add(new StunServer(ProtocolConstants.DefaultStunHost, ProtocolConstants.DefaultStunPort));
        }

        return list;
    }

    /// <summary>Builds a 20-byte Binding Request with the STUN magic cookie.</summary>
    public static byte[] BuildBindingRequest()
    {
        return BuildBindingRequestWithTransaction(RandomNumberGenerator.GetBytes(12));
    }

    /// <summary>Builds a Binding Request with a caller-supplied transaction id.</summary>
    public static byte[] BuildBindingRequestWithTransaction(byte[] transaction)
    {
        if (transaction.Length != 12)
        {
            throw new ArgumentException("STUN transaction id must be 12 bytes.", nameof(transaction));
        }

        byte[] packet = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), ProtocolConstants.StunMagicCookie);
        transaction.CopyTo(packet.AsSpan(8, 12));
        return packet;
    }

    /// <summary>
    /// Validates a Binding Success Response (type 0x0101), magic cookie and
    /// transaction id before trusting its mapped address. Forged datagrams are
    /// discarded without allocating a session.
    /// </summary>
    public static string? ParseBindingResponse(byte[] packet, byte[] transaction)
    {
        if (packet.Length < 20 || transaction.Length != 12) return null;
        if (BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(0, 2)) != 0x0101) return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4, 4)) !=
            ProtocolConstants.StunMagicCookie) return null;
        if (!CryptographicOperations.FixedTimeEquals(
                packet.AsSpan(8, 12), transaction.AsSpan())) return null;
        return ParseMappedAddress(packet);
    }

    /// <summary>Parses XOR-MAPPED-ADDRESS (0x0020) or MAPPED-ADDRESS (0x0001),
    /// for both IPv4 and IPv6. IPv6 returns the bracketed "[address]:port" form.</summary>
    public static string? ParseMappedAddress(byte[] packet)
    {
        if (packet.Length < 20)
        {
            return null;
        }

        uint cookie = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4, 4));
        ReadOnlySpan<byte> transaction = packet.AsSpan(8, 12);
        int offset = 20;
        while (offset + 4 <= packet.Length)
        {
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 2, 2));
            if (offset + 4 + length > packet.Length)
            {
                break;
            }

            ReadOnlySpan<byte> value = packet.AsSpan(offset + 4, length);
            if (value.Length >= 8 && (type == 0x0020 || type == 0x0001))
            {
                byte family = value[1];
                int port = (value[2] << 8) | value[3];

                if (family == 0x01 && value.Length >= 8)
                {
                    uint raw = BinaryPrimitives.ReadUInt32BigEndian(value.Slice(4, 4));
                    if (type == 0x0020)
                    {
                        port ^= (int)(cookie >> 16);
                        raw ^= cookie;
                    }

                    byte[] bytes = BitConverter.GetBytes(raw);
                    Array.Reverse(bytes);
                    return new IPAddress(bytes) + ":" + port;
                }

                if (family == 0x02 && value.Length >= 20)
                {
                    byte[] address = value.Slice(4, 16).ToArray();
                    if (type == 0x0020)
                    {
                        port ^= (int)(cookie >> 16);
                        byte[] mask = new byte[16];
                        BinaryPrimitives.WriteUInt32BigEndian(mask.AsSpan(0, 4), cookie);
                        transaction.CopyTo(mask.AsSpan(4, 12));
                        for (int i = 0; i < 16; i++) address[i] ^= mask[i];
                    }

                    return "[" + new IPAddress(address) + "]:" + port;
                }
            }

            offset += 4 + ((length + 3) & ~3);
        }

        return null;
    }
}

/// <summary>Adjustable: offline stub used by tests and restricted networks.</summary>
public sealed class NullStunClient : IStunClient
{
    public Task<string?> GetReflexiveEndpointAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}
