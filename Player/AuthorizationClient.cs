using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SecureVideo.Player;

internal sealed record PlayerConfig(string Server, string SigningPublicKey);

internal sealed class MediaOwnerException() : HttpRequestException(
    "权限不足，当前管理员不能管理这部加密视频。", null, HttpStatusCode.Forbidden);

internal sealed class AuthorizationClient : IDisposable
{
    private readonly HttpClient http;
    private readonly RSA signing = RSA.Create();
    private readonly RSACng device;
    private readonly string directory;
    private readonly string fingerprint;
    private readonly object cryptoGate = new();
    private readonly byte[] publicKey;
    public string? LastAccount => Load<string>("last-account");
    private const string PlaybackDenied = "权限不足，当前账号无权播放此加密视频。";
    private sealed record CachedLogin(LoginResponse Session, byte[] Salt, byte[] Hash, DateTimeOffset Until);
    private sealed record AdminError(string Code);

    public AuthorizationClient(PlayerConfig config)
    {
        if (!Uri.TryCreate(config.Server, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("授权服务器必须使用 HTTPS，本机测试可使用 HTTP。");

        signing.ImportFromPem(config.SigningPublicKey);
        if (signing.KeySize < 3072) throw new CryptographicException("服务器签名公钥无效。");
        var serverId = Convert.ToHexString(SHA256.HashData(signing.ExportSubjectPublicKeyInfo()));
        directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SecureVideo", serverId);
        Directory.CreateDirectory(directory);
        using var key = OpenDevice("SecureVideo." + serverId);
        device = new RSACng(key);
        publicKey = device.ExportSubjectPublicKeyInfo();
        fingerprint = Protocol.Device(publicKey);
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = !uri.IsLoopback })
        {
            BaseAddress = uri,
            Timeout = TimeSpan.FromSeconds(6)
        };
    }

    private static CngKey OpenDevice(string name)
    {
        var software = CngProvider.MicrosoftSoftwareKeyStorageProvider;
        if (CngKey.Exists(name, software)) return CngKey.Open(name, software);
        try
        {
            using var platformKey = GetOrCreateDevice(name, CngProvider.MicrosoftPlatformCryptoProvider);
            using var candidate = new RSACng(platformKey);
            var probe = new byte[32];
            candidate.SignData(probe, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            candidate.Decrypt(candidate.Encrypt(probe, RSAEncryptionPadding.OaepSHA256), RSAEncryptionPadding.OaepSHA256);
            return CngKey.Open(name, CngProvider.MicrosoftPlatformCryptoProvider);
        }
        catch (Exception error) when (error is CryptographicException or PlatformNotSupportedException)
        {
            return GetOrCreateDevice(name, software);
        }
    }

    private static CngKey GetOrCreateDevice(string name, CngProvider provider)
    {
        if (CngKey.Exists(name, provider)) return CngKey.Open(name, provider);
        var options = new CngKeyCreationParameters
        {
            Provider = provider,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing | CngKeyUsages.Decryption
        };
        options.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
        try { return CngKey.Create(CngAlgorithm.Rsa, name, options); }
        catch (CryptographicException) when (CngKey.Exists(name, provider)) { return CngKey.Open(name, provider); }
    }

    public async Task<(LoginResponse Session, bool Offline)> LoginAsync(string account, string password, CancellationToken ct)
    {
        string cacheName = "login-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Protocol.Account(account))));
        try
        {
            var session = await SendAsync<LoginResponse>("api/login", new LoginRequest(account, password,
                Convert.ToBase64String(publicKey)), null, ct);
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Protocol.Password(password, salt);
            try { Save(cacheName, new CachedLogin(session, salt, hash,
                session.IsAdministrator ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddHours(8))); }
            finally { CryptographicOperations.ZeroMemory(hash); }
            RememberAccount(account);
            return (session, false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            File.Delete(CachePath(cacheName));
            throw new InvalidOperationException("账号、密码或设备授权无效。", ex);
        }
        catch (Exception ex) when (IsUnavailable(ex, ct))
        {
            var cached = Load<CachedLogin>(cacheName);
            if (cached is null || cached.Until <= DateTimeOffset.UtcNow ||
                !Protocol.CheckPassword(password, cached.Salt, cached.Hash))
                throw new InvalidOperationException(http.BaseAddress!.IsLoopback
                    ? "本机授权服务未连接。请使用 Start-Player.cmd 启动。"
                    : "无法连接配置的授权服务器，请检查网络和服务器地址。", ex);
            RememberAccount(account);
            return (cached.Session with { IsAdministrator = false }, true);
        }
    }

    public async Task<(GrantPayload Grant, bool Offline)> GrantAsync(LoginResponse session, Guid media, CancellationToken ct)
    {
        string cacheName = $"grant-{session.Subject:N}-{media:N}";
        try
        {
            var challenge = await SendAsync<ChallengeResponse>("api/challenge", null, session.Token, ct);
            byte[] proof;
            lock (cryptoGate)
                proof = device.SignData(Protocol.Proof(media, challenge.Nonce), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            var signed = await SendAsync<SignedGrant>("api/grant", new GrantRequest(media, challenge.Id, proof), session.Token, ct);
            var grant = Verify(signed, session, media, offline: false);
            Save(cacheName, signed);
            return (grant, false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (ex.StatusCode == HttpStatusCode.Forbidden) File.Delete(CachePath(cacheName));
            throw new InvalidOperationException(ex.StatusCode == HttpStatusCode.Unauthorized ?
                "登录已失效，请重新登录。" : PlaybackDenied, ex);
        }
        catch (Exception ex) when (IsUnavailable(ex, ct))
        {
            var signed = Load<SignedGrant>(cacheName);
            if (signed is null) throw new InvalidOperationException("权限不足，离线播放需要先联网获取此视频的授权。", ex);
            return (Verify(signed, session, media, offline: true), true);
        }
    }

    public async Task<SignedGrant?> ManageAsync(LoginResponse session, AdminOperation operation, Guid media,
        CancellationToken ct, byte[]? key = null, string? account = null, string? password = null,
        int? memberDays = null, int? maxDevices = null, bool? allowOffline = null, int? mediaDays = null)
    {
        var challenge = await SendAsync<ChallengeResponse>("api/challenge", null, session.Token, ct);
        byte[] proof;
        lock (cryptoGate)
            proof = device.SignData(Protocol.AdminProof(operation, media, challenge.Nonce), HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/admin");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Content = JsonContent.Create(new AdminRequest(operation, media, challenge.Id, proof, key, account, password,
            memberDays, maxDevices, allowOffline, mediaDays));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        if (response.StatusCode == HttpStatusCode.Forbidden && response.Content.Headers.ContentType?.MediaType == "application/json" &&
            (await response.Content.ReadFromJsonAsync<AdminError>(cancellationToken: ct))?.Code == Protocol.MediaOwnerRequired)
            throw new MediaOwnerException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SignedGrant>(cancellationToken: ct) ??
            throw new InvalidDataException("授权服务返回了无效数据。");
    }

    public byte[] OpenExportGrant(SignedGrant signed, LoginResponse session, Guid media) =>
        Unwrap(Verify(signed, session, media, offline: false));

    public byte[] Unwrap(GrantPayload grant)
    {
        lock (cryptoGate) return device.Decrypt(grant.WrappedKey, RSAEncryptionPadding.OaepSHA256);
    }

    private GrantPayload Verify(SignedGrant signed, LoginResponse session, Guid media, bool offline)
    {
        GrantPayload grant;
        lock (cryptoGate) grant = Protocol.Verify(signed, signing);
        // Reject cached grants issued before administrator ownership was enforced.
        if (grant.MediaOwner is not Guid owner || owner == Guid.Empty ||
            (session.IsAdministrator && owner != session.Subject))
            throw new InvalidOperationException(PlaybackDenied);
        if (offline && grant.OfflineUntil <= grant.Issued)
            throw new InvalidOperationException("此账号需要联网播放加密视频。");
        var now = DateTimeOffset.UtcNow;
        bool permanent = Protocol.IsPermanentOwnerGrant(grant);
        if (grant.Subject != session.Subject || grant.Media != media || grant.Device != fingerprint ||
            (!permanent && grant.OfflineUntil > grant.Issued.AddHours(1)) ||
            (!(offline && permanent) && (now < grant.Issued.AddMinutes(-2) ||
                now >= (offline ? grant.OfflineUntil : grant.RenewBy))))
            throw new CryptographicException("播放许可证已过期或与账号、设备、媒体不匹配。");
        return grant;
    }

    private async Task<T> SendAsync<T>(string route, object? body, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct) ??
            throw new InvalidDataException("授权服务返回了无效数据。");
    }

    private static bool IsUnavailable(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && (ex is OperationCanceledException ||
        ex is HttpRequestException { StatusCode: null, HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError } ||
        ex is HttpRequestException { StatusCode: HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout });

    private void RememberAccount(string account)
    {
        try { Save("last-account", account.Trim()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException) { }
    }

    private string CachePath(string name) => Path.Combine(directory, name + ".dat");

    private T? Load<T>(string name) where T : class
    {
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(CachePath(name)), null, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<T>(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { return null; }
    }

    private void Save<T>(string name, T value)
    {
        string file = CachePath(name);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            File.Delete(temporary);
        }
    }

    public void Dispose()
    {
        http.Dispose();
        lock (cryptoGate)
        {
            device.Dispose();
            signing.Dispose();
        }
    }
}
