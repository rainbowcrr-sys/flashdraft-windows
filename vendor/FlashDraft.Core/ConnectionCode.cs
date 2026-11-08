using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlashDraft.Core;

/// <summary>
/// Connection code codec. The payload is JSON, deflated with a zlib wrapper,
/// then Base64url encoded into a flashdraft:// URI. The QR renderer uses
/// error correction level M (see ProtocolConstants.QrErrorCorrectionLevel).
/// </summary>
public static class ConnectionCode
{
    private sealed class Payload
    {
        [JsonPropertyName("v")] public int V { get; set; }
        [JsonPropertyName("sid")] public string Sid { get; set; } = string.Empty;
        [JsonPropertyName("pk")] public string Pk { get; set; } = string.Empty;
        [JsonPropertyName("lan")] public string[] Lan { get; set; } = Array.Empty<string>();
        [JsonPropertyName("stun")] public string? Stun { get; set; }
        [JsonPropertyName("exp")] public long Exp { get; set; }
        [JsonPropertyName("tok")] public string Tok { get; set; } = string.Empty;
        [JsonPropertyName("wan")] public string? Wan { get; set; }
    }

    public static string Encode(TransferSession session)
    {
        var payload = new Payload
        {
            V = ProtocolConstants.ProtocolVersion,
            Sid = session.SessionId.ToString("D"),
            Pk = ToBase64Url(session.EphemeralPublicKey),
            Lan = session.LanCandidates.ToArray(),
            Stun = session.StunEndpoint,
            Exp = session.ExpiresAt.ToUnixTimeMilliseconds(),
            Tok = ToBase64Url(session.OneTimeToken),
        };

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
        return $"{ProtocolConstants.UriScheme}://{ToBase64Url(Compress(json))}";
    }

    public static TransferSession Decode(string code, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            !code.StartsWith($"{ProtocolConstants.UriScheme}://", StringComparison.Ordinal))
        {
            throw new FormatException("Not a FlashDraft connection code.");
        }

        string encoded = code.Substring($"{ProtocolConstants.UriScheme}://".Length);
        Payload payload = JsonSerializer.Deserialize<Payload>(Decompress(FromBase64Url(encoded)))
                          ?? throw new FormatException("Empty connection code payload.");

        if (payload.V != ProtocolConstants.ProtocolVersion)
        {
            throw new FormatException("Unsupported connection code version.");
        }

        DateTimeOffset expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(payload.Exp);
        if (expiresAt <= now)
        {
            throw new InvalidOperationException("Connection code has expired; re-negotiate a new code.");
        }

        return new TransferSession(
            Guid.Parse(payload.Sid),
            FromBase64Url(payload.Pk),
            now,
            expiresAt,
            FromBase64Url(payload.Tok),
            payload.Lan,
            payload.Stun);
    }

    /// <summary>Encodes a manually exchanged offer into a flashdraft:// URI.</summary>
    public static string EncodeOffer(SessionOffer offer)
    {
        var payload = new Payload
        {
            V = ProtocolConstants.ProtocolVersion,
            Sid = offer.SessionId.ToString("D"),
            Pk = ToBase64Url(offer.OffererPublicKey),
            Lan = offer.LanCandidates.ToArray(),
            Wan = offer.ReflexiveEndpoint,
            Exp = offer.ExpiresAt.ToUnixTimeMilliseconds(),
            Tok = ToBase64Url(offer.OneTimeToken),
        };

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
        return $"{ProtocolConstants.UriScheme}://{ToBase64Url(Compress(json))}";
    }

    /// <summary>Decodes an offer and rejects expired codes.</summary>
    public static SessionOffer DecodeOffer(string code, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            !code.StartsWith($"{ProtocolConstants.UriScheme}://", StringComparison.Ordinal))
        {
            throw new FormatException("Not a FlashDraft offer code.");
        }

        string encoded = code.Substring($"{ProtocolConstants.UriScheme}://".Length);
        Payload payload = JsonSerializer.Deserialize<Payload>(Decompress(FromBase64Url(encoded)))
                          ?? throw new FormatException("Empty offer payload.");

        if (payload.V != ProtocolConstants.ProtocolVersion)
        {
            throw new FormatException("Unsupported offer version.");
        }

        DateTimeOffset expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(payload.Exp);
        if (expiresAt <= now)
        {
            throw new InvalidOperationException("Offer code has expired; exchange a fresh code.");
        }

        return new SessionOffer(
            Guid.Parse(payload.Sid),
            FromBase64Url(payload.Pk),
            expiresAt,
            FromBase64Url(payload.Tok),
            payload.Lan,
            payload.Wan);
    }

    /// <summary>Derives a 6-digit PIN from the one-time token for out-of-band confirmation.</summary>
    public static string DerivePin(byte[] oneTimeToken)
    {
        uint value = 0;
        for (int i = 0; i < 3 && i < oneTimeToken.Length; i++)
        {
            value = (value << 8) | oneTimeToken[i];
        }

        uint pin = value % 1_000_000;
        return pin.ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] Compress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}

/// <summary>Default IPairingService implementation.</summary>
public sealed class PairingService : IPairingService
{
    public string CreateConnectionCode(TransferSession session) => ConnectionCode.Encode(session);

    public TransferSession ParseConnectionCode(string code, DateTimeOffset now) =>
        ConnectionCode.Decode(code, now);

    public string CreatePin(byte[] oneTimeToken) => ConnectionCode.DerivePin(oneTimeToken);
}
