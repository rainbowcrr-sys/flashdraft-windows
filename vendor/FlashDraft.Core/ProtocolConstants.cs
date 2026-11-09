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

    /// <summary>Adjustable: public STUN server used to obtain a reflexive address.</summary>
    public const string DefaultStunHost = "stun.l.google.com";
    public const int DefaultStunPort = 19302;

    /// <summary>Adjustable: UDP port used for WAN hole punching and data.</summary>
    public const int WanDataPort = 45792;

    /// <summary>Adjustable: WAN chunks stay below the typical path MTU to avoid IP fragmentation.</summary>
    public const int WanChunkSize = 1000;

    public const int UdpAckTimeoutMs = 600;
    public const int UdpMaxRetries = 6;
    public const int ConnectionCodeTtlMinutes = 5;
    public const uint StunMagicCookie = 0x2112A442;

    /// <summary>WAN hole punching: five STUN attempts with exponential backoff.</summary>
    public const int StunMaxAttempts = 5;
    public static readonly int[] StunBackoffMs = { 400, 800, 1600, 3200, 6400 };
    public const int StunReceiveTimeoutMs = 1200;

    /// <summary>Adjustable: public STUN servers, rotated between attempts.</summary>
    public static readonly string[] StunServerEndpoints =
    {
        "stun.l.google.com:19302",
        "stun.cloudflare.com:3478",
        "stun.miwifi.com:3478",
        "global.stun.twilio.com:3478",
    };

    /// <summary>Offer codes stay valid for five minutes; keep the NAT mapping warm.</summary>
    public const int OfferKeepAliveSeconds = 20;

    /// <summary>Dual-stack punch window and probe schedule.</summary>
    public const int PunchWindowSeconds = 30;
    public const int MaxProbeIntervalMs = 2000;
    public const int MaxPunchPacketBytes = 1200;

    /// <summary>Marks an offer payload that carries the v2 endpoint set.</summary>
    public const int OfferFormatVersion2 = 2;
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
