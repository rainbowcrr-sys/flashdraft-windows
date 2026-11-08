namespace FlashDraft.Core;

/// <summary>
/// Wire-level constants shared by all FlashDraft components.
/// Values marked Adjustable may be tuned without changing the protocol shape.
/// </summary>
public static class ProtocolConstants
{
    public const string UriScheme = "flashdraft";
    public const int ProtocolVersion = 1;

    /// <summary>ASCII "FDRF" written big-endian at the start of every frame.</summary>
    public const uint Magic = 0x46445246;

    public const int SessionIdSize = 16;
    public const int FrameHeaderSize = 72;
    public const int ChunkSize = 256 * 1024;
    public const int Sha256Size = 32;
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int GcmTagSize = 16;
    public const int TokenSize = 16;
    public const int PinLength = 6;

    /// <summary>Adjustable: private administratively scoped multicast group.</summary>
    public const string MulticastGroup = "239.255.70.90";

    /// <summary>Adjustable: fixed discovery and control port.</summary>
    public const int MulticastPort = 45790;

    /// <summary>Adjustable: LAN announcement interval.</summary>
    public const int HeartbeatSeconds = 3;

    /// <summary>Adjustable: DNS-SD service type used by the mDNS fallback.</summary>
    public const string MDnsServiceType = "_flashdraft._udp.local";

    /// <summary>Adjustable: WAN hole punching probe schedule.</summary>
    public const int HolePunchProbeCount = 3;
    public const int HolePunchProbeIntervalMs = 500;
    public const int HolePunchTimeoutMs = 5000;

    /// <summary>Adjustable: throughput is averaged over a sliding window.</summary>
    public const int ProgressWindowSeconds = 2;

    /// <summary>Adjustable: ViewModel refresh cadence.</summary>
    public const int UiRefreshMs = 100;

    /// <summary>Adjustable: QR error correction level is Medium (M).</summary>
    public const string QrErrorCorrectionLevel = "M";
}

/// <summary>Frame types carried in the header.</summary>
public enum FrameType : byte
{
    Hello = 1,
    Offer = 2,
    Accept = 3,
    Data = 4,
    Ack = 5,
    Complete = 6,
    Cancel = 7,
    Ping = 8,
    Probe = 9,
}
