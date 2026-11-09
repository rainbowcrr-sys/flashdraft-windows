namespace FlashDraft.Core;

/// <summary>A short-lived session. Keys are ephemeral and are never persisted.</summary>
public sealed record TransferSession(
    Guid SessionId,
    byte[] EphemeralPublicKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    byte[] OneTimeToken,
    IReadOnlyList<string> LanCandidates,
    string? StunEndpoint);

/// <summary>Describes one file inside a transfer session.</summary>
public sealed record FileDescriptor(
    uint FileIndex,
    string RelativePath,
    long Length,
    uint TotalChunks,
    byte[] Sha256)
{
    /// <summary>Adjustable: LAN uses 256 KiB chunks; WAN UDP uses 32 KiB.</summary>
    public int ChunkSize { get; init; } = ProtocolConstants.ChunkSize;
}

/// <summary>Immutable progress snapshot pushed to the UI layer.</summary>
public sealed record ProgressSnapshot(
    uint FileIndex,
    string RelativePath,
    long BytesTransferred,
    long TotalBytes,
    double BytesPerSecond,
    TransferState State);

public enum TransferState
{
    Queued,
    Negotiating,
    Punching,
    Transferring,
    Verifying,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>LAN discovery announcement.</summary>
public sealed record DeviceAnnouncement(
    Guid SessionId,
    string DeviceName,
    int Port,
    byte[] EphemeralPublicKey,
    int ProtocolVersion);

/// <summary>Bitmap of received chunks, persisted next to the .part file.</summary>
public sealed class ChunkBitmap
{
    private readonly byte[] _bits;

    public ChunkBitmap(uint totalChunks)
    {
        TotalChunks = totalChunks;
        _bits = new byte[(int)((totalChunks + 7) / 8)];
    }

    private ChunkBitmap(uint totalChunks, byte[] bits)
    {
        TotalChunks = totalChunks;
        _bits = bits;
    }

    public uint TotalChunks { get; }

    public bool IsSet(uint index) => index < TotalChunks && (_bits[index / 8] & (1 << (int)(index % 8))) != 0;

    public void Set(uint index)
    {
        if (index >= TotalChunks)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _bits[index / 8] |= (byte)(1 << (int)(index % 8));
    }

    public bool IsComplete
    {
        get
        {
            for (uint i = 0; i < TotalChunks; i++)
            {
                if (!IsSet(i))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public string ToBase64() => Convert.ToBase64String(_bits);

    public static ChunkBitmap FromBase64(uint totalChunks, string value) =>
        new(totalChunks, Convert.FromBase64String(value));
}


/// <summary>
/// A short-lived, manually exchanged offer. It carries the offerer's ephemeral
/// public key, LAN candidates, an optional STUN reflexive endpoint, and a
/// one-time token. There is no signaling server and no automatic WAN discovery.
/// </summary>
public sealed record SessionOffer(
    Guid SessionId,
    byte[] OffererPublicKey,
    DateTimeOffset ExpiresAt,
    byte[] OneTimeToken,
    IReadOnlyList<string> LanCandidates,
    string? ReflexiveEndpoint)
{
    /// <summary>
    /// v2: ordered dial candidates (IPv6 global, IPv4 reflexive, LAN).
    /// Empty on v1 offers, which fall back to ReflexiveEndpoint + LanCandidates.
    /// </summary>
    public IReadOnlyList<string> Endpoints { get; init; } = Array.Empty<string>();

    /// <summary>Local NAT classification, used only for bilingual failure hints.</summary>
    public string? NatTypeHint { get; init; }
}
