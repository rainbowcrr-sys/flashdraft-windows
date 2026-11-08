using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using FlashDraft.Core;
using FlashDraft.Crypto;

namespace FlashDraft.Transport;

/// <summary>
/// LAN transfer over TCP. Files are queued serially; chunks inside one file are
/// encrypted and written by parallel workers, guarded by a single stream writer.
/// Control frames (Offer, Complete, Cancel) are plaintext; Data frames are
/// AES-GCM sealed with the frame header as AAD.
/// </summary>
public sealed class LanTcpTransferService : ITransferService
{
    /// <summary>Adjustable: number of parallel chunk workers inside one file.</summary>
    private const int ChunkWorkers = 4;

    private readonly SessionKeys _keys;
    private readonly int _listenPort;

    public LanTcpTransferService(SessionKeys keys, int? listenPort = null)
    {
        _keys = keys;
        _listenPort = listenPort ?? ProtocolConstants.MulticastPort + 1;
    }

    public async Task SendAsync(
        TransferSession session,
        IReadOnlyList<FileDescriptor> files,
        IReadOnlyList<string> sourceRootPaths,
        string remoteEndpoint,
        IProgress<ProgressSnapshot> progress,
        CancellationToken cancellationToken)
    {
        (string host, int port) = ParseEndpoint(remoteEndpoint);
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        using var writeGate = new SemaphoreSlim(1, 1);
        var throughput = new SlidingThroughput();

        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileDescriptor file = files[i];
            string sourcePath = sourceRootPaths[i];

            byte[] offer = JsonSerializer.SerializeToUtf8Bytes(file);
            await WriteFrameAsync(
                stream,
                new FrameHeader
                {
                    Type = FrameType.Offer,
                    SessionId = session.SessionId,
                    FileIndex = file.FileIndex,
                    PayloadLength = (uint)offer.Length,
                },
                offer,
                writeGate,
                cancellationToken).ConfigureAwait(false);

            using var handle = File.OpenHandle(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = ChunkWorkers,
                CancellationToken = cancellationToken,
            };

            await Parallel.ForEachAsync(
                Enumerable.Range(0, (int)file.TotalChunks),
                options,
                async (chunkIndex, ct) =>
                {
                    long offset = (long)chunkIndex * file.ChunkSize;
                    int length = (int)Math.Min(file.ChunkSize, file.Length - offset);
                    byte[] plain = new byte[length];
                    RandomAccess.Read(handle, plain, offset);

                    byte[] hash = SHA256.HashData(plain);
                    byte[] nonce = NonceBuilder.Build(_keys.SessionPrefix, file.FileIndex, (uint)chunkIndex);
                    var header = new FrameHeader
                    {
                        Type = FrameType.Data,
                        SessionId = session.SessionId,
                        FileIndex = file.FileIndex,
                        ChunkIndex = (uint)chunkIndex,
                        PayloadLength = (uint)(length + ProtocolConstants.GcmTagSize),
                        ChunkHash = hash,
                    };

                    byte[] head = new byte[ProtocolConstants.FrameHeaderSize];
                    header.WriteTo(head);
                    byte[] cipher = AesGcmChunkCipher.Seal(_keys.SendKey, nonce, plain, head);

                    await WriteFrameAsync(stream, header, cipher, writeGate, ct).ConfigureAwait(false);
                    double speed = throughput.Record(length);
                    progress.Report(new ProgressSnapshot(
                        file.FileIndex,
                        file.RelativePath,
                        Math.Min(offset + length, file.Length),
                        file.Length,
                        speed,
                        TransferState.Transferring));
                }).ConfigureAwait(false);

            await WriteFrameAsync(
                stream,
                new FrameHeader
                {
                    Type = FrameType.Complete,
                    SessionId = session.SessionId,
                    FileIndex = file.FileIndex,
                },
                Array.Empty<byte>(),
                writeGate,
                cancellationToken).ConfigureAwait(false);

            progress.Report(new ProgressSnapshot(
                file.FileIndex, file.RelativePath, file.Length, file.Length, 0, TransferState.Completed));
        }
    }

    public async Task ReceiveAsync(
        TransferSession session,
        string destinationDirectory,
        IProgress<ProgressSnapshot> progress,
        CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, _listenPort);
        listener.Start();
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();
            ResumeWriter? writer = null;
            FileDescriptor? current = null;
            var throughput = new SlidingThroughput();

            while (true)
            {
                (FrameHeader Header, byte[] Payload)? frame =
                    await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                FrameHeader header = frame.Value.Header;
                byte[] payload = frame.Value.Payload;

                switch (header.Type)
                {
                    case FrameType.Offer:
                        current = JsonSerializer.Deserialize<FileDescriptor>(payload)
                                  ?? throw new InvalidDataException("Invalid file offer.");
                        writer?.Dispose();
                        writer = ResumeWriter.Create(destinationDirectory, current);
                        progress.Report(new ProgressSnapshot(
                            current.FileIndex, current.RelativePath, 0, current.Length, 0, TransferState.Transferring));
                        break;

                    case FrameType.Data:
                        if (writer is null || current is null)
                        {
                            throw new InvalidDataException("Data frame before file offer.");
                        }

                        byte[] head = new byte[ProtocolConstants.FrameHeaderSize];
                        header.WriteTo(head);
                        byte[] nonce = NonceBuilder.Build(_keys.SessionPrefix, header.FileIndex, header.ChunkIndex);
                        byte[] plain = AesGcmChunkCipher.Open(_keys.ReceiveKey, nonce, payload, head);
                        if (!SHA256.HashData(plain).AsSpan().SequenceEqual(header.ChunkHash))
                        {
                            throw new InvalidDataException("Chunk hash mismatch.");
                        }

                        writer.WriteChunk(header.ChunkIndex, plain);
                        writer.SaveMeta();
                        double speed = throughput.Record(plain.Length);
                        progress.Report(new ProgressSnapshot(
                            current.FileIndex,
                            current.RelativePath,
                            writer.BytesReceived,
                            current.Length,
                            speed,
                            TransferState.Transferring));
                        break;

                    case FrameType.Complete:
                        if (writer is null || current is null)
                        {
                            break;
                        }

                        if (!writer.Commit())
                        {
                            throw new InvalidDataException("Final file verification failed.");
                        }

                        progress.Report(new ProgressSnapshot(
                            current.FileIndex, current.RelativePath, current.Length, current.Length, 0, TransferState.Completed));
                        writer.Dispose();
                        writer = null;
                        break;

                    case FrameType.Cancel:
                        writer?.Dispose();
                        writer = null;
                        progress.Report(new ProgressSnapshot(
                            current?.FileIndex ?? 0,
                            current?.RelativePath ?? string.Empty,
                            0, 0, 0, TransferState.Cancelled));
                        break;

                    default:
                        break;
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WriteFrameAsync(
        NetworkStream stream,
        FrameHeader header,
        byte[] payload,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        byte[] head = new byte[ProtocolConstants.FrameHeaderSize];
        header.WriteTo(head);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(head.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (payload.Length > 0)
            {
                await stream.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<(FrameHeader Header, byte[] Payload)?> ReadFrameAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        byte[] head = new byte[ProtocolConstants.FrameHeaderSize];
        if (!await ReadExactlyAsync(stream, head, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        FrameHeader header = FrameHeader.Read(head);
        int max = ProtocolConstants.ChunkSize + ProtocolConstants.GcmTagSize + 4096;
        if (header.PayloadLength > max)
        {
            throw new InvalidDataException("Frame payload exceeds the allowed size.");
        }

        byte[] payload = new byte[header.PayloadLength];
        if (payload.Length > 0 && !await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (header, payload);
    }

    private static async Task<bool> ReadExactlyAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        int separator = endpoint.LastIndexOf(':');
        if (separator < 0)
        {
            throw new FormatException("Endpoint must be host:port.");
        }

        return (endpoint.Substring(0, separator), int.Parse(endpoint.Substring(separator + 1)));
    }
}
