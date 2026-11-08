using System.Security.Cryptography;
using System.Text.Json;
using FlashDraft.Core;
using Microsoft.Win32.SafeHandles;

namespace FlashDraft.Transport;

/// <summary>
/// Writes chunks into a sibling .part file at their final offsets and keeps a
/// .meta file with the received-chunk bitmap. The file is renamed only after the
/// full SHA-256 matches the descriptor.
/// </summary>
public sealed class ResumeWriter : IDisposable
{
    private sealed class Meta
    {
        public uint TotalChunks { get; set; }
        public string Bitmap { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public long Length { get; set; }
    }

    private readonly FileDescriptor _file;
    private readonly string _partPath;
    private readonly string _metaPath;
    private readonly string _finalPath;
    private readonly SafeFileHandle _handle;
    private readonly ChunkBitmap _bitmap;
    private long _bytesReceived;
    private bool _disposed;
    private bool _committed;

    private ResumeWriter(
        FileDescriptor file,
        string partPath,
        string metaPath,
        string finalPath,
        SafeFileHandle handle,
        ChunkBitmap bitmap,
        long bytesReceived)
    {
        _file = file;
        _partPath = partPath;
        _metaPath = metaPath;
        _finalPath = finalPath;
        _handle = handle;
        _bitmap = bitmap;
        _bytesReceived = bytesReceived;
    }

    public long BytesReceived => _bytesReceived;

    public static ResumeWriter Create(string destinationDirectory, FileDescriptor file)
    {
        Directory.CreateDirectory(destinationDirectory);
        string safeName = Path.GetFileName(file.RelativePath);
        string finalPath = Path.Combine(destinationDirectory, safeName);
        string partPath = finalPath + ".part";
        string metaPath = finalPath + ".meta";

        ChunkBitmap bitmap = new(file.TotalChunks);
        long bytesReceived = 0;
        if (File.Exists(metaPath) && File.Exists(partPath))
        {
            Meta? meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath));
            if (meta is not null &&
                meta.TotalChunks == file.TotalChunks &&
                meta.Sha256 == Convert.ToBase64String(file.Sha256))
            {
                bitmap = ChunkBitmap.FromBase64(file.TotalChunks, meta.Bitmap);
                for (uint i = 0; i < file.TotalChunks; i++)
                {
                    if (bitmap.IsSet(i))
                    {
                        long offset = (long)i * file.ChunkSize;
                        bytesReceived += Math.Min(file.ChunkSize, file.Length - offset);
                    }
                }
            }
        }

        SafeFileHandle handle = File.OpenHandle(
            partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        return new ResumeWriter(file, partPath, metaPath, finalPath, handle, bitmap, bytesReceived);
    }

    public void WriteChunk(uint chunkIndex, byte[] plaintext)
    {
        if (_bitmap.IsSet(chunkIndex))
        {
            return;
        }

        long offset = (long)chunkIndex * _file.ChunkSize;
        RandomAccess.Write(_handle, plaintext, offset);
        _bitmap.Set(chunkIndex);
        _bytesReceived += plaintext.Length;
    }

    public void SaveMeta()
    {
        if (_committed)
        {
            return;
        }

        var meta = new Meta
        {
            TotalChunks = _file.TotalChunks,
            Bitmap = _bitmap.ToBase64(),
            Sha256 = Convert.ToBase64String(_file.Sha256),
            RelativePath = _file.RelativePath,
            Length = _file.Length,
        };
        File.WriteAllText(_metaPath, JsonSerializer.Serialize(meta));
    }

    public bool Commit()
    {
        if (!_bitmap.IsComplete)
        {
            return false;
        }

        _handle.Dispose();
        byte[] digest;
        using (FileStream stream = File.OpenRead(_partPath))
        {
            digest = SHA256.HashData(stream);
        }

        if (!digest.AsSpan().SequenceEqual(_file.Sha256))
        {
            return false;
        }

        File.Move(_partPath, _finalPath, overwrite: true);
        File.Delete(_metaPath);
        _committed = true;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_committed)
        {
            SaveMeta();
        }

        _handle.Dispose();
    }
}
