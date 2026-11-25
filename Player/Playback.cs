using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using FFmpeg.AutoGen;
using NAudio.Wave;
using static FFmpeg.AutoGen.ffmpeg;

namespace SecureVideo.Player;

internal sealed class VideoFrame(double time, int width, int height, byte[] pixels) : IDisposable
{
    public double Time { get; } = time;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;
    public void Dispose() => ArrayPool<byte>.Shared.Return(Pixels, clearArray: true);
}

internal sealed unsafe class Playback(double initialRate = 1)
{
    private readonly object control = new();
    private double? requestedSeek;
    private volatile bool paused;
    private volatile bool looping;
    private double rate = initialRate;
    private volatile float volume = 1;
    public double Rate { get => Volatile.Read(ref rate); set => Volatile.Write(ref rate, value); }
    public float Volume { get => volume; set => volume = value; }
    public bool Paused { get => paused; set => paused = value; }
    public bool Looping { get => looping; set => looping = value; }
    public void Seek(double seconds) { lock (control) requestedSeek = seconds; }

    public void Run(Stream reader, VideoFormat videoFormat, Action<VideoFrame, double, double> render,
        Action<string> report, CancellationToken ct)
    {
        using var input = new FfmpegInput(reader, videoFormat, ct);
        var format = input.Format;
        int vi = av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        input.Check(vi);
        int ai = av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, vi, null, 0);

        using var video = new Decoder(input, format->streams[vi]);
        using var audio = ai < 0 ? null : new Decoder(input, format->streams[ai]);
        using var scaler = new VideoScaler();
        using var sound = audio is null ? null : AudioOutput.TryOpen(report);
        var frames = new Queue<VideoFrame>();
        var clock = new Stopwatch();
        double start = format->start_time == AV_NOPTS_VALUE ? 0 : format->start_time / (double)AV_TIME_BASE;
        double duration = format->duration > 0 ? format->duration / (double)AV_TIME_BASE : 0;
        double target = 0;
        double activeRate = Rate;
        if (sound is not null) sound.Volume = volume;
        sound?.Reset(0, activeRate);
        double lastVideo = -1;
        bool eof = false;
        bool preview = true;
        AVPacket* packet = av_packet_alloc();
        if (packet == null) throw new OutOfMemoryException();

        double Now() => target + clock.Elapsed.TotalSeconds * activeRate;

        void DisplayVideo(AVFrame* decoded)
        {
            double timestamp = video.Timestamp(decoded, start, lastVideo < 0 ? target : lastVideo + 1.0 / 30);
            lastVideo = timestamp;
            if (timestamp < target - 0.001 || (!paused && !preview && timestamp + 0.12 < Now())) return;
            if (frames.Count < 12) frames.Enqueue(scaler.Convert(decoded, timestamp));
        }

        void PlayAudio(AVFrame* decoded)
        {
            if (sound is null || audio is null) return;
            double timestamp = audio.Timestamp(decoded, start, sound.NextTime);
            if (timestamp + decoded->nb_samples / (double)Math.Max(1, decoded->sample_rate) <= target) return;
            sound.Add(decoded, timestamp, Math.Max(target, Now()), input);
        }

        void Reset(double seconds)
        {
            target = Math.Clamp(seconds, 0, duration > 0 ? duration : Math.Max(0, seconds));
            ct.ThrowIfCancellationRequested();
            sound?.Reset(target, activeRate);
            while (frames.TryDequeue(out var pending)) pending.Dispose();
            clock.Reset();
            long stamp = checked((long)((target + start) * AV_TIME_BASE));
            input.Check(av_seek_frame(format, -1, stamp, AVSEEK_FLAG_BACKWARD));
            video.Flush();
            audio?.Flush();
            lastVideo = -1;
            preview = true;
            eof = false;
        }

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                double? seek;
                lock (control) { seek = requestedSeek; requestedSeek = null; }
                double currentRate = Rate;
                if (seek.HasValue || currentRate != activeRate)
                {
                    double position = seek ?? Now();
                    activeRate = currentRate;
                    Reset(position);
                }
                if (sound is not null) sound.Volume = volume;

                if (paused && clock.IsRunning)
                {
                    clock.Stop();
                    sound?.Pause();
                }
                if (!paused && !preview && !clock.IsRunning)
                {
                    clock.Start();
                    sound?.Play();
                }

                double now = Now();
                if (frames.TryPeek(out var next) && (preview || !paused && next.Time <= now + 0.012))
                {
                    using var image = frames.Dequeue();
                    render(image, preview ? Math.Max(target, image.Time) : now, duration);
                    preview = false;
                    continue;
                }

                if (eof && frames.Count == 0 && (sound is null || sound.Finished(now)))
                {
                    if (!paused && looping)
                    {
                        Reset(0);
                        continue;
                    }
                    return;
                }

                if ((paused && !preview) || eof || frames.Count >= 10 || (sound?.BufferedSeconds > 0.8 && frames.Count > 0))
                {
                    ct.WaitHandle.WaitOne(6);
                    continue;
                }

                int result = av_read_frame(format, packet);
                if (result == AVERROR_EOF)
                {
                    input.Check(0);
                    video.Decode(null, DisplayVideo);
                    if (sound is not null) audio?.Decode(null, PlayAudio);
                    sound?.Finish();
                    eof = true;
                    if (preview && frames.Count == 0) return;
                    continue;
                }
                input.Check(result);
                try
                {
                    if (packet->stream_index == vi) video.Decode(packet, DisplayVideo);
                    else if (packet->stream_index == ai && sound is not null) audio?.Decode(packet, PlayAudio);
                }
                finally { av_packet_unref(packet); }
            }
        }
        finally
        {
            while (frames.TryDequeue(out var frame)) frame.Dispose();
            av_packet_free(&packet);
        }
    }
}

internal sealed unsafe class Decoder : IDisposable
{
    public delegate void FrameHandler(AVFrame* frame);
    private readonly FfmpegInput input;
    private readonly AVStream* stream;
    private AVCodecContext* context;
    private AVFrame* frame;

    public Decoder(FfmpegInput input, AVStream* stream)
    {
        this.input = input;
        this.stream = stream;
        try
        {
            var codec = avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec == null) throw new InvalidDataException("FFmpeg 缺少该编码的解码器。");
            context = avcodec_alloc_context3(codec);
            frame = av_frame_alloc();
            if (frame == null || context == null) throw new OutOfMemoryException();
            input.Check(avcodec_parameters_to_context(context, stream->codecpar));
            context->max_pixels = 4096L * 2160;
            context->thread_count = 4;
            input.Check(avcodec_open2(context, codec, null));
        }
        catch { Dispose(); throw; }
    }

    public void Decode(AVPacket* packet, FrameHandler callback)
    {
        input.Check(avcodec_send_packet(context, packet));
        while (true)
        {
            int code = avcodec_receive_frame(context, frame);
            if (code == AVERROR(EAGAIN) || code == AVERROR_EOF) return;
            input.Check(code);
            try { callback(frame); }
            finally { av_frame_unref(frame); }
        }
    }

    public double Timestamp(AVFrame* decoded, double start, double fallback) =>
        decoded->best_effort_timestamp == AV_NOPTS_VALUE
            ? fallback : decoded->best_effort_timestamp * av_q2d(stream->time_base) - start;

    public void Flush() => avcodec_flush_buffers(context);

    public void Dispose()
    {
        var current = context;
        avcodec_free_context(&current);
        var decoded = frame;
        av_frame_free(&decoded);
        context = null;
        frame = null;
    }
}

internal sealed unsafe class VideoScaler : IDisposable
{
    private SwsContext* scaler;

    public VideoFrame Convert(AVFrame* frame, double timestamp)
    {
        int width = frame->width;
        int height = frame->height;
        if (width <= 0 || height <= 0 || (long)width * height > 4096L * 2160)
            throw new InvalidDataException("视频分辨率超出支持范围。");
        double factor = Math.Min(1, Math.Min(1920.0 / width, 1080.0 / height));
        int outputWidth = Math.Max(1, (int)(width * factor));
        int outputHeight = Math.Max(1, (int)(height * factor));
        scaler = sws_getCachedContext(scaler, width, height, (AVPixelFormat)frame->format, outputWidth,
            outputHeight, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (scaler == null) throw new InvalidDataException("无法转换视频画面。");

        int stride = (outputWidth * 4 + 31) & ~31;
        var aligned = ArrayPool<byte>.Shared.Rent(checked(stride * outputHeight + 64));
        try
        {
            fixed (byte* pointer = aligned)
            {
                var planes = new byte_ptrArray4();
                var strides = new int_array4();
                planes[0] = pointer;
                strides[0] = stride;
                if (sws_scale(scaler, frame->data, frame->linesize, 0, height, planes, strides) != outputHeight)
                    throw new InvalidDataException("画面转换不完整。");
            }
            if (stride != outputWidth * 4)
                for (int row = 1; row < outputHeight; row++)
                    aligned.AsSpan(row * stride, outputWidth * 4).CopyTo(aligned.AsSpan(row * outputWidth * 4));
            return new(timestamp, outputWidth, outputHeight, aligned);
        }
        catch { ArrayPool<byte>.Shared.Return(aligned, clearArray: true); throw; }
    }

    public void Dispose() => sws_freeContext(scaler);
}

internal sealed unsafe class AudioOutput : IDisposable
{
    private const int Rate = 48000;
    private const int FrameBytes = 4;
    private static readonly byte[] Silence = new byte[Rate * FrameBytes];
    private readonly WaveOutEvent output = new() { DesiredLatency = 90, NumberOfBuffers = 3 };
    private readonly AudioBuffer buffer = new(Rate * FrameBytes * 3);
    private SwrContext* resampler;
    private AVChannelLayout layout;
    private AVSampleFormat sampleFormat;
    private int sampleRate;
    private double rate = 1;
    private AudioTempo? tempo;
    public float Volume { set { if (output.Volume != value) output.Volume = value; } }
    public double NextTime { get; private set; }
    public double BufferedSeconds => buffer.Count / (double)(Rate * FrameBytes);

    private AudioOutput()
    {
        try { output.Init(buffer); }
        catch { output.Dispose(); throw; }
    }

    public static AudioOutput? TryOpen(Action<string> report)
    {
        try { return new AudioOutput(); }
        catch (NAudio.MmException) { report("没有可用的音频设备，正在静音播放。"); return null; }
    }

    public void Add(AVFrame* frame, double timestamp, double now, FfmpegInput input)
    {
        if (frame->sample_rate is < 1 or > 384000 || frame->nb_samples is < 1 or > 32768)
            throw new InvalidDataException("音频参数超出支持范围。");
        var format = (AVSampleFormat)frame->format;
        var current = layout;
        if (resampler == null || frame->sample_rate != sampleRate || format != sampleFormat ||
            av_channel_layout_compare(&current, &frame->ch_layout) != 0)
        {
            FreeResampler();
            var channelLayout = new AVChannelLayout();
            var stereo = new AVChannelLayout();
            av_channel_layout_default(&stereo, 2);
            input.Check(av_channel_layout_copy(&channelLayout, &frame->ch_layout));
            layout = channelLayout;
            SwrContext* context = null;
            int status = swr_alloc_set_opts2(&context, &stereo, AVSampleFormat.AV_SAMPLE_FMT_S16, Rate,
                &channelLayout, format, frame->sample_rate, 0, null);
            resampler = context;
            input.Check(status);
            input.Check(swr_init(context));
            sampleRate = frame->sample_rate;
            sampleFormat = format;
        }

        int samples = swr_get_out_samples(resampler, frame->nb_samples);
        input.Check(samples);
        if (samples > Rate * 2) throw new InvalidDataException("音频块过大。");
        var audio = ArrayPool<byte>.Shared.Rent(samples * FrameBytes);
        try
        {
            int written;
            fixed (byte* data = audio)
            {
                byte* destination = data;
                written = swr_convert(resampler, &destination, samples, frame->extended_data, frame->nb_samples);
            }
            input.Check(written);
            if (Math.Abs(timestamp - NextTime) < 0.08) timestamp = NextTime;
            double playAt = Math.Max(NextTime, now - 0.015);
            if (timestamp > playAt)
            {
                int silence = (int)Math.Min(Rate, (timestamp - playAt) * Rate);
                Write(Silence.AsSpan(0, silence * FrameBytes), input);
            }
            int skip = (int)Math.Clamp(Math.Round((playAt - timestamp) * Rate), 0, written);
            Write(audio.AsSpan(skip * FrameBytes, (written - skip) * FrameBytes), input);
            NextTime = Math.Max(playAt, timestamp + written / (double)Rate);
        }
        finally { ArrayPool<byte>.Shared.Return(audio, clearArray: true); }
    }

    private void Write(ReadOnlySpan<byte> pcm, FfmpegInput input)
    {
        if (pcm.IsEmpty) return;
        if (rate == 1)
        {
            buffer.Write(pcm);
            return;
        }

        tempo ??= new AudioTempo(rate, Rate, input.Check);
        tempo.Push(pcm);
        tempo.Drain(buffer.Write);
    }

    public void Finish()
    {
        if (tempo is null) return;
        tempo.Complete();
        tempo.Drain(buffer.Write);
    }

    public void Play() => output.Play();
    public void Pause() => output.Pause();
    public bool Finished(double now) => buffer.Count == 0 && now >= NextTime + 0.10 * rate;

    public void Reset(double target, double newRate)
    {
        output.Stop();
        buffer.Clear();
        FreeResampler();
        tempo?.Dispose();
        tempo = null;
        rate = newRate;
        NextTime = target;
    }

    private void FreeResampler()
    {
        var context = resampler;
        swr_free(&context);
        resampler = null;
        var channels = layout;
        av_channel_layout_uninit(&channels);
        layout = default;
    }

    public void Dispose()
    {
        output.Dispose();
        buffer.Clear();
        tempo?.Dispose();
        FreeResampler();
    }

    private sealed class AudioBuffer(int capacity) : IWaveProvider
    {
        private readonly byte[] data = new byte[capacity];
        private int offset;
        private int size;
        public WaveFormat WaveFormat { get; } = new(Rate, 16, 2);
        public int Count { get { lock (data) return size; } }

        public void Write(ReadOnlySpan<byte> source)
        {
            lock (data)
            {
                int count = Math.Min(source.Length, data.Length - size);
                int tail = (offset + size) % data.Length;
                int first = Math.Min(count, data.Length - tail);
                source[..first].CopyTo(data.AsSpan(tail));
                source.Slice(first, count - first).CopyTo(data);
                size += count;
            }
        }

        public int Read(byte[] destination, int start, int count)
        {
            lock (data)
            {
                int available = Math.Min(count, size);
                int first = Math.Min(available, data.Length - offset);
                data.AsSpan(offset, first).CopyTo(destination.AsSpan(start));
                data.AsSpan(0, available - first).CopyTo(destination.AsSpan(start + first));
                CryptographicOperations.ZeroMemory(data.AsSpan(offset, first));
                CryptographicOperations.ZeroMemory(data.AsSpan(0, available - first));
                offset = (offset + available) % data.Length;
                size -= available;
                destination.AsSpan(start + available, count - available).Clear();
                return count;
            }
        }

        public void Clear()
        {
            lock (data)
            {
                CryptographicOperations.ZeroMemory(data);
                offset = size = 0;
            }
        }
    }
}

internal sealed unsafe class AudioTempo : IDisposable
{
    private const int FrameBytes = 4;
    private readonly Action<int> check;
    private readonly int sampleRate;
    private AVFilterGraph* graph;
    private AVFilterContext* source;
    private AVFilterContext* sink;
    private AVFrame* send;
    private AVFrame* receive;
    private long sampleIndex;

    public delegate void PcmSink(ReadOnlySpan<byte> pcm);

    public AudioTempo(double rate, int sampleRate, Action<int> check)
    {
        this.check = check;
        this.sampleRate = sampleRate;
        try
        {
            graph = avfilter_graph_alloc();
            send = av_frame_alloc();
            receive = av_frame_alloc();
            if (graph == null || send == null || receive == null) throw new OutOfMemoryException();
            graph->nb_threads = 1;
            source = Create("abuffer", $"time_base=1/{sampleRate}:sample_rate={sampleRate}:sample_fmt=s16:channel_layout=stereo");
            var tempoFilter = Create("atempo", rate.ToString(CultureInfo.InvariantCulture));
            sink = Create("abuffersink", null);
            check(avfilter_link(source, 0, tempoFilter, 0));
            check(avfilter_link(tempoFilter, 0, sink, 0));
            check(avfilter_graph_config(graph, null));
        }
        catch { Dispose(); throw; }
    }

    private AVFilterContext* Create(string name, string? options)
    {
        var filter = avfilter_get_by_name(name);
        if (filter == null) throw new InvalidDataException($"FFmpeg 缺少 {name} 音频滤镜。");
        AVFilterContext* context = null;
        check(avfilter_graph_create_filter(&context, filter, name, options, null, graph));
        return context;
    }

    public void Push(ReadOnlySpan<byte> pcm)
    {
        send->nb_samples = pcm.Length / FrameBytes;
        send->format = (int)AVSampleFormat.AV_SAMPLE_FMT_S16;
        send->sample_rate = sampleRate;
        send->pts = sampleIndex;
        sampleIndex += send->nb_samples;
        av_channel_layout_default(&send->ch_layout, 2);
        try
        {
            check(av_frame_get_buffer(send, 0));
            pcm.CopyTo(new Span<byte>(send->data[0], pcm.Length));
            check(av_buffersrc_add_frame(source, send));
        }
        finally { av_frame_unref(send); }
    }

    public void Complete() => check(av_buffersrc_add_frame(source, null));

    public void Drain(PcmSink write)
    {
        while (true)
        {
            int result = av_buffersink_get_frame(sink, receive);
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) return;
            check(result);
            try { write(new ReadOnlySpan<byte>(receive->data[0], receive->nb_samples * FrameBytes)); }
            finally { av_frame_unref(receive); }
        }
    }

    public void Dispose()
    {
        var inputFrame = send;
        var outputFrame = receive;
        var filterGraph = graph;
        av_frame_free(&inputFrame);
        av_frame_free(&outputFrame);
        avfilter_graph_free(&filterGraph);
    }
}
