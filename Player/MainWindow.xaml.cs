using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SecureVideo.Player;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer controlsIdleTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private AuthorizationClient? authorization;
    private Uri? serverAddress;
    private LoginResponse? session;
    private CancellationTokenSource? playbackCancellation;
    private Task? activePlayback;
    private int playbackRequest;
    private bool scanningFolder;
    private readonly List<PlaylistEntry> playlist = [];
    private Playback? playback;
    private CancellationTokenSource? adminCancellation;
    private WriteableBitmap? surface;
    private nint window;
    private bool protectedWindow;
    private bool seeking;
    private bool updating;
    private bool ffmpegLoaded;

    private static readonly double[] Speeds = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2];
    private bool muted;
    private bool fullscreen;
    private bool offlineLogin;
    private bool offlinePlayback;
    private string? outputFolder;
    private WindowState previousWindowState;
    private WindowStyle previousWindowStyle;
    private ResizeMode previousResizeMode;

    public MainWindow()
    {
        InitializeComponent();
        foreach (double speed in Speeds)
            Speed.Items.Add(speed.ToString("0.##", CultureInfo.InvariantCulture) + "×");
        Speed.SelectedIndex = Array.IndexOf(Speeds, 1);
        UpdateOutputFolderLabel();
        controlsIdleTimer.Tick += OnPlaybackControlsIdle;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        window = new WindowInteropHelper(this).Handle;
        protectedWindow = Security.ProtectWindow(window);
        Protection.Text = protectedWindow ? "录屏保护已启用" : "录屏保护不可用";
        if (!protectedWindow)
        {
            ProtectionBadge.Background = new SolidColorBrush(Color.FromRgb(57, 33, 39));
            Protection.Foreground = new SolidColorBrush(Color.FromRgb(255, 169, 174));
        }
        try
        {
            if (!protectedWindow) throw new InvalidOperationException("当前 Windows 会话无法保护播放窗口。");
            var configFile = Path.Combine(AppContext.BaseDirectory, "player.json");
            using var embedded = typeof(MainWindow).Assembly.GetManifestResourceStream("SecureVideo.PlayerConfig");
            using var configStream = embedded ?? (File.Exists(configFile) ? File.OpenRead(configFile) :
                throw new InvalidOperationException("缺少 player.json，请使用开发者工具导出播放器配置。"));
            var config = JsonSerializer.Deserialize<PlayerConfig>(configStream) ??
                throw new InvalidDataException("播放器配置无效。");
            authorization = new AuthorizationClient(config);
            serverAddress = new Uri(config.Server);
            Account.Text = authorization.LastAccount ?? "";
            LoginButton.IsEnabled = true;
            if (Account.Text.Length == 0) Account.Focus();
            else Password.Focus();
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async void LoginClick(object sender, RoutedEventArgs e)
    {
        if (authorization is null || string.IsNullOrWhiteSpace(Account.Text) || Password.Password.Length == 0)
        {
            Status.Text = "请输入账号和密码。";
            return;
        }
        LoginButton.IsEnabled = false;
        Status.Text = "正在验证账号…";
        string password = Password.Password;
        Password.Clear();
        try
        {
            if (serverAddress is { IsLoopback: true, Scheme: "http" } localServer)
                await LocalAuthorizationService.EnsureReadyAsync(localServer.Port, lifetime.Token);
            var login = await authorization.LoginAsync(Account.Text, password, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            session = login.Session;
            offlineLogin = login.Offline;
            UpdateLayoutState();
            Status.Text = login.Offline ? "已离线登录。加密视频需已有离线许可。" :
                session.IsAdministrator ? "管理员已联网登录" : "已登录";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Status.Text = error.Message; }
        finally { LoginButton.IsEnabled = true; }
    }

    private async void OpenClick(object sender, RoutedEventArgs e)
    {
        if (session is null) return;
        var picker = new OpenFileDialog
        {
            Filter = "视频 (*.mp4;*.mkv;*.svideo)|*.mp4;*.mkv;*.svideo|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };
        if (picker.ShowDialog(this) != true) return;

        AddPlaylistFiles(picker.FileNames);
        await RequestPlaybackAsync(new PlaylistEntry(picker.FileName));
    }

    private async void LoadFolderClick(object sender, RoutedEventArgs e)
    {
        if (session is null || scanningFolder) return;
        var picker = new OpenFolderDialog { Title = "加载视频文件夹" };
        if (picker.ShowDialog(this) == true) await LoadPlaylistFolderAsync(picker.FolderName);
    }

    private async Task LoadPlaylistFolderAsync(string folder)
    {
        if (session is not { } current || scanningFolder) return;
        scanningFolder = true;
        Status.Text = "正在查找文件夹里的视频…";
        try
        {
            var paths = await Task.Run(() => PlaylistFiles.Scan(folder, lifetime.Token), lifetime.Token);
            if (!ReferenceEquals(session, current) || lifetime.IsCancellationRequested) return;

            var added = AddPlaylistFiles(paths);
            Status.Text = paths.Length == 0 ? "此文件夹及子文件夹中没有支持的视频。" :
                added == 0 ? "这些视频已在播放列表中。" : $"已添加 {added} 个视频。";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (ReferenceEquals(session, current)) Status.Text = $"无法读取文件夹：{error.Message}";
        }
        finally { scanningFolder = false; }
    }

    private async void OpenZipClick(object sender, RoutedEventArgs e)
    {
        if (session is null || scanningFolder) return;
        var picker = new OpenFileDialog { Filter = "ZIP 压缩包 (*.zip)|*.zip", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) await LoadPlaylistZipAsync(picker.FileName);
    }

    private async Task LoadPlaylistZipAsync(string path)
    {
        if (session is not { } current || scanningFolder) return;
        scanningFolder = true;
        Status.Text = "正在读取压缩包目录…";
        try
        {
            var entries = await Task.Run(() =>
            {
                using var file = File.OpenRead(path);
                return ZipVideo.List(file, lifetime.Token);
            }, lifetime.Token);
            if (!ReferenceEquals(session, current) || lifetime.IsCancellationRequested) return;

            var added = AddPlaylistEntries(entries.Select(entry => new PlaylistEntry(path, entry)));
            Status.Text = entries.Count == 0 ? "压缩包中没有支持的视频。" :
                added == 0 ? "这些视频已在播放列表中。" : $"已列出 {added} 个视频，双击可播放。";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (ReferenceEquals(session, current)) Status.Text = $"无法打开 ZIP：{error.Message}";
        }
        finally { scanningFolder = false; }
    }

    private int AddPlaylistFiles(IEnumerable<string> paths) =>
        AddPlaylistEntries(paths.Select(path => new PlaylistEntry(Path.GetFullPath(path))));

    private int AddPlaylistEntries(IEnumerable<PlaylistEntry> entries)
    {
        var knownPaths = new HashSet<string>(playlist.Select(Identity), StringComparer.OrdinalIgnoreCase);
        int previousCount = playlist.Count;
        foreach (var entry in entries)
        {
            if (knownPaths.Add(Identity(entry))) playlist.Add(entry);
        }

        int added = playlist.Count - previousCount;
        if (added == 0) return 0;

        var selected = PlaylistList.SelectedItem as PlaylistEntry;
        playlist.Sort(PlaylistFiles.Compare);
        PlaylistList.ItemsSource = playlist.ToArray();
        PlaylistList.SelectedItem = selected ?? playlist[0];
        PlaylistCount.Text = $"{playlist.Count} 个";
        UpdateSidePanels();
        return added;
    }

    private static string Identity(PlaylistEntry entry) => $"{entry.FullPath}\0{entry.Archived?.Index}";

    private void PlaylistSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PlaylistPlayButton.IsEnabled = PlaylistList.SelectedItem is PlaylistEntry;

    private async void PlaylistPlayClick(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is PlaylistEntry entry) await RequestPlaybackAsync(entry);
    }

    private async void PlaylistDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(PlaylistList, source) is not ListBoxItem { Content: PlaylistEntry entry }) return;
        e.Handled = true;
        await RequestPlaybackAsync(entry);
    }

    private async void PlaylistKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || PlaylistList.SelectedItem is not PlaylistEntry entry) return;
        e.Handled = true;
        if (!e.IsRepeat) await RequestPlaybackAsync(entry);
    }

    private async Task RequestPlaybackAsync(PlaylistEntry entry)
    {
        int request = ++playbackRequest;
        playbackCancellation?.Cancel();
        if (activePlayback is not null) await activePlayback;
        if (request != playbackRequest || session is null || lifetime.IsCancellationRequested) return;

        PlaylistList.SelectedItem = playlist.Find(requested => StringComparer.OrdinalIgnoreCase.Equals(Identity(entry), Identity(requested)));
        if (PlaylistList.SelectedItem is { } selected) PlaylistList.ScrollIntoView(selected);
        var started = OpenEntryAsync(entry);
        activePlayback = started;
        try { await started; }
        finally { if (ReferenceEquals(activePlayback, started)) activePlayback = null; }
    }

    private Task OpenVideoAsync(string path) => OpenEntryAsync(new PlaylistEntry(path));

    private async Task OpenEntryAsync(PlaylistEntry entry)
    {
        if (authorization is not { } client || session is not { } current || playbackCancellation is not null) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        playbackCancellation = cancel;
        UpdateLayoutState();
        StopButton.IsEnabled = true;
        StopButton.Tag = Asset("stop");
        FileNameLabel.Text = entry.FileName;
        FileBadge.ToolTip = entry.Location;
        Status.Text = "正在获取播放许可…";
        try
        {
            VerifyPlaybackProtection();
            if (!ffmpegLoaded) { Security.LoadFfmpeg(); ffmpegLoaded = true; }
            using var source = File.OpenRead(entry.FullPath);
            using var archiveVideo = entry.Archived is { } item ? ZipVideo.Open(source, item, cancel.Token) : null;
            var input = archiveVideo ?? source;
            (VideoHeader? Header, VideoFormat? PlainFormat) ReadFormat()
            {
                var encrypted = VideoHeader.ReadIfEncrypted(input);
                return encrypted is null ? (null, LocalVideo.Detect(input)) : (encrypted, null);
            }
            // The first read of a ZIP entry can decompress a block, even for a short signature.
            var (header, localFormat) = archiveVideo is null ? ReadFormat() :
                await Task.Run(ReadFormat, cancel.Token);
            if (header is null)
            {
                Status.Text = "正在播放";
                await PlayStreamAsync(input, localFormat!.Value, cancel.Token);
                Status.Text = "播放结束。";
                return;
            }
            var license = await client.GrantAsync(current, header.MediaId, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            offlinePlayback = license.Offline;
            Status.Text = offlinePlayback ? "正在离线播放" : "正在播放";
            StatusIcon.Source = Asset(license.Offline ? "offline" : "online");

            var key = client.Unwrap(license.Grant);
            EncryptedVideoReader reader;
            try { reader = new EncryptedVideoReader(input, key, header.MediaId); }
            finally { CryptographicOperations.ZeroMemory(key); }
            using (reader)
            using (var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token))
            {
                var monitor = MonitorAsync(client, current, header.MediaId, license, cancel, monitorCancellation.Token);
                try
                {
                    await PlayStreamAsync(reader, header.Format, cancel.Token);
                    Status.Text = "播放结束。";
                }
                finally
                {
                    await monitorCancellation.CancelAsync();
                    string? stopped = await monitor;
                    if (stopped is not null) Status.Text = stopped;
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) { Status.Text = error.Message; }
        finally
        {
            ClearScreen();
            if (fullscreen) ToggleFullscreen();
            controlsIdleTimer.Stop();
            playback = null;
            offlinePlayback = false;
            playbackCancellation = null;
            PauseButton.IsEnabled = StopButton.IsEnabled = Progress.IsEnabled = FullscreenButton.IsEnabled = false;
            PauseButton.Content = "暂停";
            PauseButton.Tag = Asset("pause-disabled");
            StopButton.Tag = Asset("stop-disabled");
            UpdateLayoutState();
            PublishProgress(0, 0);
        }
    }

    private Task PlayStreamAsync(Stream stream, VideoFormat format, CancellationToken ct)
    {
        var engine = new Playback(Speeds[Speed.SelectedIndex])
        {
            Looping = Loop.IsChecked == true,
            Volume = muted ? 0 : (float)(Volume.Value / 100)
        };
        playback = engine;
        UpdateLayoutState();
        RevealPlaybackControls();
        PauseButton.IsEnabled = FullscreenButton.IsEnabled = true;
        PauseButton.Tag = Asset("pause");
        return Task.Run(() => engine.Run(stream, format,
            (frame, time, duration) => Dispatcher.Invoke(() => Draw(frame, time, duration), DispatcherPriority.Render, ct),
            message => Dispatcher.Invoke(() => Status.Text = message, DispatcherPriority.Normal, ct), ct));
    }

    private async Task<string?> MonitorAsync(AuthorizationClient client, LoginResponse current, Guid media,
        (GrantPayload Grant, bool Offline) lease, CancellationTokenSource playbackStop, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                VerifyPlaybackProtection();
                var now = DateTimeOffset.UtcNow;
                var expiry = lease.Offline ? lease.Grant.OfflineUntil : lease.Grant.RenewBy;
                if (now >= expiry) throw new InvalidOperationException("播放许可证已到期，请联网重新打开视频。");
                // Recheck the window and expiry once a second. Contact the authorization server every 30 seconds.
                var retryAt = now.AddSeconds(lease.Offline ? 10 : 30);
                do
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    VerifyPlaybackProtection();
                    now = DateTimeOffset.UtcNow;
                } while (now < retryAt && now < expiry.AddSeconds(-12));

                using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var remaining = expiry - now;
                if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("播放许可证已到期。");
                if (!Protocol.IsPermanentOwnerGrant(lease.Grant) || !lease.Offline)
                    request.CancelAfter(remaining);
                bool wasOffline = lease.Offline;
                lease = await client.GrantAsync(current, media, request.Token);
                if (lease.Offline != wasOffline)
                {
                    offlinePlayback = lease.Offline;
                    Status.Text = playback?.Paused == true ? "播放已暂停" : offlinePlayback ? "正在离线播放" : "正在播放";
                    StatusIcon.Source = Asset(lease.Offline ? "offline" : "online");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception error)
        {
            ClearScreen();
            await playbackStop.CancelAsync();
            return error is OperationCanceledException ? "播放许可证已到期。" : error.Message;
        }
    }

    private void VerifyPlaybackProtection()
    {
        Security.CheckDebugger();
        if (!protectedWindow || !Security.IsWindowProtected(window))
            throw new InvalidOperationException("录屏保护不可用，播放已停止。");
    }

    private void Draw(VideoFrame frame, double position, double duration)
    {
        if (playbackCancellation?.IsCancellationRequested != false) return;
        VerifyPlaybackProtection();
        if (surface?.PixelWidth != frame.Width || surface.PixelHeight != frame.Height)
        {
            ClearScreen();
            surface = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            Screen.Source = surface;
        }
        surface.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
        if (!seeking) PublishProgress(position, duration);
    }

    private unsafe void ClearScreen()
    {
        if (surface is null) return;
        surface.Lock();
        try
        {
            new Span<byte>((void*)surface.BackBuffer, surface.BackBufferStride * surface.PixelHeight).Clear();
            surface.AddDirtyRect(new Int32Rect(0, 0, surface.PixelWidth, surface.PixelHeight));
        }
        finally { surface.Unlock(); }
        Screen.Source = null;
        surface = null;
    }

    private void PublishProgress(double position, double duration)
    {
        updating = true;
        Progress.Maximum = duration > 0 ? duration : Math.Max(1, position);
        Progress.Value = Math.Clamp(position, 0, Progress.Maximum);
        Progress.IsEnabled = duration > 0 && playback is not null;
        Time.Text = FormatTime(position) + " / " + FormatTime(duration);
        updating = false;
    }

    private static string FormatTime(double seconds) =>
        TimeSpan.FromSeconds(Math.Clamp(seconds, 0, TimeSpan.MaxValue.TotalSeconds - 1))
            .ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");

    private void SeekStart(object sender, MouseButtonEventArgs e) => seeking = true;
    private void SeekEnd(object sender, MouseButtonEventArgs e)
    {
        if (seeking) playback?.Seek(Progress.Value);
        seeking = false;
        RevealPlaybackControls();
    }

    private void SeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updating) return;
        if (seeking) Time.Text = FormatTime(e.NewValue) + " / " + FormatTime(Progress.Maximum);
        else playback?.Seek(e.NewValue);
    }

    private void LoopChanged(object sender, RoutedEventArgs e)
    {
        if (playback is { } engine) engine.Looping = Loop.IsChecked == true;
    }

    private void PauseClick(object sender, RoutedEventArgs e)
    {
        if (playback is not { } engine) return;
        engine.Paused = !engine.Paused;
        PauseButton.Content = engine.Paused ? "继续" : "暂停";
        PauseButton.Tag = Asset(engine.Paused ? "play" : "pause");
        Status.Text = engine.Paused ? "播放已暂停" : offlinePlayback ? "正在离线播放" : "正在播放";
        RevealPlaybackControls();
    }

    private void StopClick(object sender, RoutedEventArgs e)
    {
        playbackCancellation?.Cancel();
        ClearScreen();
        playbackRequest++;
        Status.Text = "播放已停止。";
    }

    private void PlayerCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var card = (Border)sender;
        card.Clip = new RectangleGeometry(new Rect(e.NewSize), 16, 16);
    }

    private static readonly Brush PlaybackOverlayBrush = MakePlaybackOverlayBrush();

    private static Brush MakePlaybackOverlayBrush()
    {
        var brush = new LinearGradientBrush(Color.FromArgb(115, 14, 17, 25), Color.FromArgb(247, 14, 17, 25), 90);
        brush.Freeze();
        return brush;
    }

    private void OnPlaybackPointerActivity(object sender, MouseEventArgs e) => RevealPlaybackControls();

    private void RevealPlaybackControls()
    {
        controlsIdleTimer.Stop();
        if (playback is not { } engine) return;
        PlaybackControls.Visibility = Visibility.Visible;
        if (!engine.Paused) controlsIdleTimer.Start();
    }

    private void OnPlaybackControlsIdle(object? sender, EventArgs e)
    {
        if (playback is not { Paused: false })
        {
            controlsIdleTimer.Stop();
            return;
        }

        if (PlaybackControls.IsMouseCaptureWithin || Speed.IsDropDownOpen) return;
        controlsIdleTimer.Stop();
        PlaybackControls.Visibility = Visibility.Collapsed;
    }

    private void SpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (playback is { } engine && Speed.SelectedIndex >= 0)
            engine.Rate = Speeds[Speed.SelectedIndex];
    }

    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateVolume();

    private void MuteClick(object sender, RoutedEventArgs e)
    {
        muted = !muted;
        UpdateVolume();
    }

    private void UpdateVolume()
    {
        if (Volume is null || MuteButton is null) return;
        if (playback is { } engine) engine.Volume = muted ? 0 : (float)(Volume.Value / 100);
        MuteButton.Content = muted ? "取消静音" : "静音";
        Volume.ToolTip = $"音量 {Volume.Value:0}%（↑ / ↓）";
    }

    private void FullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void VideoClick(object sender, MouseButtonEventArgs e)
    {
        if (playback is null || e.ClickCount != 2) return;
        ToggleFullscreen();
        e.Handled = true;
    }

    private void ToggleFullscreen()
    {
        if (fullscreen)
        {
            WindowState = WindowState.Normal;
            ResizeMode = previousResizeMode;
            WindowStyle = previousWindowStyle;
            WindowState = previousWindowState;
            LayoutRoot.Margin = new Thickness(24, 0, 24, 0);
            Header.Visibility = Visibility.Visible;
            FullscreenButton.Content = "全屏";
        }
        else
        {
            previousWindowState = WindowState;
            previousWindowStyle = WindowStyle;
            previousResizeMode = ResizeMode;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            LayoutRoot.Margin = new Thickness(0);
            Header.Visibility = Visibility.Collapsed;
            FullscreenButton.Content = "退出全屏";
        }
        fullscreen = !fullscreen;
        UpdateLayoutState();
        RevealPlaybackControls();
    }

    private static BitmapImage Asset(string name) =>
        new(new Uri($"pack://application:,,,/Player;component/Assets/{name}.png"));

    private void UpdateLayoutState()
    {
        bool loggedIn = session is not null;
        bool isAdmin = session?.IsAdministrator == true;
        bool openingOrPlaying = playbackCancellation is not null;
        LoginPanel.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        LogoutButton.Visibility = RoleBadge.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
        PlaybackControls.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(PlaybackControls, playback is null ? 1 : 0);
        PlaybackControls.Background = playback is null ? (Brush)FindResource("SurfaceBrush") : PlaybackOverlayBrush;
        RoleName.Text = isAdmin ? "管理员" : offlineLogin ? "离线成员" : "普通成员";
        PlaylistActions.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
        UpdateSidePanels();
        StatusIcon.Source = Asset(!loggedIn ? "info" : offlineLogin ? "offline" : "online");
        VideoPrompt.Visibility = openingOrPlaying ? Visibility.Collapsed : Visibility.Visible;
        FileBadge.Visibility = openingOrPlaying ? Visibility.Visible : Visibility.Collapsed;
        BackdropLayer.Visibility = openingOrPlaying ? Visibility.Collapsed : Visibility.Visible;
        Backdrop.Source = Asset(!loggedIn ? "login-grid" : isAdmin ? "admin-grid" : "player-grid");
        PromptIcon.Source = Asset(loggedIn ? "folder" : "locked");
        PromptIcon.Width = PromptIcon.Height = loggedIn ? 34 : 48;
        PromptTile.Width = PromptTile.Height = loggedIn ? 70 : 92;
        PromptTile.BorderBrush = (Brush)FindResource(loggedIn ? "LineBrush" : "AccentBrush");
        PromptTile.CornerRadius = new CornerRadius(loggedIn ? 20 : 24);
        EmptyHint.Text = loggedIn ? "选择要播放的视频" : "登录后打开视频";
        FormatHint.Text = loggedIn ? "支持 MP4、MKV 和 .svideo" : "支持本地 MP4、MKV 和加密 .svideo 文件";
        OpenButton.IsEnabled = loggedIn;
        // Keep the central action as the primary idle affordance.
        PromptOpen.Visibility = loggedIn && !openingOrPlaying ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.Visibility = openingOrPlaying ? Visibility.Visible : Visibility.Collapsed;
        Footer.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateSidePanels()
    {
        bool showAdmin = session?.IsAdministrator == true && !fullscreen;
        bool showPlaylist = session is not null && playlist.Count > 0 && !fullscreen;
        SidePanels.Visibility = showAdmin || showPlaylist ? Visibility.Visible : Visibility.Collapsed;
        AdminPanel.Visibility = showAdmin ? Visibility.Visible : Visibility.Collapsed;
        PlaylistPanel.Visibility = showPlaylist ? Visibility.Visible : Visibility.Collapsed;
        PlaylistRow.Height = showPlaylist ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        AdminRow.Height = showAdmin ? new GridLength(1.35, GridUnitType.Star) : new GridLength(0);
        AdminPanel.Margin = new Thickness(0, showAdmin && showPlaylist ? 12 : 0, 0, 0);
    }

    private void OnPlaybackKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control && key == Key.O && OpenButton.IsEnabled)
        {
            OpenClick(sender, e);
            e.Handled = true;
            return;
        }

        if (modifiers != ModifierKeys.None || playback is null || Speed.IsDropDownOpen ||
            Keyboard.FocusedElement is TextBox or PasswordBox) return;
        if (PlaylistList.IsKeyboardFocusWithin && key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home) return;
        switch (key)
        {
            case Key.Space:
            case Key.K:
                if (!e.IsRepeat) PauseClick(sender, e);
                break;
            case Key.Left when Progress.IsEnabled:
                Progress.Value -= 5;
                break;
            case Key.Right when Progress.IsEnabled:
                Progress.Value += 5;
                break;
            case Key.Home when Progress.IsEnabled:
                Progress.Value = 0;
                break;
            case Key.Up:
            case Key.Down:
                muted = false;
                Volume.Value += key == Key.Up ? 5 : -5;
                UpdateVolume();
                break;
            case Key.M:
                if (!e.IsRepeat) MuteClick(sender, e);
                break;
            case Key.F:
            case Key.F11:
            case Key.Escape when fullscreen:
                if (!e.IsRepeat) ToggleFullscreen();
                break;
            case Key.OemOpenBrackets:
            case Key.OemCloseBrackets:
                Speed.SelectedIndex = Math.Clamp(Speed.SelectedIndex + (key == Key.OemOpenBrackets ? -1 : 1),
                    0, Speeds.Length - 1);
                break;
            case Key.R:
                Speed.SelectedIndex = Array.IndexOf(Speeds, 1);
                break;
            case Key.S:
                if (!e.IsRepeat) StopClick(sender, e);
                break;
            default:
                return;
        }
        RevealPlaybackControls();
        e.Handled = true;
    }

    private async void EncryptClick(object sender, RoutedEventArgs e)
    {
        if (session?.IsAdministrator != true) return;
        var source = new OpenFileDialog { Filter = "本地视频 (*.mp4;*.mkv)|*.mp4;*.mkv" };
        if (source.ShowDialog(this) != true) return;
        await EncryptVideoAsync(source.FileName);
    }

    private static string DefaultOutputFolder()
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos, Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrWhiteSpace(folder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos")
            : folder;
    }

    private void UpdateOutputFolderLabel()
    {
        OutputFolderLabel.Text = outputFolder ?? DefaultOutputFolder();
        OutputFolderLabel.ToolTip = OutputFolderLabel.Text;
    }

    private void ChooseOutputFolderClick(object sender, RoutedEventArgs e)
    {
        if (session?.IsAdministrator != true) return;
        var path = outputFolder ?? DefaultOutputFolder();
        var picker = new OpenFolderDialog { Title = "选择加密视频输出文件夹" };
        if (Directory.Exists(path)) picker.InitialDirectory = path;
        if (picker.ShowDialog(this) != true) return;
        outputFolder = picker.FolderName;
        UpdateOutputFolderLabel();
    }

    private void ResetOutputFolderClick(object sender, RoutedEventArgs e)
    {
        outputFolder = null;
        UpdateOutputFolderLabel();
    }

    private Task EncryptVideoAsync(string sourcePath) => RunAdminAsync(async (client, admin, ct) =>
    {
            var folder = outputFolder ?? DefaultOutputFolder();
            Directory.CreateDirectory(folder);
            var destination = Path.Combine(folder, Path.ChangeExtension(Path.GetFileName(sourcePath), ".svideo"));
            if (File.Exists(destination))
                throw new IOException($"输出文件已存在：{destination}。请选择其他输出文件夹，或移走同名文件。");

            var id = Guid.NewGuid();
            var key = RandomNumberGenerator.GetBytes(32);
            var staging = destination + "." + id.ToString("N") + ".partial";
            try
            {
                await Task.Run(() =>
                {
                    using var input = File.OpenRead(sourcePath);
                    var format = LocalVideo.Detect(input, sourcePath);
                    using var output = new FileStream(staging, FileMode.CreateNew);
                    EncryptedVideoWriter.Write(input, output, id, format, key, ct);
                    output.Flush(flushToDisk: true);
                }, ct);
                await client.ManageAsync(admin, AdminOperation.Register, id, ct, key: key);
                ct.ThrowIfCancellationRequested();
                File.Move(staging, destination);
                return $"加密文件已保存：{destination}";
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                File.Delete(staging);
            }
    });

    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (session?.IsAdministrator != true) return;
        var source = PickEncrypted();
        if (source is null) return;
        await RunAdminAsync(async (client, admin, ct) =>
        {
            using var input = File.OpenRead(source);
            var header = VideoHeader.Read(input);
            string extension = header.Format == VideoFormat.Mp4 ? ".mp4" : ".mkv";
            var destination = new SaveFileDialog
            {
                Filter = $"明文视频 (*{extension})|*{extension}", DefaultExt = extension,
                FileName = Path.GetFileNameWithoutExtension(source)
            };
            if (destination.ShowDialog(this) != true) return "已取消导出。";
            if (!Path.GetExtension(destination.FileName).Equals(extension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"此视频应导出为 {extension}。");
            var grant = await client.ManageAsync(admin, AdminOperation.Export, header.MediaId, ct) ??
                throw new InvalidDataException("服务器没有返回导出许可。");
            var key = client.OpenExportGrant(grant, admin, header.MediaId);
            string staging = destination.FileName + "." + Guid.NewGuid().ToString("N") + ".partial";
            var buffer = new byte[VideoHeader.DefaultBlockSize];
            try
            {
                using var reader = new EncryptedVideoReader(input, key, header.MediaId);
                await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, bufferSize: 1, useAsync: true))
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        int count = reader.Read(buffer);
                        if (count == 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                    output.Flush(flushToDisk: true);
                }
                await client.ManageAsync(admin, AdminOperation.Export, header.MediaId, ct);
                ct.ThrowIfCancellationRequested();
                File.Move(staging, destination.FileName, overwrite: true);
                return "明文视频已导出。";
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(buffer);
                File.Delete(staging);
            }
        });
    }

    private async void CreateMemberClick(object sender, RoutedEventArgs e)
    {
        string account = MemberAccount.Text.Trim();
        string password = MemberPassword.Password;
        if (account.Length == 0 || password.Length < Protocol.MinimumPasswordLength)
        {
            Status.Text = $"请输入成员账号和至少 {Protocol.MinimumPasswordLength} 个字符的初始密码。";
            return;
        }
        if (!int.TryParse(MemberDays.Text, out int days) || days is < 1 or > 3650 ||
            !int.TryParse(MemberDevices.Text, out int devices) || devices is < 1 or > 20)
        {
            Status.Text = "账号有效期须为 1–3650 天，设备上限须为 1–20 台。";
            return;
        }
        bool allowOffline = MemberOffline.IsChecked == true;
        MemberPassword.Clear();
        await RunAdminAsync(async (client, admin, ct) =>
        {
            await client.ManageAsync(admin, AdminOperation.CreateMember, Guid.Empty, ct, account: account, password: password,
                memberDays: days, maxDevices: devices, allowOffline: allowOffline);
            return $"成员已创建，有效期 {days} 天，限 {devices} 台设备。";
        });
    }

    private async void AssignClick(object sender, RoutedEventArgs e)
    {
        if (session?.IsAdministrator != true) return;
        string account = MemberAccount.Text.Trim();
        if (account.Length == 0) { Status.Text = "请先输入要授权的成员账号。"; return; }
        int? mediaDays = null;
        if (!string.IsNullOrWhiteSpace(MediaDays.Text))
        {
            if (!int.TryParse(MediaDays.Text, out int days) || days is < 1 or > 3650)
            {
                Status.Text = "单个视频授权须为 1–3650 天，留空时随账号有效期。";
                return;
            }
            mediaDays = days;
        }
        var path = PickEncrypted();
        if (path is null) return;
        await RunAdminAsync(async (client, admin, ct) =>
        {
            using var input = File.OpenRead(path);
            var header = VideoHeader.Read(input);
            await client.ManageAsync(admin, AdminOperation.Assign, header.MediaId, ct, account: account, mediaDays: mediaDays);
            return mediaDays is int days ? $"已授权该成员播放此视频，有效期 {days} 天。" : "已授权该成员播放此视频。";
        });
    }

    private string? PickEncrypted()
    {
        var picker = new OpenFileDialog { Filter = "加密视频 (*.svideo)|*.svideo" };
        return picker.ShowDialog(this) == true ? picker.FileName : null;
    }

    private async Task RunAdminAsync(Func<AuthorizationClient, LoginResponse, CancellationToken, Task<string>> action)
    {
        if (authorization is not { } client || session is not { IsAdministrator: true } admin ||
            adminCancellation is not null) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        adminCancellation = cancel;
        AdminPanel.IsEnabled = false;
        Status.Text = "正在处理…";
        try
        {
            var message = await action(client, admin, cancel.Token);
            if (!cancel.IsCancellationRequested) Status.Text = message;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (System.Net.Http.HttpRequestException error) when (!cancel.IsCancellationRequested)
        {
            if (error is MediaOwnerException)
            {
                Status.Text = error.Message;
                return;
            }
            switch (error.StatusCode)
            {
                case System.Net.HttpStatusCode.Forbidden:
                case System.Net.HttpStatusCode.Unauthorized:
                    if (session is not null) session = session with { IsAdministrator = false };
                    UpdateLayoutState();
                    Status.Text = "管理员会话或权限已失效，请重新登录。";
                    break;
                case System.Net.HttpStatusCode.Conflict:
                    Status.Text = "账号或文件登记已存在。";
                    break;
                case System.Net.HttpStatusCode.NotFound:
                    Status.Text = "找不到该视频或成员。";
                    break;
                default:
                    Status.Text = "管理员操作失败，请检查网络连接及输入。";
                    break;
            }
        }
        catch (Exception error)
        {
            if (!cancel.IsCancellationRequested) Status.Text = error.Message;
        }
        finally
        {
            adminCancellation = null;
            AdminPanel.IsEnabled = true;
        }
    }

    private void LogoutClick(object sender, RoutedEventArgs e)
    {
        playbackCancellation?.Cancel();
        ClearScreen();
        session = null;
        playbackRequest++;
        playlist.Clear();
        PlaylistList.ItemsSource = null;
        adminCancellation?.Cancel();
        AdminPanel.Visibility = Visibility.Collapsed;
        MemberAccount.Clear();
        MemberPassword.Clear();
        MemberDays.Text = "365";
        MemberDevices.Text = "1";
        MemberOffline.IsChecked = true;
        MediaDays.Clear();
        OpenButton.IsEnabled = false;
        LogoutButton.Visibility = Visibility.Collapsed;
        offlineLogin = false;
        UpdateLayoutState();
        Password.Focus();
        Status.Text = "已退出登录。";
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        controlsIdleTimer.Stop();
        lifetime.Cancel();
        ClearScreen();
        authorization?.Dispose();
    }
}
