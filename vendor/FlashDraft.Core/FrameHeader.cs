using System.Buffers.Binary;

namespace FlashDraft.Core;

/// <summary>
/// Fixed-size frame header. The whole header is used as AES-GCM AAD, so any
/// modification of metadata invalidates the authentication tag.
/// Layout: magic(4) version(1) type(1) flags(1) reserved(1) session(16)
/// fileIndex(4) chunkIndex(4) payloadLength(4) reserved2(4) chunkHash(32).
/// </summary>
public sealed class FrameHeader
{
    public byte Version { get; init; } = ProtocolConstants.ProtocolVersion;
    public FrameType Type { get; init; } = FrameType.Data;
    public byte Flags { get; init; }
    public Guid SessionId { get; init; }
    public uint FileIndex { get; init; }
    public uint ChunkIndex { get; init; }
    public uint PayloadLength { get; init; }
    public byte[] ChunkHash { get; init; } = new byte[ProtocolConstants.Sha256Size];

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < ProtocolConstants.FrameHeaderSize)
        {
            throw new ArgumentException("Header buffer is too small.", nameof(destination));
        }

        if (ChunkHash.Length != ProtocolConstants.Sha256Size)
        {
            throw new InvalidOperationException("Chunk hash must be SHA-256 sized.");
        }

        destination.Slice(0, ProtocolConstants.FrameHeaderSize).Clear();
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(0, 4), ProtocolConstants.Magic);
        destination[4] = Version;
        destination[5] = (byte)Type;
        destination[6] = Flags;
        destination[7] = 0;
        SessionId.TryWriteBytes(destination.Slice(8, ProtocolConstants.SessionIdSize));
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(24, 4), FileIndex);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(28, 4), ChunkIndex);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(32, 4), PayloadLength);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(36, 4), 0);
        ChunkHash.CopyTo(destination.Slice(40, ProtocolConstants.Sha256Size));
    }

    public static FrameHeader Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < ProtocolConstants.FrameHeaderSize)
        {
            throw new InvalidDataException("Truncated frame header.");
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(source.Slice(0, 4)) != ProtocolConstants.Magic)
        {
            throw new InvalidDataException("Invalid frame magic.");
        }

        byte version = source[4];
        if (version != ProtocolConstants.ProtocolVersion)
        {
            throw new InvalidDataException($"Unsupported protocol version {version}.");
        }

        return new FrameHeader
        {
            Version = version,
            Type = (FrameType)source[5],
            Flags = source[6],
            SessionId = new Guid(source.Slice(8, ProtocolConstants.SessionIdSize)),
            FileIndex = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(24, 4)),
            ChunkIndex = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(28, 4)),
            PayloadLength = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(32, 4)),
            ChunkHash = source.Slice(40, ProtocolConstants.Sha256Size).ToArray(),
        };
    }
}
