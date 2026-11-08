namespace FlashDraft.Core;

/// <summary>Sends and receives files over an established session.</summary>
public interface ITransferService
{
    Task SendAsync(
        TransferSession session,
        IReadOnlyList<FileDescriptor> files,
        IReadOnlyList<string> sourceRootPaths,
        string remoteEndpoint,
        IProgress<ProgressSnapshot> progress,
        CancellationToken cancellationToken);

    Task ReceiveAsync(
        TransferSession session,
        string destinationDirectory,
        IProgress<ProgressSnapshot> progress,
        CancellationToken cancellationToken);
}

/// <summary>Announces and discovers peers. Multicast is primary; mDNS is the fallback.</summary>
public interface IDiscoveryService
{
    Task AnnounceAsync(DeviceAnnouncement announcement, CancellationToken cancellationToken);

    IAsyncEnumerable<DeviceAnnouncement> DiscoverAsync(CancellationToken cancellationToken);
}

/// <summary>Builds and parses the manually exchanged connection code.</summary>
public interface IPairingService
{
    string CreateConnectionCode(TransferSession session);

    TransferSession ParseConnectionCode(string code, DateTimeOffset now);

    string CreatePin(byte[] oneTimeToken);
}

/// <summary>UDP hole punching state machine. TURN is intentionally not implemented.</summary>
public interface IHolePunchingService
{
    Task<HolePunchResult> PunchAsync(
        TransferSession session,
        string remoteEndpoint,
        CancellationToken cancellationToken);
}

public sealed record HolePunchResult(bool Succeeded, string? FailureReason, string? NegotiatedEndpoint);
