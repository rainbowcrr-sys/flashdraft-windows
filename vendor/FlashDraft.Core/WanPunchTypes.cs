using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FlashDraft.Core;

namespace FlashDraft.Transport;

/// <summary>Local NAT classification. Subtypes that need RFC 3489 CHANGE-REQUEST
/// are reported conservatively as a cone and labelled as an estimate.</summary>
public enum NatType
{
    Unknown,
    Open,
    FullCone,
    RestrictedCone,
    PortRestrictedCone,
    Symmetric,
    UdpBlocked,
}

/// <summary>A public STUN endpoint used for reflexive address discovery.</summary>
public sealed record StunServer(string Host, int Port)
{
    public override string ToString() => Host + ":" + Port;
}

/// <summary>Result of one STUN query round, including samples used for NAT typing.</summary>
public sealed record StunResult(
    string? Endpoint,
    IReadOnlyList<int> ProbePorts,
    string? Server,
    bool Ipv6Available)
{
    public NatType EstimateNatType() => NatClassifier.FromSamples(ProbePorts);
}

/// <summary>Ordered dial candidates. IPv6 global addresses come first, then the
/// IPv4 reflexive endpoint, then LAN candidates.</summary>
public sealed class EndpointSet
{
    private EndpointSet(IReadOnlyList<string> ordered) => Ordered = ordered;

    public IReadOnlyList<string> Ordered { get; }

    public static EndpointSet Build(
        string? ipv6,
        string? ipv4Reflexive,
        IEnumerable<string> lanCandidates)
    {
        var result = new List<string>();

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            IPAddress? address = Parse(candidate)?.Address;
            if (address is null) return;
            if (IPAddress.IsLoopback(address)) return;
            if (address.AddressFamily == AddressFamily.InterNetworkV6 &&
                (address.IsIPv6LinkLocal || address.IsIPv6Multicast)) return;
            string normalized = Normalize(candidate);
            if (!result.Contains(normalized)) result.Add(normalized);
        }

        Add(ipv6);
        Add(ipv4Reflexive);
        foreach (string candidate in lanCandidates) Add(candidate);
        return new EndpointSet(result);
    }

    /// <summary>Reads v2 endpoints when present, otherwise falls back to the v1
    /// reflexive endpoint plus LAN candidates so old offer codes still work.</summary>
    public static IReadOnlyList<string> FromOffer(SessionOffer offer)
    {
        if (offer.Endpoints.Count > 0) return offer.Endpoints;
        var fallback = new List<string>();
        if (!string.IsNullOrWhiteSpace(offer.ReflexiveEndpoint)) fallback.Add(offer.ReflexiveEndpoint!);
        fallback.AddRange(offer.LanCandidates);
        return fallback;
    }

    /// <summary>Parses "host:port" and "[v6]:port" into an endpoint.</summary>
    public static IPEndPoint? Parse(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        string text = candidate.Trim();
        string host;
        string portText;
        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            int close = text.IndexOf(']');
            if (close < 0 || close + 1 >= text.Length || text[close + 1] != ':') return null;
            host = text.Substring(1, close - 1);
            portText = text.Substring(close + 2);
        }
        else
        {
            int separator = text.LastIndexOf(':');
            if (separator < 0) return null;
            host = text.Substring(0, separator);
            portText = text.Substring(separator + 1);
        }

        if (!IPAddress.TryParse(host, out IPAddress? address) ||
            !int.TryParse(portText, out int port) || port is < 1 or > 65535)
        {
            return null;
        }

        return new IPEndPoint(address, port);
    }

    public static string Normalize(string candidate)
    {
        IPEndPoint? ep = Parse(candidate);
        if (ep is null) return candidate;
        return ep.AddressFamily == AddressFamily.InterNetworkV6
            ? "[" + ep.Address + "]:" + ep.Port
            : ep.Address + ":" + ep.Port;
    }

    public static AddressFamily FamilyOf(string candidate) =>
        Parse(candidate)?.AddressFamily ?? AddressFamily.InterNetwork;
}

/// <summary>Options for the dual-stack WAN hole-punching state machine.</summary>
public sealed record PunchOptions(
    Guid SessionId,
    byte[] Token,
    IReadOnlyList<string>? RemoteEndpoints,
    int? LocalPort = null,
    bool EnableIpv6 = true,
    bool IsInitiator = false,
    TimeSpan? Window = null,
    int ProbeIntervalMs = 500,
    int MaxProbeIntervalMs = 2000,
    NatType LocalNat = NatType.Unknown,
    NatType RemoteNat = NatType.Unknown,
    UdpClient? SharedV4 = null,
    UdpClient? SharedV6 = null);

/// <summary>Structured, bilingual failure description. Nothing is uploaded.</summary>
public sealed record PunchFailure(
    string Code,
    string TitleEn,
    string TitleZh,
    string ReasonEn,
    string ReasonZh,
    IReadOnlyList<string> Suggestions);

/// <summary>Outcome of a punch attempt, including the negotiated socket.</summary>
public sealed record PunchOutcome(
    bool Succeeded,
    PunchedEndpoint? Endpoint,
    PunchFailure? Failure,
    NatType LocalNat);

/// <summary>Thrown when WAN hole punching fails, carrying the detailed message.</summary>
public sealed class PunchFailedException : InvalidOperationException
{
    public PunchFailedException(PunchFailure failure)
        : base(failure.TitleEn + "\n" + failure.TitleZh + "\n" + failure.ReasonEn + "\n" +
               failure.ReasonZh + "\n" + string.Join("\n", failure.Suggestions) +
               "\nDiagnostic code / 诊断码: " + failure.Code)
    {
        Failure = failure;
    }

    public PunchFailure Failure { get; }
}

/// <summary>Classifies NAT behaviour from the mapped ports seen across different
/// STUN servers. A stable port means a cone; varying ports mean symmetric NAT.</summary>
public static class NatClassifier
{
    public static NatType FromSamples(IReadOnlyList<int> ports)
    {
        if (ports.Count == 0) return NatType.UdpBlocked;
        if (ports.Count == 1) return NatType.Unknown;
        int first = ports[0];
        bool varies = false;
        for (int i = 1; i < ports.Count; i++)
        {
            if (ports[i] != first) varies = true;
        }

        return varies ? NatType.Symmetric : NatType.PortRestrictedCone;
    }

    public static string Display(NatType type) => type switch
    {
        NatType.Open => "Open / 公网直连",
        NatType.FullCone => "Full Cone / 全锥型",
        NatType.RestrictedCone => "Restricted Cone / 受限锥型",
        NatType.PortRestrictedCone => "Port-Restricted Cone / 端口受限锥型（推定）",
        NatType.Symmetric => "Symmetric NAT / 对称型",
        NatType.UdpBlocked => "UDP blocked / UDP 被阻断",
        _ => "Unknown / 未能判定",
    };

    public static string Short(NatType type) => type switch
    {
        NatType.Open => "OPN",
        NatType.FullCone => "FCL",
        NatType.RestrictedCone => "RCL",
        NatType.PortRestrictedCone => "PRC",
        NatType.Symmetric => "SYM",
        NatType.UdpBlocked => "BLK",
        _ => "UNK",
    };
}

/// <summary>Builds the detailed, bilingual failure text shown to the user.</summary>
public static class PunchDiagnostics
{
    public static PunchFailure Build(NatType local, NatType remote, string stage)
    {
        string stageCode = stage switch
        {
            "stun" => "01",
            "probe" => "02",
            _ => "03",
        };
        string code = "NET-" + NatClassifier.Short(local) + "-" +
                      NatClassifier.Short(remote) + "-" + stageCode;

        string reasonEn = "Both peers must send UDP probes at the same time to open their NAT mappings. " +
                          "Local NAT: " + NatClassifier.Display(local) + "; remote NAT: " +
                          NatClassifier.Display(remote) + ".";
        string reasonZh = "双方需要同时发送 UDP 探测包以打开 NAT 映射。" +
                          "本机 NAT：" + NatClassifier.Display(local) +
                          "；对端 NAT：" + NatClassifier.Display(remote) + "。";

        if (local == NatType.Symmetric || remote == NatType.Symmetric)
        {
            reasonEn += " A symmetric NAT allocates a different external port for every destination, " +
                        "so the reflexive address in the offer code cannot be reused.";
            reasonZh += " 对称型 NAT 会为每个目标分配不同的外部端口，连接码里的反射地址无法复用。";
        }
        else if (local == NatType.UdpBlocked || remote == NatType.UdpBlocked)
        {
            reasonEn += " UDP egress appears to be blocked on at least one side.";
            reasonZh += " 至少有一侧的 UDP 出站被阻断。";
        }
        else
        {
            reasonEn += " The probes did not complete within the punch window (30 s).";
            reasonZh += " 探测包未能在打洞窗口（30 秒）内完成握手。";
        }

        return new PunchFailure(
            code,
            "WAN hole punching failed",
            "公网打洞失败",
            reasonEn,
            reasonZh,
            new[]
            {
                "1. Use the same LAN / 改用同一局域网直连",
                "2. Enable IPv6 on both devices / 双方开启 IPv6（可绕过 NAT）",
                "3. Configure manual port mapping / 在路由器配置端口映射",
                "4. Exchange a fresh offer code (codes expire in 5 minutes) / 重新交换连接码（5 分钟过期）",
            });
    }
}

/// <summary>Binary-safe helpers for the v2 probe/ack datagrams.</summary>
public static class PunchWire
{
    public const string Prefix = "FD2";

    /// <summary>
    /// Both peers derive the same session id from the exchanged offers, so it
    /// is the shared key for the probe MAC. The one-time token is per-peer and
    /// is only used to prove that a session has actually been bound.
    /// </summary>
    public static byte[] Key(Guid sessionId) =>
        SHA256.HashData(sessionId.ToByteArray()).AsSpan(0, 16).ToArray();

    public static string Seal(string kind, Guid sessionId)
    {
        string body = Prefix + "|" + kind + "|" + sessionId.ToString("D");
        using var hmac = new HMACSHA256(Key(sessionId));
        string mac = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant().Substring(0, 16);
        return body + "|" + mac;
    }

    public static string Frame(string kind, Guid sessionId, string selfEndpoint) =>
        Seal(kind, sessionId) + "|" + selfEndpoint;

    public static (string Kind, string SelfEndpoint)? Parse(byte[] datagram, Guid sessionId)
    {
        if (datagram.Length is 0 or > ProtocolConstants.MaxPunchPacketBytes) return null;
        string text;
        try
        {
            text = Encoding.UTF8.GetString(datagram);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        string[] parts = text.Split('|');
        if (parts.Length != 5) return null;
        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal)) return null;
        if (parts[1] != "PROBE" && parts[1] != "ACK") return null;
        if (!string.Equals(parts[2], sessionId.ToString("D"), StringComparison.Ordinal)) return null;

        string expected = Seal(parts[1], sessionId);
        string actual = parts[0] + "|" + parts[1] + "|" + parts[2] + "|" + parts[3];
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual)))
        {
            return null;
        }

        return (parts[1], parts[4]);
    }
}
