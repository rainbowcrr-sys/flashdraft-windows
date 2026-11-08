using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FlashDraft.Core;

namespace FlashDraft.Transport;

/// <summary>
/// LAN discovery over UDP multicast (primary). Announcements are small JSON
/// datagrams sent on a fixed group and port; heartbeat cadence is adjustable.
/// </summary>
public sealed class UdpMulticastDiscovery : IDiscoveryService, IDisposable
{
    private readonly UdpClient _client;

    public UdpMulticastDiscovery()
    {
        _client = new UdpClient(AddressFamily.InterNetwork);
        _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _client.Client.Bind(new IPEndPoint(IPAddress.Any, ProtocolConstants.MulticastPort));
        _client.JoinMulticastGroup(IPAddress.Parse(ProtocolConstants.MulticastGroup));
    }

    public async Task AnnounceAsync(DeviceAnnouncement announcement, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(announcement);
        var target = new IPEndPoint(IPAddress.Parse(ProtocolConstants.MulticastGroup), ProtocolConstants.MulticastPort);
        await _client.SendAsync(payload, target, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<DeviceAnnouncement> DiscoverAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            DeviceAnnouncement? announcement;
            try
            {
                announcement = JsonSerializer.Deserialize<DeviceAnnouncement>(result.Buffer);
            }
            catch (JsonException)
            {
                continue;
            }

            if (announcement is not null && announcement.ProtocolVersion == ProtocolConstants.ProtocolVersion)
            {
                yield return announcement;
            }
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Pluggable DNS-SD provider for the mDNS fallback path.</summary>
public interface IMdnsProvider
{
    IAsyncEnumerable<DeviceAnnouncement> QueryAsync(string serviceType, CancellationToken cancellationToken);
}

/// <summary>
/// Adjustable: default provider is a no-op. Plug in a DNS-SD implementation
/// (for example a library that resolves _flashdraft._udp.local with TXT records
/// carrying sid, port, pk and v) before production release.
/// </summary>
public sealed class NullMdnsProvider : IMdnsProvider
{
    public async IAsyncEnumerable<DeviceAnnouncement> QueryAsync(
        string serviceType,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}

/// <summary>mDNS fallback discovery. Used when multicast is blocked on the network.</summary>
public sealed class MDnsFallbackDiscovery : IDiscoveryService
{
    private readonly IMdnsProvider _provider;

    public MDnsFallbackDiscovery(IMdnsProvider? provider = null)
    {
        _provider = provider ?? new NullMdnsProvider();
    }

    public Task AnnounceAsync(DeviceAnnouncement announcement, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public IAsyncEnumerable<DeviceAnnouncement> DiscoverAsync(CancellationToken cancellationToken) =>
        _provider.QueryAsync(ProtocolConstants.MDnsServiceType, cancellationToken);
}
