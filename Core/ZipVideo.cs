using System.IO.Compression;
using System.Security.Cryptography;

namespace SecureVideo;

public sealed record ZipVideoEntry(int Index, string Name, long Length);

public static class ZipVideo
{
    public static IReadOnlyList<ZipVideoEntry> List(Stream input, CancellationToken cancellation = default)
    {
        RequireSeekable(input);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var videos = new List<ZipVideoEntry>();
        for (int i = 0; i < archive.Entries.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var entry = archive.Entries[i];
            if (IsVideo(entry.Name) && entry.Length > 0)
                videos.Add(new(i, entry.FullName, entry.Length));
        }
        return videos;
    }

    public static bool IsVideo(string path) =>
        Path.GetExtension(path).ToUpperInvariant() is ".MP4" or ".MKV" or ".SVIDEO";

    // The caller owns input. The returned stream owns only the ZIP reader.
    public static Stream Open(Stream input, ZipVideoEntry item, CancellationToken cancellation = default)
    {
        RequireSeekable(input);
        var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        try
        {
            if (item.Index < 0 || item.Index >= archive.Entries.Count)
                throw new InvalidDataException("压缩包内容已变更，请重新加载。");
            var entry = archive.Entries[item.Index];
            if (entry.FullName != item.Name || entry.Length != item.Length)
                throw new InvalidDataException("压缩包内容已变更，请重新加载。");
            return new EntryStream(archive, entry, cancellation);
        }
        catch { archive.Dispose(); throw; }
    }

    private static void RequireSeekable(Stream input)
    {
        // ZipArchive would otherwise copy the entire ZIP into memory.
        if (!input.CanSeek) throw new NotSupportedException("ZIP 必须从支持文件定位的本地来源打开。");
    }

    private sealed class EntryStream(ZipArchive archive, ZipArchiveEntry entry, CancellationToken cancellation) : Stream
    {
        private const int CacheSize = 1024 * 1024;
        private readonly byte[] cache = new byte[CacheSize];
        private Stream? compressed;
        private long decodedPosition;
        private long windowStart = -1;
        private int windowLength;
        private long position;
        private bool disposed;

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellation.ThrowIfCancellationRequested();
            if (position >= Length || buffer.IsEmpty) return 0;
            if (windowStart < 0 || position < windowStart || position - windowStart >= windowLength)
                LoadWindow(position / CacheSize * CacheSize);

            int offset = (int)(position - windowStart);
            int count = Math.Min(buffer.Length, windowLength - offset);
            cache.AsSpan(offset, count).CopyTo(buffer);
            position += count;
            return count;
        }

        private void LoadWindow(long start)
        {
            windowStart = -1;
            if (compressed is null || start < decodedPosition)
            {
                compressed?.Dispose();
                compressed = entry.Open();
                decodedPosition = 0;
            }

            // Deflate does not support direct random reads. Replay in bounded chunks for cold seeks.
            while (decodedPosition < start)
            {
                cancellation.ThrowIfCancellationRequested();
                int count = (int)Math.Min(CacheSize, start - decodedPosition);
                compressed.ReadExactly(cache.AsSpan(0, count));
                decodedPosition += count;
            }
            cancellation.ThrowIfCancellationRequested();
            windowLength = (int)Math.Min(CacheSize, Length - start);
            compressed.ReadExactly(cache.AsSpan(0, windowLength));
            decodedPosition += windowLength;
            windowStart = start;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            long next = checked(offset + (origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => position,
                SeekOrigin.End => Length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            }));
            if (next < 0) throw new IOException("无效的压缩包内偏移。");
            return position = next;
        }
        public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
        public override long Length => entry.Length;
        public override bool CanRead => !disposed;
        public override bool CanSeek => !disposed;
        public override bool CanWrite => false;
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                CryptographicOperations.ZeroMemory(cache);
                try { compressed?.Dispose(); }
                finally { archive.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }
}
