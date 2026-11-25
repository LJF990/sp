using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using SecureVideo;
using SecureVideo.Player;

if (args is ["--ui", var embeddedConfig, var testMedia, var localVideo, var role])
{
    TestWindow(embeddedConfig, Console.ReadLine() ?? throw new ArgumentException("缺少测试密码。"), testMedia, localVideo, role == "admin");
    return;
}
if (args is not [var repo]) throw new ArgumentException("传入仓库目录。");
var work = Path.Combine(repo, "artifacts", "smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
var serverExe = Path.Combine(repo, "Server", "bin", "Release", "net8.0-windows", "Server.exe");
var ffmpegExe = Path.Combine(repo, "artifacts", "ffmpeg-tools", "ffmpeg.exe");
var signConfig = Path.Combine(work, "player.json");
int port;
using (var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
{
    socket.Start();
    port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
}
string serverUrl = $"http://127.0.0.1:{port}";
var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
var otherAdminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
Process? server = null;
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
try
{
    TestContainer();
    TestZipStream();
    Run(serverExe, ["init"]);
    Run(serverExe, ["add-user", "cli-short-password", "1", "1"], "xyz");
    Run(serverExe, ["add-user", "smoke", "1", "1"], password);
    Run(serverExe, ["add-admin", "test-admin", "1", "1"], adminPassword);
    Run(serverExe, ["add-admin", "other-admin", "1", "1"], otherAdminPassword);
    Run(serverExe, ["export-config", serverUrl, signConfig]);
    StartServer(useLocalService: true);

    var config = JsonSerializer.Deserialize<PlayerConfig>(File.ReadAllText(signConfig))!;
    using var client = new AuthorizationClient(config);
    try
    {
        await client.LoginAsync("smoke", "wrong", timeout.Token);
        throw new Exception("错误密码被接受。");
    }
    catch (InvalidOperationException error) when (error.InnerException is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }) { }

    var (session, offline) = await client.LoginAsync("smoke", password, timeout.Token);
    Check(!offline, "在线登录失败");
    Security.LoadFfmpeg();
    TestAudioTempo();

    Guid last = default;
    foreach (string extension in new[] { "mp4", "mkv" })
    {
        string source = Path.Combine(work, "sample." + extension);
        string encrypted = Path.Combine(work, extension + ".svideo");
        Run(ffmpegExe, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=321x181:rate=20",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "2", "-c:v", "mpeg4",
            "-g", "8", "-c:a", "aac", source]);
        last = Guid.Parse(Run(serverExe, ["import", "test-admin", source, encrypted]));
        using var media = File.OpenRead(encrypted);
        Check(VideoHeader.Read(media).MediaId == last, "媒体 ID 不匹配");
        await ExpectDenied(() => client.GrantAsync(session, last, timeout.Token));
        Run(serverExe, ["grant", "smoke", last.ToString()]);
        var lease = await client.GrantAsync(session, last, timeout.Token);
        var key = client.Unwrap(lease.Grant);
        var reader = new EncryptedVideoReader(media, key, last);
        CryptographicOperations.ZeroMemory(key);
        using (reader)
        {
            var playback = new Playback();
            var times = new List<double>();
            playback.Run(reader, reader.Header.Format, (frame, _, duration) =>
            {
                Check(frame.Width > 0 && frame.Height > 0 && duration > 1, "画面或时长无效");
                Check(frame.Pixels.AsSpan(0, frame.Width * frame.Height * 4).IndexOfAnyExcept((byte)0) >= 0,
                    "解码画面为空");
                times.Add(frame.Time);
                if (times.Count == 2) playback.Seek(1.1);
            }, message => Console.WriteLine(message), timeout.Token);
            Check(times.Count >= 5 && times[2] >= 1.09, "FFmpeg 未定位到请求时间");
            Console.WriteLine($"{extension}: authenticated decode and seek passed ({times.Count} frames)");
        }
    }

    StopServer();
    using (var restarted = new AuthorizationClient(config))
    {
        Check(restarted.LastAccount == "smoke", "未记住上次登录账号");
        var cached = await restarted.LoginAsync("smoke", password, timeout.Token);
        Check(cached.Offline, "离线登录未使用缓存");
        var lease = await restarted.GrantAsync(cached.Session, last, timeout.Token);
        Check(lease.Offline && lease.Grant.OfflineUntil - lease.Grant.Issued <= TimeSpan.FromHours(1), "离线授权不正确");
        CryptographicOperations.ZeroMemory(restarted.Unwrap(lease.Grant));
    }
    Run(serverExe, ["revoke", "smoke", last.ToString()]);
    StartServer();
    var renewed = await client.LoginAsync("smoke", password, timeout.Token);
    await ExpectDenied(() => client.GrantAsync(renewed.Session, last, timeout.Token));
    Run(serverExe, ["disable-user", "smoke"]);
    await ExpectDenied(() => client.LoginAsync("smoke", password, timeout.Token));
    Console.WriteLine("online login, offline cache, and authorization revocation passed");

    Run(serverExe, ["renew-user", "smoke", "1"]);
    Run(serverExe, ["grant", "smoke", last.ToString()]);
    var testMediaPath = Path.Combine(work, "mkv.svideo");
    var localVideoPath = Path.Combine(work, "sample.mkv");
    var admin = (await client.LoginAsync("test-admin", adminPassword, timeout.Token)).Session;
    var otherAdmin = (await client.LoginAsync("other-admin", otherAdminPassword, timeout.Token)).Session;
    var member = (await client.LoginAsync("smoke", password, timeout.Token)).Session;
    Check(admin.IsAdministrator && !member.IsAdministrator, "账号角色错误");
    foreach (var operation in Enum.GetValues<AdminOperation>())
        await ExpectAdminDenied(() => client.ManageAsync(member, operation, last, timeout.Token));
    await ExpectBadOptions(() => client.ManageAsync(admin, AdminOperation.CreateMember, Guid.Empty, timeout.Token,
        account: "new-member", password: password, memberDays: 0, maxDevices: 2));
    await ExpectBadOptions(() => client.ManageAsync(admin, AdminOperation.CreateMember, Guid.Empty, timeout.Token,
        account: "new-member", password: "xy"));
    await client.ManageAsync(admin, AdminOperation.CreateMember, Guid.Empty, timeout.Token,
        account: "new-member", password: "xyz", memberDays: 30, maxDevices: 2, allowOffline: false);
    var newMember = (await client.LoginAsync("new-member", "xyz", timeout.Token)).Session;
    Check(!newMember.IsAdministrator, "创建的成员不应具有管理员权限");
    var created = Guid.NewGuid();
    var newKey = RandomNumberGenerator.GetBytes(32);
    var adminVideo = Path.Combine(work, "admin.svideo");
    using (var plain = File.OpenRead(localVideoPath))
    using (var cipher = File.Create(adminVideo))
        EncryptedVideoWriter.Write(plain, cipher, created, VideoFormat.Mkv, newKey);
    try
    {
        await client.ManageAsync(admin, AdminOperation.Register, created, timeout.Token, key: newKey);
        await client.ManageAsync(admin, AdminOperation.Register, created, timeout.Token, key: newKey);
        await ExpectAdminDenied(() => client.ManageAsync(otherAdmin, AdminOperation.Register, created, timeout.Token, key: newKey));
    }
    finally { CryptographicOperations.ZeroMemory(newKey); }
    var foreignVideo = Path.Combine(work, "other-admin.svideo");
    var foreignMedia = Guid.Parse(Run(serverExe, ["import", "other-admin", localVideoPath, foreignVideo]));
    var renamedForeignVideo = Path.ChangeExtension(foreignVideo, ".mp4");
    File.Move(foreignVideo, renamedForeignVideo);
    var ownerGrant = await client.GrantAsync(admin, created, timeout.Token);
    Check(ownerGrant.Grant.MediaOwner == admin.Subject && Protocol.IsPermanentOwnerGrant(ownerGrant.Grant),
        "管理员播放许可未绑定所有者或没有离线长期授权");
    await ExpectDenied(() => client.GrantAsync(otherAdmin, created, timeout.Token));
    await ExpectDenied(() => client.GrantAsync(admin, foreignMedia, timeout.Token));
    Check((await client.GrantAsync(otherAdmin, foreignMedia, timeout.Token)).Grant.MediaOwner == otherAdmin.Subject,
        "命令行加密视频没有绑定指定管理员");
    await ExpectAdminDenied(() => client.ManageAsync(otherAdmin, AdminOperation.Export, created, timeout.Token));
    await ExpectAdminDenied(() => client.ManageAsync(otherAdmin, AdminOperation.Assign, created, timeout.Token,
        account: "new-member"));
    await ExpectDenied(() => client.GrantAsync(newMember, created, timeout.Token));
    await ExpectBadOptions(() => client.ManageAsync(admin, AdminOperation.Assign, created, timeout.Token,
        account: "new-member", mediaDays: 3651));
    await client.ManageAsync(admin, AdminOperation.Assign, created, timeout.Token, account: "new-member", mediaDays: 7);
    var onlineOnly = await client.GrantAsync(newMember, created, timeout.Token);
    Check(!onlineOnly.Offline && onlineOnly.Grant.OfflineUntil == onlineOnly.Grant.Issued,
        "仅联网成员收到离线授权");
    var export = await client.ManageAsync(admin, AdminOperation.Export, created, timeout.Token);
    var exportKey = client.OpenExportGrant(export!, admin, created);
    using (var reader = new EncryptedVideoReader(File.OpenRead(adminVideo), exportKey, created))
    using (var recovered = new MemoryStream())
    {
        reader.CopyTo(recovered);
        Check(recovered.ToArray().SequenceEqual(File.ReadAllBytes(localVideoPath)), "导出数据不匹配");
    }
    CryptographicOperations.ZeroMemory(exportKey);
    Console.WriteLine("admin registration, member grant, export round trip, and member access denial passed");

    TestWindow(signConfig, password, testMediaPath, localVideoPath, administrator: false);
    TestWindow(signConfig, adminPassword, adminVideo, localVideoPath, administrator: true, foreignVideo: renamedForeignVideo);
    if (Environment.GetEnvironmentVariable("SVIDEO_TEST_PUBLISH") == "1")
    {
        var publishOutput = Run("pwsh.exe", ["-NoProfile", "-File", Path.Combine(repo, "scripts", "Publish-Player.ps1"),
            "-Config", signConfig]);
        const string prefix = "Published player: ";
        string published = publishOutput.Split('\n').Select(line => line.Trim())
            .Last(line => line.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
        var testOutput = Path.Combine(work, "published-test");
        Directory.CreateDirectory(testOutput);
        foreach (var path in Directory.GetFiles(AppContext.BaseDirectory))
            File.Copy(path, Path.Combine(testOutput, Path.GetFileName(path)));
        foreach (var path in Directory.GetFiles(published))
            File.Copy(path, Path.Combine(testOutput, Path.GetFileName(path)), overwrite: true);
        Directory.CreateDirectory(Path.Combine(testOutput, "ffmpeg"));
        foreach (var path in Directory.GetFiles(Path.Combine(published, "ffmpeg")))
            File.Copy(path, Path.Combine(testOutput, "ffmpeg", Path.GetFileName(path)));
        File.Delete(Path.Combine(testOutput, "player.json"));
        Console.WriteLine(Run(Path.Combine(testOutput, "Smoke.exe"), ["--ui", "-", testMediaPath, localVideoPath, "member"], password));
        Console.WriteLine("obfuscated player with embedded configuration passed");
    }

    StopServer();
    try
    {
        await client.GrantAsync(newMember, created, timeout.Token);
        throw new Exception("仅联网成员成功离线打开了加密视频。");
    }
    catch (InvalidOperationException error) when (error.Message == "此账号需要联网播放加密视频。") { }
    using (var offlineClient = new AuthorizationClient(config))
    {
        var login = await offlineClient.LoginAsync("test-admin", adminPassword, timeout.Token);
        var cached = await offlineClient.GrantAsync(login.Session, created, timeout.Token);
        Check(login.Offline && !login.Session.IsAdministrator && cached.Offline && Protocol.IsPermanentOwnerGrant(cached.Grant),
            "管理员重新启动后未取得长期离线播放许可，或离线管理权限未关闭");
        CryptographicOperations.ZeroMemory(offlineClient.Unwrap(cached.Grant));
    }
    Console.WriteLine("member time limits and permanent device-bound owner playback passed");

    var launchDirectory = Path.Combine(work, "launcher-test");
    Directory.CreateDirectory(launchDirectory);
    var playerOutput = Path.Combine(repo, "Player", "bin", "Release", "net8.0-windows");
    foreach (var path in Directory.GetFiles(playerOutput))
        File.Copy(path, Path.Combine(launchDirectory, Path.GetFileName(path)));
    server = Process.Start(Info(serverExe, ["local-player", Path.Combine(launchDirectory, "Player.exe"), signConfig]));
    using var launchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (true)
    {
        launchTimeout.Token.ThrowIfCancellationRequested();
        if (server is not { HasExited: false } launcher) throw new Exception("本机启动器提前退出");
        var candidates = Process.GetProcessesByName("Player");
        try
        {
            var player = candidates.FirstOrDefault(candidate => candidate.MainWindowHandle != IntPtr.Zero &&
                string.Equals(candidate.MainModule?.FileName, Path.Combine(launchDirectory, "Player.exe"),
                    StringComparison.OrdinalIgnoreCase));
            if (player is not null)
            {
                Check(player.CloseMainWindow(), "播放器无法正常关闭");
                await launcher.WaitForExitAsync(launchTimeout.Token);
                Check(launcher.ExitCode == 0, "本机授权服务未正常退出");
                break;
            }
        }
        finally { foreach (var candidate in candidates) candidate.Dispose(); }
        await Task.Delay(100, launchTimeout.Token);
    }
    Console.WriteLine("local launcher started the player and shut down with it");
}
finally
{
    StopServer();
    Directory.Delete(work, recursive: true);
}

static void TestWindow(string config, string password, string video, string plainVideo, bool administrator,
    string? foreignVideo = null)
{
    Exception? uiError = null;
    var ui = new Thread(() =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            MainWindow? window = null;
            try
            {
                if (config != "-") File.Copy(config, Path.Combine(AppContext.BaseDirectory, "player.json"), overwrite: true);
                window = new MainWindow();
                window.Show();
                Check(window.Title.Length == 0 && window.Icon is not null, "窗口标题或图标不正确");
                if (config != "-" && administrator) SaveLayout("login");
                Check(window.FindName("Protection") is System.Windows.Controls.TextBlock { Text: "录屏保护已启用" },
                    "当前桌面未启用录屏保护");
                var login = (System.Windows.Controls.Button)window.FindName("LoginButton");
                Check(login.IsEnabled, "配置加载后登录按钮不可用");
                var panel = (System.Windows.Controls.Border)window.FindName("AdminPanel");
                Check(panel.Visibility == System.Windows.Visibility.Collapsed, "登录前管理员面板可见");
                ((System.Windows.Controls.TextBox)window.FindName("Account")).Text = administrator ? "test-admin" : "smoke";
                ((System.Windows.Controls.PasswordBox)window.FindName("Password")).Password = password;
                login.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var open = (System.Windows.Controls.Button)window.FindName("OpenButton");
                await Until(() => open.IsEnabled);
                var method = typeof(MainWindow).GetMethod("OpenVideoAsync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                Check(panel.IsVisible == administrator, "管理员面板显示与账号权限不符");
                if (config != "-") SaveLayout(administrator ? "admin" : "member");
                if (administrator)
                {
                    var label = (System.Windows.Controls.TextBlock)window.FindName("OutputFolderLabel");
                    var expected = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos, Environment.SpecialFolderOption.DoNotVerify);
                    Check(!string.IsNullOrEmpty(label.Text) && (expected.Length == 0 || label.Text == expected),
                        "默认加密输出路径不是 Windows 视频文件夹");
                    var output = Path.Combine(Path.GetDirectoryName(video)!, "selected");
                    typeof(MainWindow).GetField("outputFolder",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(window, output);
                    var encrypt = typeof(MainWindow).GetMethod("EncryptVideoAsync",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    await (Task)encrypt.Invoke(window, [plainVideo])!;
                    string destination = Path.Combine(output, Path.ChangeExtension(Path.GetFileName(plainVideo), ".svideo"));
                    Guid id;
                    using (var result = File.OpenRead(destination)) id = VideoHeader.Read(result).MediaId;
                    await (Task)encrypt.Invoke(window, [plainVideo])!;
                    using var original = File.OpenRead(destination);
                    Check(VideoHeader.Read(original).MediaId == id, "同名加密文件被覆盖");
                }
                var speed = (System.Windows.Controls.ComboBox)window.FindName("Speed");
                Check(speed.Items.Count == 7 && speed.SelectedItem is "1×", "倍速菜单默认值错误");
                var image = (System.Windows.Controls.Image)window.FindName("Screen");
                if (foreignVideo is not null)
                {
                    await (Task)method.Invoke(window, [foreignVideo])!;
                    var status = (System.Windows.Controls.TextBlock)window.FindName("Status");
                    Check(status.Text.StartsWith("权限不足", StringComparison.Ordinal) && image.Source is null &&
                        panel.IsVisible, "其他管理员的加密视频未被正确拒绝");
                }
                var renamed = Path.ChangeExtension(video, ".renamed.mkv");
                File.Copy(video, renamed, overwrite: true);
                foreach (var path in new[] { video, plainVideo, renamed })
                {
                    bool testControls = !administrator && config != "-" && path == video;
                    var loop = (System.Windows.Controls.CheckBox)window.FindName("Loop");
                    loop.IsChecked = testControls;
                    var playing = (Task)method.Invoke(window, [path])!;
                    await Until(() => image.Source is not null || playing.IsCompleted);
                    Check(image.Source is not null, ((System.Windows.Controls.TextBlock)window.FindName("Status")).Text);
                    Press(System.Windows.Input.Key.Space);
                    var pause = (System.Windows.Controls.Button)window.FindName("PauseButton");
                    Check(pause.Content is "继续", "空格未暂停");
                    Press(System.Windows.Input.Key.R);
                    Press(System.Windows.Input.Key.OemCloseBrackets);
                    Check(speed.SelectedItem is "1.25×", "倍速快捷键未生效");
                    speed.SelectedItem = "2×";
                    Press(System.Windows.Input.Key.M);
                    var mute = (System.Windows.Controls.Button)window.FindName("MuteButton");
                    Check(mute.Content is "取消静音", "静音快捷键未生效");
                    Press(System.Windows.Input.Key.M);
                    Press(System.Windows.Input.Key.Space);
                    Press(System.Windows.Input.Key.F11);
                    Check(window.WindowStyle == System.Windows.WindowStyle.None &&
                        Security.IsWindowProtected(new System.Windows.Interop.WindowInteropHelper(window).Handle),
                        "全屏模式或窗口保护失效");
                    Press(System.Windows.Input.Key.Escape);
                    Check(window.WindowStyle != System.Windows.WindowStyle.None, "未恢复普通窗口");
                    if (testControls)
                    {
                        var playbackControls = (System.Windows.FrameworkElement)window.FindName("PlaybackControls");
                        Press(System.Windows.Input.Key.F11);
                        Check(playbackControls.IsVisible, "进入全屏时播放控件不可见");
                        await Until(() => !playbackControls.IsVisible || playing.IsCompleted);
                        Check(!playing.IsCompleted, "等待控件自动隐藏时播放提前结束");
                        var motion = new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                            Environment.TickCount) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseMoveEvent };
                        window.RaiseEvent(motion);
                        Check(playbackControls.IsVisible, "鼠标移动后未显示播放控件");
                        Press(System.Windows.Input.Key.Space);
                        await Task.Delay(TimeSpan.FromMilliseconds(3200));
                        Check(playbackControls.IsVisible, "暂停时播放控件被隐藏");
                        Press(System.Windows.Input.Key.Escape);
                    }
                    await Task.Delay(200);
                    Check(!playing.IsCompleted, ((System.Windows.Controls.TextBlock)window.FindName("Status")).Text);
                    var stop = (System.Windows.Controls.Button)window.FindName("StopButton");
                    stop.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await playing;
                    Check(image.Source is null, "停止后画面未清空");
                }
                if (!administrator) await CheckPlaylist();
                var logout = (System.Windows.Controls.Button)window.FindName("LogoutButton");
                logout.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Check(panel.Visibility == System.Windows.Visibility.Collapsed, "退出后管理员面板可见");
                Check(((System.Windows.Controls.TextBox)window.FindName("Account")).Text == (administrator ? "test-admin" : "smoke"),
                    "返回登录页后未保留上次账号");

                async Task CheckPlaylist()
                {
                    const System.Reflection.BindingFlags flags =
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var folder = Path.Combine(Path.GetDirectoryName(plainVideo)!,
                        "playlist-" + Guid.NewGuid().ToString("N"));
                    var nested = Path.Combine(folder, "child", "nested");
                    Directory.CreateDirectory(nested);
                    var first = Path.Combine(folder, "02 " + new string('V', 95) + ".mkv");
                    var second = Path.Combine(nested, "10 episode.mkv");
                    var third = Path.Combine(nested, "untitled.mkv");
                    foreach (string file in new[] { first, second, third }) File.Copy(plainVideo, file);
                    File.WriteAllText(Path.Combine(folder, "00 ignore.txt"), "not a video");

                    var load = typeof(MainWindow).GetMethod("LoadPlaylistFolderAsync", flags)!;
                    await (Task)load.Invoke(window, [folder])!;
                    await (Task)load.Invoke(window, [folder])!;
                    var list = (System.Windows.Controls.ListBox)window.FindName("PlaylistList");
                    Check(list.IsVisible && list.Items.OfType<PlaylistEntry>().Select(entry => entry.FullPath)
                        .SequenceEqual(new[] { first, second, third }), "递归扫描、数字排序或去重失败");
                    window.UpdateLayout();
                    var scroll = FindScrollViewer(list);
                    Check(scroll is { ScrollableWidth: > 0 }, "长文件名未启用横向滚动");

                    speed.SelectedItem = "1×";
                    var loop = (System.Windows.Controls.CheckBox)window.FindName("Loop");
                    loop.IsChecked = true;
                    var play = (System.Windows.Controls.Button)window.FindName("PlaylistPlayButton");
                    play.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Until(() => image.Source is not null);
                    list.SelectedIndex = 1;
                    play.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    var badge = (System.Windows.Controls.Border)window.FindName("FileBadge");
                    await Until(() => Equals(badge.ToolTip, second) && image.Source is not null);
                    var stop = (System.Windows.Controls.Button)window.FindName("StopButton");
                    stop.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Until(() => !stop.IsEnabled);
                    var zip = Path.Combine(folder, "batch.zip");
                    using (var package = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                    {
                        foreach (var (path, name) in new[] { (plainVideo, "one/01 inside.mkv"), (video, "two/20 inside.mp4") })
                        {
                            using var original = File.OpenRead(path);
                            using var target = package.CreateEntry(name).Open();
                            original.CopyTo(target);
                        }
                    }
                    var loadZip = typeof(MainWindow).GetMethod("LoadPlaylistZipAsync", flags)!;
                    await (Task)loadZip.Invoke(window, [zip])!;
                    Check(image.Source is null && !stop.IsEnabled, "读取 ZIP 目录时不应开始播放");
                    var archived = list.Items.OfType<PlaylistEntry>().Where(item => item.FullPath == zip).ToArray();
                    Check(archived.Length == 2 && archived[0].Location.EndsWith("one/01 inside.mkv"), "ZIP 路径未加入列表");
                    foreach (var item in archived)
                    {
                        list.SelectedItem = item;
                        play.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        await Until(() => image.Source is not null);
                        Check(Equals(badge.ToolTip, item.Location), "ZIP 条目未打开正确的视频");
                        stop.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        await Until(() => !stop.IsEnabled);
                    }
                    Console.WriteLine("recursive playlist, ZIP listing, and archived video playback passed");
                }

                static System.Windows.Controls.ScrollViewer? FindScrollViewer(System.Windows.DependencyObject element)
                {
                    if (element is System.Windows.Controls.ScrollViewer scroll) return scroll;
                    for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); i++)
                    {
                        var found = FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(element, i));
                        if (found is not null) return found;
                    }
                    return null;
                }

                void SaveLayout(string name)
                {
                    var directory = Environment.GetEnvironmentVariable("SVIDEO_LAYOUT_PREVIEWS");
                    if (string.IsNullOrEmpty(directory)) return;
                    window.UpdateLayout();
                    var preview = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
                        (int)Math.Ceiling(window.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    preview.Render(window);
                    var output = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    output.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
                    Directory.CreateDirectory(directory);
                    using var file = File.Create(Path.Combine(directory, name + ".png"));
                    output.Save(file);
                }

                void Press(System.Windows.Input.Key key)
                {
                    var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                        System.Windows.PresentationSource.FromVisual(window), Environment.TickCount, key)
                    {
                        RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
                    };
                    window.RaiseEvent(args);
                    Check(args.Handled, $"快捷键 {key} 未响应");
                }
            }
            catch (Exception error) { uiError = error; }
            finally
            {
                if (window?.FindName("StopButton") is System.Windows.Controls.Button { IsEnabled: true } stop)
                {
                    stop.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    try { await Until(() => !stop.IsEnabled); }
                    catch (OperationCanceledException error) { uiError ??= error; }
                }
                window?.Close();
                dispatcher.InvokeShutdown();
            }
        }));
        Dispatcher.Run();
    });
    ui.SetApartmentState(ApartmentState.STA);
    ui.Start();
    Check(ui.Join(TimeSpan.FromSeconds(28)), "WPF 启动超时");
    if (uiError is not null) throw new Exception("WPF 窗口检查失败", uiError);
    Console.WriteLine("WPF login, protected playback and stop passed");
}

void StartServer(bool useLocalService = false)
{
    string[] arguments = useLocalService ? ["serve-local", Environment.ProcessId.ToString(), port.ToString()] :
        ["serve", "--urls", serverUrl];
    var startInfo = Info(serverExe, arguments);
    if (useLocalService) startInfo.Environment.Remove("SVIDEO_ALLOW_LOCAL_HTTP");
    server = Process.Start(startInfo);
    using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
    for (int i = 0; i < 50; i++)
    {
        if (server is null || server.HasExited) throw new Exception("授权服务启动失败");
        try
        {
            using var response = http.GetAsync(serverUrl, timeout.Token).GetAwaiter().GetResult();
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
        Thread.Sleep(100);
    }
    throw new Exception("授权服务未开始监听");
}

void StopServer()
{
    if (server is null) return;
    if (!server.HasExited) server.Kill(entireProcessTree: true);
    server.WaitForExit();
    server.Dispose();
    server = null;
}

ProcessStartInfo Info(string exe, string[] arguments)
{
    var info = new ProcessStartInfo(exe) { UseShellExecute = false };
    foreach (string argument in arguments) info.ArgumentList.Add(argument);
    info.Environment["SVIDEO_VAULT"] = Path.Combine(work, "vault");
    info.Environment["SVIDEO_ALLOW_LOCAL_HTTP"] = "1";
    info.Environment["Logging__LogLevel__Default"] = "Warning";
    return info;
}

string Run(string exe, string[] arguments, string? stdin = null)
{
    var info = Info(exe, arguments);
    info.RedirectStandardOutput = true;
    info.RedirectStandardInput = stdin is not null;
    using var process = Process.Start(info) ?? throw new Exception("进程未启动");
    if (stdin is not null)
    {
        process.StandardInput.WriteLine(stdin);
        process.StandardInput.Close();
    }
    string output = process.StandardOutput.ReadToEnd().Trim();
    process.WaitForExit();
    Check(process.ExitCode == 0, $"{Path.GetFileName(exe)} 执行失败");
    return output;
}

static async Task Until(Func<bool> ready)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    while (!ready()) await Task.Delay(20, deadline.Token);
}

static async Task ExpectBadOptions(Func<Task<SignedGrant?>> action)
{
    try { await action(); }
    catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.BadRequest) { return; }
    throw new Exception("服务端接受了无效的授权参数。");
}

static async Task ExpectAdminDenied(Func<Task<SignedGrant?>> action)
{
    try { await action(); }
    catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.Forbidden) { return; }
    throw new Exception("普通账号取得了管理员权限。");
}

static async Task ExpectDenied<T>(Func<Task<T>> action)
{
    try { await action(); }
    catch (InvalidOperationException error) when (error.InnerException is HttpRequestException
        { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }) { return; }
    throw new Exception("授权拒绝未生效。");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void TestAudioTempo()
{
    const int sampleRate = 48000;
    const int framesPerBlock = 480;
    var pcm = new byte[framesPerBlock * 4];
    foreach (double speed in new[] { 0.5, 2.0 })
    {
        int samples = 0;
        int crossings = 0;
        short previous = 0;
        using var tempo = new AudioTempo(speed, sampleRate, code => Check(code >= 0, $"FFmpeg 音频滤镜错误 {code}"));
        for (int index = 0; index < sampleRate; index += framesPerBlock)
        {
            for (int sample = 0; sample < framesPerBlock; sample++)
            {
                short value = (short)(12000 * Math.Sin(2 * Math.PI * 440 * (index + sample) / sampleRate));
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sample * 4), value);
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sample * 4 + 2), value);
            }
            tempo.Push(pcm);
            tempo.Drain(Measure);
        }
        tempo.Complete();
        tempo.Drain(Measure);
        double duration = (double)samples / sampleRate;
        Check(Math.Abs(duration - 1 / speed) < 0.1, $"音频时长与 {speed}× 不匹配");
        Check(Math.Abs(crossings / duration - 440) < 12, "变速后音高发生变化");

        void Measure(ReadOnlySpan<byte> output)
        {
            for (int i = 0; i < output.Length; i += 4)
            {
                short sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(output[i..]);
                if (sample > 0 && previous <= 0) crossings++;
                previous = sample;
                samples++;
            }
        }
    }
    Console.WriteLine("0.5x and 2x audio duration and pitch passed");
}

static void TestZipStream()
{
    var bytes = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 197);
    using var output = new MemoryStream();
    using (var create = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    using (var video = create.CreateEntry("nested/02 test.mkv").Open())
        video.Write(bytes);
    output.Position = 0;
    var entry = ZipVideo.List(output).Single();
    using var videoStream = ZipVideo.Open(output, entry);
    foreach (int position in new[] { bytes.Length - 120, 700, 1024 * 1024 - 15, 2 * 1024 * 1024 + 8 })
    {
        videoStream.Position = position;
        var actual = new byte[100];
        videoStream.ReadExactly(actual);
        Check(actual.AsSpan().SequenceEqual(bytes.AsSpan(position, actual.Length)), "ZIP 字节定位失败");
    }
    Console.WriteLine("ZIP multiblock forward and backward seeking passed");
}

static void TestContainer()
{
    var plain = RandomNumberGenerator.GetBytes(2 * VideoHeader.DefaultBlockSize + 19);
    var key = RandomNumberGenerator.GetBytes(32);
    var id = Guid.NewGuid();
    using var source = new MemoryStream(plain);
    using var output = new MemoryStream();
    EncryptedVideoWriter.Write(source, output, id, VideoFormat.Mp4, key);
    byte[] encrypted = output.ToArray();
    using (var reader = Open(encrypted))
    {
        var portion = new byte[80];
        reader.Position = VideoHeader.DefaultBlockSize - 12;
        reader.ReadExactly(portion);
        Check(portion.AsSpan().SequenceEqual(plain.AsSpan(VideoHeader.DefaultBlockSize - 12, 80)), "跨块定位失败");
        reader.Seek(-19, SeekOrigin.End);
        Check(reader.Read(portion) == 19 && portion.AsSpan(0, 19).SequenceEqual(plain.AsSpan(^19)), "尾块不匹配");
        Check(reader.Read(portion) == 0, "EOF 错误");
    }
    var damaged = encrypted.ToArray();
    damaged[VideoHeader.Size + VideoHeader.TagSize + 4] ^= 1;
    Reject(() => { using var reader = Open(damaged); reader.ReadByte(); });
    damaged = encrypted.ToArray();
    damaged[24] = (byte)VideoFormat.Mkv;
    Reject(() => Open(damaged).Dispose());
    Reject(() => Open(encrypted[..^1]).Dispose());
    Reject(() => new EncryptedVideoReader(new MemoryStream(encrypted), RandomNumberGenerator.GetBytes(32), id).Dispose());
    Console.WriteLine("container seek, EOF, tamper, truncation and wrong key passed");

    EncryptedVideoReader Open(byte[] data) => new(new MemoryStream(data), key, id);
    static void Reject(Action attempt)
    {
        try { attempt(); }
        catch (Exception e) when (e is CryptographicException or InvalidDataException) { return; }
        throw new Exception("损坏或错误密钥的容器被接受。");
    }
}
