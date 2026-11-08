using System.Security.Cryptography;
using FlashDraft.Core;
using FlashDraft.Crypto;

namespace FlashDraft.Transport;

/// <summary>
/// Binds two manually exchanged offers into a shared session. Both peers derive
/// the same session id and directional keys from their own private key and the
/// peer's public key. No private key ever leaves the local process.
/// </summary>
public static class SecureSession
{
    public static Guid DeriveSessionId(SessionOffer a, SessionOffer b)
    {
        byte[] left = a.SessionId.ToByteArray();
        byte[] right = b.SessionId.ToByteArray();
        byte[] first = left;
        byte[] second = right;
        if (left.AsSpan().SequenceCompareTo(right) > 0)
        {
            first = right;
            second = left;
        }

        byte[] material = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, material, 0, first.Length);
        Buffer.BlockCopy(second, 0, material, first.Length, second.Length);
        return new Guid(SHA256.HashData(material).AsSpan(0, 16));
    }

    public static SessionKeys Bind(
        EphemeralKeyPair localKeys,
        SessionOffer localOffer,
        SessionOffer remoteOffer,
        out Guid sessionId)
    {
        sessionId = DeriveSessionId(localOffer, remoteOffer);
        byte[] shared = localKeys.CalculateSharedSecret(remoteOffer.OffererPublicKey);
        bool isInitiator = localOffer.OffererPublicKey.AsSpan()
            .SequenceCompareTo(remoteOffer.OffererPublicKey) < 0;
        return KeyDerivation.Derive(
            shared, sessionId, localOffer.OffererPublicKey,
            remoteOffer.OffererPublicKey, isInitiator);
    }
}
