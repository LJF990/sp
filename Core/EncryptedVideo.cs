using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SecureVideo;

public enum VideoFormat : byte { Mp4 = 1, Mkv = 2 }

public sealed record VideoHeader(Guid MediaId, VideoFormat Format, long Length, int BlockSize)
{
    public const int Size = 40;
    public const int TagSize = 16;
    public const int DefaultBlockSize = 256 * 1024;
    internal static ReadOnlySpan<byte> Magic => "SVIDEO01"u8;
    internal long Blocks => (Length - 1) / BlockSize + 1;
    internal long PhysicalLength => checked(Size + TagSize + Length + Blocks * TagSize);

    public static VideoHeader Read(Stream input)
    {
        Span<byte> bytes = stackalloc byte[Size];
        input.ReadExactly(bytes);
        return Parse(bytes, input.Length);
    }

    public static VideoHeader? ReadIfEncrypted(Stream input)
    {
        long position = input.Position;
        Span<byte> bytes = stackalloc byte[Size];
        try
        {
            input.Position = 0;
            var signature = bytes[..Magic.Length];
            if (input.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false) != signature.Length ||
                !signature.SequenceEqual(Magic))
                return null;

            input.ReadExactly(bytes[signature.Length..]);
            return Parse(bytes, input.Length);
        }
        finally { input.Position = position; }
    }

    internal static VideoHeader Parse(ReadOnlySpan<byte> bytes, long fileLength)
    {
        var format = (VideoFormat)bytes[24];
        var length = BinaryPrimitives.ReadInt64LittleEndian(bytes[28..]);
        var blockSize = BinaryPrimitives.ReadInt32LittleEndian(bytes[36..]);
        var id = new Guid(bytes[8..24]);
        if (!bytes[..8].SequenceEqual(Magic) || bytes[25] != 0 || bytes[26] != 0 || bytes[27] != 0 ||
            format is not (VideoFormat.Mp4 or VideoFormat.Mkv) || id == Guid.Empty || length <= 0 ||
            blockSize != DefaultBlockSize)
            throw new InvalidDataException("加密视频文件头无效。");

        var header = new VideoHeader(id, format, length, blockSize);
        if (length > long.MaxValue / 2 || header.PhysicalLength != fileLength)
            throw new InvalidDataException("加密视频文件长度无效。");
        return header;
    }

    internal byte[] Encode()
    {
        var bytes = new byte[Size];
        Magic.CopyTo(bytes);
        MediaId.TryWriteBytes(bytes.AsSpan(8));
        bytes[24] = (byte)Format;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(28), Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36), BlockSize);
        return bytes;
    }

    internal static void Nonce(Span<byte> nonce, long slot)
    {
        nonce.Clear();
        BinaryPrimitives.WriteInt64BigEndian(nonce[4..], slot);
    }
}

public static class LocalVideo
{
    public static VideoFormat GetFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" => VideoFormat.Mp4,
        ".mkv" => VideoFormat.Mkv,
        _ => throw new InvalidDataException("请选择 MP4 或 MKV 视频。")
    };

    public static VideoFormat Detect(Stream source, string path)
    {
        var expected = GetFormat(path);
        var actual = Detect(source);
        if (actual != expected) throw new InvalidDataException("视频内容与文件格式不符。");
        return actual;
    }

    public static VideoFormat Detect(Stream source)
    {
        long position = source.Position;
        Span<byte> signature = stackalloc byte[8];
        try
        {
            source.Position = 0;
            if (source.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false) == signature.Length)
            {
                if (BinaryPrimitives.ReadUInt32BigEndian(signature) == 0x1a45dfa3) return VideoFormat.Mkv;
                if (signature[4..].SequenceEqual("ftyp"u8)) return VideoFormat.Mp4;
            }
            throw new InvalidDataException("无法识别视频内容，请选择有效的 MP4、MKV 或加密视频。");
        }
        finally { source.Position = position; }
    }
}

public static class EncryptedVideoWriter
{
    // One freshly generated key per output. Nonce 0 authenticates the header, subsequent nonces are block positions.
    public static void Write(Stream source, Stream output, Guid id, VideoFormat format, byte[] newKey, CancellationToken cancellation = default)
    {
        if (source.Length <= 0 || source.Length > long.MaxValue / 2 || newKey.Length != 32 || id == Guid.Empty)
            throw new ArgumentException("视频长度、媒体 ID 或密钥无效。");
        var header = new VideoHeader(id, format, source.Length, VideoHeader.DefaultBlockSize);
        var encoded = header.Encode();
        using var aes = new AesGcm(newKey, VideoHeader.TagSize);
        var plain = new byte[header.BlockSize];
        var cipher = new byte[header.BlockSize];
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> tag = stackalloc byte[VideoHeader.TagSize];
        try
        {
            VideoHeader.Nonce(nonce, 0);
            aes.Encrypt(nonce, [], [], tag, encoded);
            output.Write(encoded);
            output.Write(tag);
            for (long block = 0; block < header.Blocks; block++)
            {
                cancellation.ThrowIfCancellationRequested();
                int size = (int)Math.Min(header.BlockSize, header.Length - block * header.BlockSize);
                source.ReadExactly(plain.AsSpan(0, size));
                VideoHeader.Nonce(nonce, block + 1);
                aes.Encrypt(nonce, plain.AsSpan(0, size), cipher.AsSpan(0, size), tag, encoded);
                output.Write(cipher.AsSpan(0, size));
                output.Write(tag);
            }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

public sealed class EncryptedVideoReader : Stream
{
    private readonly Stream input;
    private readonly AesGcm aes;
    private readonly byte[] encoded = new byte[VideoHeader.Size];
    private readonly byte[] plain;
    private readonly byte[] cipher;
    private long cachedBlock = -1;
    private long position;
    private bool disposed;

    public VideoHeader Header { get; }

    public EncryptedVideoReader(Stream input, byte[] key, Guid expectedMedia)
    {
        this.input = input;
        input.Position = 0;
        input.ReadExactly(encoded);
        Header = VideoHeader.Parse(encoded, input.Length);
        if (Header.MediaId != expectedMedia || key.Length != 32)
            throw new CryptographicException("视频和播放许可不匹配。");
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> tag = stackalloc byte[VideoHeader.TagSize];
        input.ReadExactly(tag);
        VideoHeader.Nonce(nonce, 0);
        aes = new AesGcm(key, VideoHeader.TagSize);
        try { aes.Decrypt(nonce, [], tag, [], encoded); }
        catch { aes.Dispose(); throw; }
        plain = new byte[Header.BlockSize];
        cipher = new byte[Header.BlockSize];
    }

    public override int Read(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int requested = (int)Math.Min(destination.Length, Math.Max(0, Length - position));
        int copied = 0;
        while (copied < requested)
        {
            long block = position / Header.BlockSize;
            int offset = (int)(position % Header.BlockSize);
            int count = Math.Min(requested - copied, Header.BlockSize - offset);
            if (block != cachedBlock) ReadBlock(block);
            plain.AsSpan(offset, count).CopyTo(destination[copied..]);
            position += count;
            copied += count;
        }
        return copied;
    }

    private void ReadBlock(long block)
    {
        cachedBlock = -1;
        int size = (int)Math.Min(Header.BlockSize, Length - block * Header.BlockSize);
        input.Position = VideoHeader.Size + VideoHeader.TagSize + block * (Header.BlockSize + VideoHeader.TagSize);
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> tag = stackalloc byte[VideoHeader.TagSize];
        input.ReadExactly(cipher.AsSpan(0, size));
        input.ReadExactly(tag);
        VideoHeader.Nonce(nonce, block + 1);
        aes.Decrypt(nonce, cipher.AsSpan(0, size), tag, plain.AsSpan(0, size), encoded);
        cachedBlock = block;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long target = checked(offset + (origin switch
        {
            SeekOrigin.Begin => 0, SeekOrigin.Current => position, SeekOrigin.End => Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        }));
        if (target < 0) throw new IOException("无效的视频偏移。");
        return position = target;
    }

    public override bool CanRead => !disposed;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => false;
    public override long Length => Header.Length;
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!disposed && disposing)
        {
            disposed = true;
            CryptographicOperations.ZeroMemory(plain);
            aes.Dispose();
            input.Dispose();
        }
        base.Dispose(disposing);
    }
}
