using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace SecureVideo.Player;

internal static class LocalAuthorizationService
{
    public static async Task EnsureReadyAsync(int port, CancellationToken token)
    {
        if (await IsListeningAsync(port, token)) return;

        string server = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Server", "bin", "Release", "net8.0-windows", "Server.exe"));
        if (!File.Exists(server)) return;

        var info = new ProcessStartInfo(server) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("serve-local");
        info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        try
        {
            using var process = Process.Start(info);
            for (int attempt = 0; attempt < 16 && process is { HasExited: false }; attempt++)
            {
                await Task.Delay(150, token);
                if (await IsListeningAsync(port, token)) return;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // LoginAsync can still use offline credentials if the listener cannot start.
        }
    }

    private static async Task<bool> IsListeningAsync(int port, CancellationToken token)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, token);
            return true;
        }
        catch (SocketException) { return false; }
    }
}
