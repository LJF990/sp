using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using static FFmpeg.AutoGen.ffmpeg;

namespace SecureVideo.Player;

internal sealed unsafe class FfmpegInput : IDisposable
{
    private const int EIO_CODE = 5; // errno.h in the Windows FFmpeg build
    private readonly Stream stream;
    private readonly avio_alloc_context_read_packet read;
    private readonly avio_alloc_context_seek seek;
    private readonly CancellationToken cancellation;
    private AVIOContext* io;
    private AVFormatContext* format;
    private bool opened;
    private Exception? streamError;
    public AVFormatContext* Format => format;

    public FfmpegInput(Stream stream, VideoFormat videoFormat, CancellationToken cancellation)
    {
        this.stream = stream;
        this.cancellation = cancellation;
        read = Read;
        seek = Seek;
        try
        {
            const int size = 64 * 1024;
            var buffer = (byte*)av_malloc(size);
            if (buffer == null) throw new OutOfMemoryException();
            io = avio_alloc_context(buffer, size, 0, null, read, default, seek);
            if (io == null)
            {
                av_free(buffer);
                throw new OutOfMemoryException();
            }

            format = avformat_alloc_context();
            if (format == null) throw new OutOfMemoryException();
            format->pb = io;
            format->flags |= AVFMT_FLAG_CUSTOM_IO;
            var demuxer = av_find_input_format(videoFormat == VideoFormat.Mp4 ? "mov" : "matroska");
            if (demuxer == null) throw new InvalidOperationException("FFmpeg 缺少 MP4 或 MKV 解复用器。");

            AVDictionary* options = null;
            try
            {
                Check(av_dict_set(&options, "protocol_whitelist", "securevideo", 0));
                Check(av_dict_set(&options, "enable_drefs", "0", 0));
                var context = format;
                var result = avformat_open_input(&context, "", demuxer, &options);
                format = context;
                opened = result >= 0;
                Check(result);
            }
            finally { av_dict_free(&options); }
            Check(avformat_find_stream_info(format, null));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private int Read(void* opaque, byte* buffer, int size)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            int count = stream.Read(new Span<byte>(buffer, size));
            return count == 0 ? AVERROR_EOF : count;
        }
        catch (Exception ex)
        {
            streamError = ex;
            return AVERROR(EIO_CODE);
        }
    }

    private long Seek(void* opaque, long offset, int flags)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if ((flags & AVSEEK_SIZE) != 0) return stream.Length;
            int origin = flags & ~AVSEEK_FORCE;
            return origin is >= 0 and <= 2 ? stream.Seek(offset, (SeekOrigin)origin) : AVERROR(EINVAL);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException)
        {
            return AVERROR(EINVAL);
        }
        catch (Exception ex)
        {
            streamError = ex;
            return AVERROR(EIO_CODE);
        }
    }

    public void Check(int code)
    {
        cancellation.ThrowIfCancellationRequested();
        if (streamError is not null) throw new InvalidDataException("视频数据读取失败或验证未通过。", streamError);
        if (code < 0)
        {
            byte* error = stackalloc byte[256];
            av_strerror(code, error, 256);
            throw new InvalidDataException("FFmpeg：" + Marshal.PtrToStringUTF8((nint)error));
        }
    }

    public void Dispose()
    {
        if (format != null)
        {
            var context = format;
            if (opened) avformat_close_input(&context);
            else avformat_free_context(context);
            format = null;
        }
        if (io != null)
        {
            var context = io;
            if (context->buffer != null)
            {
                if (context->buffer_size > 0) new Span<byte>(context->buffer, context->buffer_size).Clear();
                av_free(context->buffer);
                context->buffer = null;
            }
            avio_context_free(&context);
            io = null;
        }
        GC.KeepAlive(read);
        GC.KeepAlive(seek);
    }
}
