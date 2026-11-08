using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FlashDraft.Core;

namespace FlashDraft.Transport;

/// <summary>Obtains a public reflexive endpoint for WAN hole punching.</summary>
public interface IStunClient
{
    Task<string?> GetReflexiveEndpointAsync(CancellationToken cancellationToken);
}

/// <summary>Minimal RFC 5389 Binding Request client.</summary>
public sealed class StunClient : IStunClient
{
    private readonly string _host;
    private readonly int _port;

    public StunClient(string? host = null, int? port = null)
    {
        _host = host ?? ProtocolConstants.DefaultStunHost;
        _port = port ?? ProtocolConstants.DefaultStunPort;
    }

    public async Task<string?> GetReflexiveEndpointAsync(CancellationToken cancellationToken)
    {
        byte[] request = BuildBindingRequest();
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await udp.SendAsync(request, request.Length, _host, _port);
            UdpReceiveResult reply = await udp.ReceiveAsync(timeout.Token);
            return ParseMappedAddress(reply.Buffer);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>Builds a 20-byte Binding Request with the STUN magic cookie.</summary>
    public static byte[] BuildBindingRequest()
    {
        byte[] packet = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), ProtocolConstants.StunMagicCookie);
        RandomNumberGenerator.Fill(packet.AsSpan(8, 12));
        return packet;
    }

    /// <summary>Parses XOR-MAPPED-ADDRESS (0x0020) or MAPPED-ADDRESS (0x0001).</summary>
    public static string? ParseMappedAddress(byte[] packet)
    {
        if (packet.Length < 20)
        {
            return null;
        }

        uint cookie = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4, 4));
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
            if (value.Length >= 8 && value[1] == 0x01 && (type == 0x0020 || type == 0x0001))
            {
                int port = (value[2] << 8) | value[3];
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
