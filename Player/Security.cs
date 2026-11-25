using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SecureVideo.Player;

internal static partial class Security
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint window, uint affinity);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowDisplayAffinity(nint window, out uint affinity);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsDebuggerPresent();

    public static bool ProtectWindow(nint window) => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) &&
        SetWindowDisplayAffinity(window, 0x11) && IsWindowProtected(window);

    public static bool IsWindowProtected(nint window) => GetWindowDisplayAffinity(window, out uint value) && value == 0x11;

    public static void CheckDebugger()
    {
#if !DEBUG
        if (Debugger.IsAttached || IsDebuggerPresent())
            throw new InvalidOperationException("检测到调试器，受保护播放已停止。");
#endif
    }

    public static void LoadFfmpeg()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        using var manifest = typeof(Security).Assembly.GetManifestResourceStream("SecureVideo.FFmpegManifest");
        if (manifest is null)
            throw new InvalidOperationException("缺少 FFmpeg 8.1 运行库，请运行 scripts/Install-FFmpeg.ps1 后重新构建。");
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(manifest) ??
            throw new InvalidDataException("FFmpeg 校验清单无效。");
        var files = Directory.GetFiles(directory, "*.dll");
        string[] required = ["avutil-60.dll", "swresample-6.dll", "swscale-9.dll", "avcodec-62.dll", "avformat-62.dll", "avfilter-11.dll"];
        if (files.Length != hashes.Count || required.Any(file => !hashes.ContainsKey(file)))
            throw new InvalidDataException("FFmpeg 运行库不完整。");
        foreach (string file in files)
        {
            using var stream = File.OpenRead(file);
            if (!hashes.TryGetValue(Path.GetFileName(file), out string? expected) ||
                !Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("FFmpeg 运行库未通过完整性校验。");
        }
        FFmpeg.AutoGen.ffmpeg.RootPath = directory;
        if (FFmpeg.AutoGen.ffmpeg.avformat_version() >> 16 != 62)
            throw new InvalidDataException("FFmpeg 运行库版本不兼容。");
    }
}
