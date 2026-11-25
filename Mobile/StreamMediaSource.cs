using Android.Media;
using OperationCanceledException = System.OperationCanceledException;
using Stream = System.IO.Stream;
using Android.OS;
using Microsoft.Win32.SafeHandles;

namespace SecureVideo.Mobile;

internal sealed class DocumentFile : IDisposable
{
    private readonly ParcelFileDescriptor descriptor;
    public FileStream Stream { get; }

    public DocumentFile(Android.Content.ContentResolver resolver, Android.Net.Uri uri)
    {
        descriptor = resolver.OpenFileDescriptor(uri, "r") ?? throw new IOException("无法打开文件。");
        try
        {
            Stream = new FileStream(new SafeFileHandle((nint)descriptor.Fd, ownsHandle: false), FileAccess.Read);
            if (!Stream.CanSeek)
            {
                Stream.Dispose();
                throw new IOException("此文件来源不支持定位，请从手机本地存储选择文件。");
            }
        }
        catch { descriptor.Dispose(); throw; }
    }

    public void Dispose()
    {
        Stream.Dispose();
        descriptor.Dispose();
    }
}

internal sealed class StreamMediaSource(Stream input, DocumentFile file) : MediaDataSource
{
    private readonly object gate = new();
    private bool closed;

    public override long Size => input.Length;

    public override int ReadAt(long position, byte[]? buffer, int offset, int size)
    {
        if (buffer is null) return -1;
        lock (gate)
        {
            if (closed) return -1;
            try
            {
                input.Position = position;
                int read = input.Read(buffer, offset, size);
                return read == 0 && size > 0 ? -1 : read;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or OperationCanceledException or System.Security.Cryptography.CryptographicException)
            {
                throw new Java.IO.IOException(error.Message);
            }
        }
    }

    public override void Close()
    {
        lock (gate)
        {
            if (closed) return;
            closed = true;
            try { input.Dispose(); }
            finally { file.Dispose(); }
        }
    }
}
