using System.Security.Cryptography;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using Uri = Android.Net.Uri;
using OperationCanceledException = System.OperationCanceledException;
using Orientation = Android.Widget.Orientation;
using Path = System.IO.Path;
using Stream = System.IO.Stream;

namespace SecureVideo.Mobile;

[Activity(Label = "加密视频播放器", MainLauncher = true, Exported = true,
    Theme = "@android:style/Theme.Material.NoActionBar", ConfigurationChanges =
        Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize)]
public sealed class MainActivity : Activity
{
    private const int PickMedia = 10;
    private const int PickConfig = 11;
    private readonly List<MediaItem> items = [];
    private readonly Handler dispatcher = new(Looper.MainLooper!);
    private readonly CancellationTokenSource lifetime = new();
    private TextView status = null!;
    private ListView list = null!;
    private SurfaceView screen = null!;
    private SeekBar progress = null!;
    private Button playPause = null!;
    private MediaPlayer? player;
    private StreamMediaSource? source;
    private CancellationTokenSource? playing;
    private MobileAuthorization? authorization;
    private bool prepared;
    private bool seeking;
    private bool authenticating;
    private bool surfaceReady;
    private const string ConfigName = "player.json";
    private sealed record MediaItem(Uri Uri, string Document, ZipVideoEntry? Archived)
    {
        public string Name => Archived is null ? Document : Archived.Name;
        public override string ToString() => Archived is null ? Document : $"{Archived.Name}\n{Document}";
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.Secure);
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(14), Dp(10), Dp(14), Dp(8));
        root.SetBackgroundColor(Color.Rgb(14, 17, 25));
        SetContentView(root);

        var title = new TextView(this) { Text = "加密视频播放器", TextSize = 21 };
        title.SetTextColor(Color.Rgb(241, 243, 255));
        root.AddView(title);
        var actions = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        root.AddView(actions);
        AddButton(actions, "添加视频 / ZIP", () => Browse(PickMedia));
        AddButton(actions, "授权设置", () => Browse(PickConfig));
        AddButton(actions, "登录", ShowLogin);
        screen = new SurfaceView(this);
        screen.Holder!.AddCallback(new DisplaySurface(this));
        root.AddView(screen, new LinearLayout.LayoutParams(-1, 0, 3));
        progress = new SeekBar(this) { Enabled = false };
        progress.StartTrackingTouch += (_, _) => seeking = true;
        progress.StopTrackingTouch += (_, _) =>
        {
            seeking = false;
            if (prepared) player?.SeekTo(progress.Progress);
        };
        root.AddView(progress);
        var controls = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        root.AddView(controls);
        playPause = AddButton(controls, "暂停", TogglePlayback);
        playPause.Enabled = false;
        AddButton(controls, "停止", StopPlayback);
        status = new TextView(this) { Text = "选择本地视频或 ZIP 压缩包" };
        status.SetTextColor(Color.Rgb(175, 179, 255));
        root.AddView(status);
        list = new ListView(this);
        list.ItemClick += async (_, e) =>
        {
            if (e.Position >= 0 && e.Position < items.Count) await Play(items[e.Position]);
        };
        root.AddView(list, new LinearLayout.LayoutParams(-1, 0, 2));

        try
        {
            string path = ConfigPath();
            if (File.Exists(path))
            {
                var config = JsonSerializer.Deserialize<MobileConfig>(File.ReadAllText(path)) ??
                    throw new InvalidDataException("授权配置无效。");
                authorization = new MobileAuthorization(config, FilesDir!.AbsolutePath);
            }
        }
        catch (Exception error) { status.Text = error.Message; }
        UpdateProgress();
    }

    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + .5f);
    private string ConfigPath() => Path.Combine(FilesDir!.AbsolutePath, ConfigName);

    private Button AddButton(LinearLayout row, string label, Action click)
    {
        var button = new Button(this) { Text = label, TextSize = 12 };
        button.SetAllCaps(false);
        button.Click += (_, _) => click();
        row.AddView(button, new LinearLayout.LayoutParams(0, -2, 1));
        return button;
    }

    private void Browse(int request)
    {
        if (authenticating) return;
        using var picker = new Intent(Intent.ActionOpenDocument);
        picker.AddCategory(Intent.CategoryOpenable);
        picker.SetType("*/*");
        StartActivityForResult(picker, request);
    }

    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data is not { } uri || lifetime.IsCancellationRequested) return;
        try
        {
            if (requestCode == PickConfig)
            {
                StopPlayback();
                using var input = ContentResolver!.OpenInputStream(uri) ?? throw new IOException("无法读取配置。");
                var configBytes = new byte[16385];
                int count = input.ReadAtLeast(configBytes, configBytes.Length, false);
                if (count > 16384) throw new InvalidDataException("授权配置超过大小限制。");
                var config = JsonSerializer.Deserialize<MobileConfig>(configBytes.AsSpan(0, count)) ??
                    throw new InvalidDataException("授权配置无效。");
                var client = new MobileAuthorization(config, FilesDir!.AbsolutePath);
                try { File.WriteAllText(ConfigPath(), JsonSerializer.Serialize(config)); }
                catch { client.Dispose(); throw; }
                authorization?.Dispose();
                authorization = client;
                status.Text = "授权服务器已设置，可以登录。";
                return;
            }

            if (requestCode != PickMedia) return;
            string name = DisplayName(uri);
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                status.Text = "正在读取 ZIP 目录…";
                var videos = await Task.Run(() =>
                {
                    using var input = new DocumentFile(ContentResolver!, uri);
                    if (VideoHeader.ReadIfEncrypted(input.Stream) is not null) return null;
                    return ZipVideo.List(input.Stream, lifetime.Token);
                }, lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                if (videos is not null)
                {
                    AddItems(videos.Select(entry => new MediaItem(uri, name, entry)));
                    status.Text = videos.Count == 0 ? "ZIP 中没有支持的视频。" : $"已列出 {videos.Count} 个条目。点击条目播放。";
                    return;
                }
            }
            var item = new MediaItem(uri, name, null);
            AddItems([item]);
            await Play(item);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!lifetime.IsCancellationRequested) status.Text = error.Message; }
    }

    private string DisplayName(Uri uri)
    {
        using var cursor = ContentResolver!.Query(uri, ["_display_name"], null, null, null);
        return cursor is not null && cursor.MoveToFirst() ? cursor.GetString(0) ?? "视频" : uri.LastPathSegment ?? "视频";
    }

    private void AddItems(IEnumerable<MediaItem> results)
    {
        foreach (var item in results)
            if (!items.Any(old => old.Uri.Equals(item.Uri) && old.Archived?.Index == item.Archived?.Index))
                items.Add(item);
        items.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        list.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleListItem1,
            items.Select(item => item.ToString()).ToArray());
    }

    private void ShowLogin()
    {
        if (authenticating) return;
        if (authorization is null) { status.Text = "请先通过「授权设置」导入服务器配置。"; return; }
        var form = new LinearLayout(this) { Orientation = Orientation.Vertical };
        form.SetPadding(Dp(18), 0, Dp(18), 0);
        var account = new EditText(this) { Hint = "账号", InputType = InputTypes.ClassText };
        var password = new EditText(this) { Hint = "密码", InputType = InputTypes.ClassText | InputTypes.TextVariationPassword };
        form.AddView(account);
        form.AddView(password);
        var dialog = new AlertDialog.Builder(this).SetTitle("登录授权账号")!.SetView(form)!
            .SetNegativeButton("取消", (_, _) => { })!
            .SetPositiveButton("登录", async (_, _) =>
            {
                string secret = password.Text ?? "";
                password.Text = "";
                await Login(account.Text ?? "", secret);
            })!.Create()!;
        dialog.Show();
        dialog.Window?.AddFlags(WindowManagerFlags.Secure);
    }

    private async Task Login(string account, string password)
    {
        if (authorization is not { } client || authenticating) return;
        authenticating = true;
        StopPlayback();
        status.Text = "正在登录…";
        try { await client.Login(account, password, lifetime.Token); status.Text = client.Offline ? "已离线登录" : "已登录"; }
        catch (Exception error) { if (!lifetime.IsCancellationRequested) status.Text = error.Message; }
        finally { authenticating = false; }
    }

    private async Task Play(MediaItem item)
    {
        StopPlayback();
        if (!surfaceReady) { status.Text = "视频画面尚未就绪，请再点一次。"; return; }
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        playing = request;
        var token = request.Token;
        DocumentFile? file = null;
        Stream? archive = null;
        Stream? decrypted = null;
        try
        {
            status.Text = "正在打开 " + item.Name;
            file = new DocumentFile(ContentResolver!, item.Uri);
            archive = item.Archived is { } archived ? ZipVideo.Open(file.Stream, archived, token) : null;
            var input = archive ?? file.Stream;
            (GrantPayload Grant, bool Offline)? lease = null;
            MobileAuthorization? client = null;
            var header = await Task.Run(() => VideoHeader.ReadIfEncrypted(input), token);
            if (header is not null)
            {
                client = authorization;
                if (client?.Session is null) throw new InvalidOperationException("请先设置授权服务器并登录。");
                lease = await client.Grant(header.MediaId, token);
                var key = client.Unwrap(lease.Value.Grant);
                try { decrypted = new EncryptedVideoReader(input, key, header.MediaId); }
                finally { CryptographicOperations.ZeroMemory(key); }
                input = decrypted;
            }
            token.ThrowIfCancellationRequested();

            var media = new MediaPlayer();
            player = media;
            source = new StreamMediaSource(input, file);
            file = null;
            archive = decrypted = null;
            media.SetDataSource(source);
            media.SetDisplay(screen.Holder);
            media.Prepared += (_, _) =>
            {
                if (player != media) return;
                prepared = true;
                progress.Enabled = media.Duration > 0;
                progress.Max = Math.Max(media.Duration, 1);
                playPause.Enabled = true;
                playPause.Text = "暂停";
                media.Start();
                status.Text = item.Name;
            };
            media.Completion += (_, _) =>
            {
                if (player != media) return;
                StopPlayback();
                status.Text = "播放结束";
            };
            media.Error += (_, e) =>
            {
                e.Handled = true;
                if (player != media) return;
                StopPlayback();
                status.Text = "此视频无法播放。请检查文件、压缩方式和设备支持的编码。";
            };
            media.PrepareAsync();
            if (lease is { } granted) _ = MonitorGrant(client!, granted, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (playing == request)
            {
                StopPlayback();
                status.Text = error.Message;
            }
        }
        finally
        {
            decrypted?.Dispose();
            archive?.Dispose();
            file?.Dispose();
        }
    }

    private async Task MonitorGrant(MobileAuthorization client, (GrantPayload Grant, bool Offline) lease, CancellationToken token)
    {
        try
        {
            while (true)
            {
                var deadline = lease.Offline ? lease.Grant.OfflineUntil : lease.Grant.RenewBy;
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("播放许可已到期。");
                var delay = TimeSpan.FromSeconds(Math.Min(lease.Offline ? 10 : 30, Math.Max(0, remaining.TotalSeconds - 11)));
                await Task.Delay(delay, token);
                using var renewal = CancellationTokenSource.CreateLinkedTokenSource(token);
                remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("播放许可已到期。");
                if (!lease.Offline || !Protocol.IsPermanentOwnerGrant(lease.Grant)) renewal.CancelAfter(remaining);
                lease = await client.Grant(lease.Grant.Media, renewal.Token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (token.IsCancellationRequested) return;
            StopPlayback();
            status.Text = "在线授权验证失败，播放已停止。";
        }
    }

    private void UpdateProgress()
    {
        if (lifetime.IsCancellationRequested) return;
        if (prepared && !seeking && player is { } media) progress.Progress = media.CurrentPosition;
        dispatcher.PostDelayed(UpdateProgress, 400);
    }

    private void TogglePlayback()
    {
        if (!prepared || player is not { } media) return;
        if (media.IsPlaying) { media.Pause(); playPause.Text = "继续"; }
        else { media.Start(); playPause.Text = "暂停"; }
    }

    private void StopPlayback()
    {
        var request = playing;
        playing = null;
        request?.Cancel();
        request?.Dispose();
        prepared = false;
        seeking = false;
        var media = player;
        player = null;
        if (media is not null) { media.Release(); media.Dispose(); }
        if (source is not null) { source.Close(); source.Dispose(); source = null; }
        progress.Enabled = false;
        progress.Progress = 0;
        playPause.Enabled = false;
        playPause.Text = "暂停";
    }

    protected override void OnStop()
    {
        StopPlayback();
        base.OnStop();
    }

    protected override void OnDestroy()
    {
        lifetime.Cancel();
        dispatcher.RemoveCallbacksAndMessages(null);
        StopPlayback();
        authorization?.Dispose();
        base.OnDestroy();
    }

    private sealed class DisplaySurface(MainActivity activity) : Java.Lang.Object, ISurfaceHolderCallback
    {
        public void SurfaceCreated(ISurfaceHolder holder) => activity.surfaceReady = true;
        public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) { }
        public void SurfaceDestroyed(ISurfaceHolder holder)
        {
            activity.surfaceReady = false;
            activity.StopPlayback();
        }
    }
}
