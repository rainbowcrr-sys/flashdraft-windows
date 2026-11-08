using System.Buffers.Binary;
using System.Security.Cryptography;
using FlashDraft.Core;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace FlashDraft.Crypto;

/// <summary>
/// Ephemeral X25519 key pair. Entropy comes from the operating system CSPRNG
/// through SecureRandom; no custom entropy source is injected.
/// The private key never leaves this object and is discarded with the session.
/// </summary>
public sealed class EphemeralKeyPair
{
    private readonly X25519PrivateKeyParameters _privateKey;

    public EphemeralKeyPair()
    {
        _privateKey = new X25519PrivateKeyParameters(new SecureRandom());
        PublicKey = _privateKey.GeneratePublicKey().GetEncoded();
    }

    public byte[] PublicKey { get; }

    public byte[] CalculateSharedSecret(byte[] remotePublicKey)
    {
        var remote = new X25519PublicKeyParameters(remotePublicKey, 0);
        var agreement = new X25519Agreement();
        agreement.Init(_privateKey);
        byte[] shared = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(remote, shared, 0);
        return shared;
    }
}

/// <summary>Directional keys and the shared per-session nonce prefix.</summary>
public sealed record SessionKeys(byte[] SendKey, byte[] ReceiveKey, byte[] SessionPrefix);

public static class KeyDerivation
{
    /// <summary>
    /// Derives two ordered keys from the X25519 shared secret using HKDF-SHA256.
    /// The salt binds the session id and both public keys in a deterministic
    /// order, so both peers compute identical k1 and k2. The initiator sends
    /// with k1 and receives with k2; the responder does the opposite.
    /// The 4-byte nonce prefix is derived as well, so both peers build the
    /// same AES-GCM nonce for a given file and chunk index.
    /// </summary>
    public static SessionKeys Derive(
        byte[] sharedSecret,
        Guid sessionId,
        byte[] localPublicKey,
        byte[] remotePublicKey,
        bool isInitiator)
    {
        byte[] first = localPublicKey;
        byte[] second = remotePublicKey;
        if (localPublicKey.AsSpan().SequenceCompareTo(remotePublicKey) > 0)
        {
            first = remotePublicKey;
            second = localPublicKey;
        }

        byte[] sessionBytes = sessionId.ToByteArray();
        byte[] saltMaterial = new byte[sessionBytes.Length + first.Length + second.Length];
        Buffer.BlockCopy(sessionBytes, 0, saltMaterial, 0, sessionBytes.Length);
        Buffer.BlockCopy(first, 0, saltMaterial, sessionBytes.Length, first.Length);
        Buffer.BlockCopy(second, 0, saltMaterial, sessionBytes.Length + first.Length, second.Length);
        byte[] salt = SHA256.HashData(saltMaterial);

        byte[] k1 = new byte[ProtocolConstants.KeySize];
        byte[] k2 = new byte[ProtocolConstants.KeySize];
        byte[] prefix = new byte[4];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, k1, salt, "flashdraft/key1/v1"u8.ToArray());
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, k2, salt, "flashdraft/key2/v1"u8.ToArray());
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, prefix, salt, "flashdraft/nonce/v1"u8.ToArray());

        return isInitiator
            ? new SessionKeys(k1, k2, prefix)
            : new SessionKeys(k2, k1, prefix);
    }
}

/// <summary>
/// Builds a 96-bit AES-GCM nonce: 4-byte session prefix + 4-byte file index +
/// 4-byte chunk index. The nonce is reproducible for resumed chunks and unique
/// within a session.
/// </summary>
public static class NonceBuilder
{
    public static byte[] Build(ReadOnlySpan<byte> sessionPrefix, uint fileIndex, uint chunkIndex)
    {
        if (sessionPrefix.Length != 4)
        {
            throw new ArgumentException("Session prefix must be 4 bytes.", nameof(sessionPrefix));
        }

        byte[] nonce = new byte[ProtocolConstants.NonceSize];
        sessionPrefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(4, 4), fileIndex);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8, 4), chunkIndex);
        return nonce;
    }
}

/// <summary>AES-GCM chunk cipher. AAD is the plaintext frame header bytes.</summary>
public static class AesGcmChunkCipher
{
    public static byte[] Seal(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad)
    {
        byte[] output = new byte[plaintext.Length + ProtocolConstants.GcmTagSize];
        using var aes = new AesGcm(key, ProtocolConstants.GcmTagSize);
        aes.Encrypt(
            nonce,
            plaintext,
            output.AsSpan(0, plaintext.Length),
            output.AsSpan(plaintext.Length, ProtocolConstants.GcmTagSize),
            aad);
        return output;
    }

    public static byte[] Open(byte[] key, byte[] nonce, byte[] ciphertext, byte[] aad)
    {
        int plainLength = ciphertext.Length - ProtocolConstants.GcmTagSize;
        if (plainLength < 0)
        {
            throw new InvalidDataException("Ciphertext is shorter than the GCM tag.");
        }

        byte[] plaintext = new byte[plainLength];
        using var aes = new AesGcm(key, ProtocolConstants.GcmTagSize);
        aes.Decrypt(
            nonce,
            ciphertext.AsSpan(0, plainLength),
            ciphertext.AsSpan(plainLength, ProtocolConstants.GcmTagSize),
            plaintext,
            aad);
        return plaintext;
    }

    public static byte[] HashChunk(byte[] plaintext) => SHA256.HashData(plaintext);
}
