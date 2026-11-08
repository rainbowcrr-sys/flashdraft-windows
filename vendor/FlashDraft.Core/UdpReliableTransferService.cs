using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using FlashDraft.Core;
using FlashDraft.Crypto;
using Microsoft.Win32.SafeHandles;

namespace FlashDraft.Transport;

/// <summary>
/// Reliable encrypted file transfer over an already punched UDP socket.
/// One datagram carries one frame header plus one AES-GCM sealed chunk.
/// Stop-and-wait ACKs with bounded retransmission provide reliability.
/// </summary>
public sealed class UdpReliableTransferService
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _remote;
    private readonly SessionKeys _keys;
    private readonly Guid _sessionId;
    private readonly SlidingThroughput _throughput = new();

    public UdpReliableTransferService(
        UdpClient client, IPEndPoint remote, SessionKeys keys, Guid sessionId)
    {
        _client = client;
        _remote = remote;
        _keys = keys;
        _sessionId = sessionId;
    }

    public async Task SendAsync(
        IReadOnlyList<FileDescriptor> files,
        IReadOnlyList<string> sourcePaths,
        IProgress<ProgressSnapshot> progress,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileDescriptor file = files[i];
            byte[] offer = JsonSerializer.SerializeToUtf8Bytes(file);
            await SendWithAckAsync(
                new FrameHeader
                {
                    Type = FrameType.Offer,
                    SessionId = _sessionId,
                    FileIndex = file.FileIndex,
                    PayloadLength = (uint)offer.Length,
                },
                offer,
                cancellationToken).ConfigureAwait(false);

            using SafeFileHandle handle = File.OpenHandle(
                sourcePaths[i], FileMode.Open, FileAccess.Read, FileShare.Read);
            for (uint chunk = 0; chunk < file.TotalChunks; chunk++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long offset = (long)chunk * file.ChunkSize;
                int length = (int)Math.Min(file.ChunkSize, file.Length - offset);
                byte[] plain = new byte[length];
                RandomAccess.Read(handle, plain, offset);

                byte[] nonce = NonceBuilder.Build(_keys.SessionPrefix, file.FileIndex, chunk);
                var header = new FrameHeader
                {
                    Type = FrameType.Data,
                    SessionId = _sessionId,
                    FileIndex = file.FileIndex,
                    ChunkIndex = chunk,
                    PayloadLength = (uint)(length + ProtocolConstants.GcmTagSize),
                    ChunkHash = SHA256.HashData(plain),
                };

                byte[] head = new byte[ProtocolConstants.FrameHeaderSize];
                header.WriteTo(head);
                byte[] cipher = AesGcmChunkCipher.Seal(_keys.SendKey, nonce, plain, head);
                await SendWithAckAsync(header, cipher, cancellationToken).ConfigureAwait(false);

                double speed = _throughput.Record(length);
                progress.Report(new ProgressSnapshot(
                    file.FileIndex, file.RelativePath,
                    Math.Min(offset + length, file.Length), file.Length,
                    speed, TransferState.Transferring));
            }

            await SendWithAckAsync(
                new FrameHeader
                {
                    Type = FrameType.Complete,
                    SessionId = _sessionId,
                    FileIndex = file.FileIndex,
                },
                Array.Empty<byte>(),
                cancellationToken).ConfigureAwait(false);

            progress.Report(new ProgressSnapshot(
                file.FileIndex, file.RelativePath, file.Length, file.Length,
                0, TransferState.Completed));
        }
    }

    public async Task ReceiveAsync(
        string destinationDirectory,
        IProgress<ProgressSnapshot> progress,
        CancellationToken cancellationToken)
    {
        ResumeWriter? writer = null;
        FileDescriptor? current = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result = await _client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (result.Buffer.Length < ProtocolConstants.FrameHeaderSize)
            {
                continue;
            }

            FrameHeader header = FrameHeader.Read(result.Buffer.AsSpan(0, ProtocolConstants.FrameHeaderSize));
            if (header.SessionId != _sessionId)
            {
                continue;
            }

            byte[] payload = result.Buffer.AsSpan(ProtocolConstants.FrameHeaderSize).ToArray();
            switch (header.Type)
            {
                case FrameType.Offer:
                    current = JsonSerializer.Deserialize<FileDescriptor>(payload)
                              ?? throw new InvalidDataException("Invalid file offer.");
                    writer?.Dispose();
                    writer = ResumeWriter.Create(destinationDirectory, current);
                    await SendAckAsync(header, cancellationToken).ConfigureAwait(false);
                    break;

                case FrameType.Data:
                    if (writer is null || current is null)
                    {
                        break;
                    }

                    byte[] head = new byte[ProtocolConstants.FrameHeaderSize];
                    header.WriteTo(head);
                    byte[] nonce = NonceBuilder.Build(
                        _keys.SessionPrefix, header.FileIndex, header.ChunkIndex);
                    byte[] plain = AesGcmChunkCipher.Open(_keys.ReceiveKey, nonce, payload, head);
                    if (!SHA256.HashData(plain).AsSpan().SequenceEqual(header.ChunkHash))
                    {
                        throw new InvalidDataException("Chunk hash mismatch.");
                    }

                    writer.WriteChunk(header.ChunkIndex, plain);
                    writer.SaveMeta();
                    await SendAckAsync(header, cancellationToken).ConfigureAwait(false);
                    double speed = _throughput.Record(plain.Length);
                    progress.Report(new ProgressSnapshot(
                        current.FileIndex, current.RelativePath, writer.BytesReceived,
                        current.Length, speed, TransferState.Transferring));
                    break;

                case FrameType.Complete:
                    if (writer is not null && writer.Commit())
                    {
                        progress.Report(new ProgressSnapshot(
                            current?.FileIndex ?? 0, current?.RelativePath ?? string.Empty,
                            current?.Length ?? 0, current?.Length ?? 0,
                            0, TransferState.Completed));
                    }

                    await SendAckAsync(header, cancellationToken).ConfigureAwait(false);
                    writer?.Dispose();
                    writer = null;
                    return;

                case FrameType.Cancel:
                    writer?.Dispose();
                    writer = null;
                    return;

                default:
                    break;
            }
        }
    }

    private async Task SendWithAckAsync(
        FrameHeader header, byte[] payload, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < ProtocolConstants.UdpMaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SendFrameAsync(header, payload, cancellationToken).ConfigureAwait(false);
            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(ProtocolConstants.UdpAckTimeoutMs);
            try
            {
                while (true)
                {
                    UdpReceiveResult reply = await _client.ReceiveAsync(window.Token).ConfigureAwait(false);
                    if (reply.Buffer.Length < ProtocolConstants.FrameHeaderSize)
                    {
                        continue;
                    }

                    FrameHeader ack = FrameHeader.Read(reply.Buffer.AsSpan(0, ProtocolConstants.FrameHeaderSize));
                    if (ack.Type == FrameType.Ack &&
                        ack.FileIndex == header.FileIndex &&
                        ack.ChunkIndex == header.ChunkIndex)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Retransmit the same frame.
            }
        }

        throw new TimeoutException("Peer did not acknowledge a UDP frame.");
    }

    private async Task SendAckAsync(FrameHeader original, CancellationToken cancellationToken)
    {
        var ack = new FrameHeader
        {
            Type = FrameType.Ack,
            SessionId = _sessionId,
            FileIndex = original.FileIndex,
            ChunkIndex = original.ChunkIndex,
        };
        await SendFrameAsync(ack, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendFrameAsync(
        FrameHeader header, byte[] payload, CancellationToken cancellationToken)
    {
        byte[] datagram = new byte[ProtocolConstants.FrameHeaderSize + payload.Length];
        header.WriteTo(datagram.AsSpan(0, ProtocolConstants.FrameHeaderSize));
        payload.CopyTo(datagram, ProtocolConstants.FrameHeaderSize);
        await _client.SendAsync(datagram, datagram.Length, _remote).ConfigureAwait(false);
    }
}
