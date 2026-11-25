using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using CipherMode = Javax.Crypto.CipherMode;
using Rsa = System.Security.Cryptography.RSA;

namespace SecureVideo.Mobile;

internal sealed record MobileConfig(string Server, string SigningPublicKey);

internal sealed class MobileAuthorization : IDisposable
{
    private readonly HttpClient http;
    private readonly Rsa signer = Rsa.Create();
    private readonly Rsa device;
    private readonly byte[] publicKey;
    private readonly string cacheDirectory;
    private sealed record CachedLogin(LoginResponse Session, byte[] Salt, byte[] Hash, DateTimeOffset Until);
    public bool Offline { get; private set; }
    private readonly object keyLock = new();
    public LoginResponse? Session { get; private set; }

    public MobileAuthorization(MobileConfig config, string files)
    {
        if (!Uri.TryCreate(config.Server, UriKind.Absolute, out var url) || url.Scheme != "https" ||
            url.UserInfo.Length > 0 || url.AbsolutePath != "/" || url.Query.Length > 0 || url.Fragment.Length > 0)
            throw new InvalidDataException("请导入使用 HTTPS 的授权服务器配置。");
        signer.ImportFromPem(config.SigningPublicKey);
        if (signer.KeySize < 3072) throw new CryptographicException("服务器公钥无效。");
        string server = Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()));
        cacheDirectory = Path.Combine(files, server);
        Directory.CreateDirectory(cacheDirectory);
        device = OpenDevice(files, server);
        publicKey = device.ExportSubjectPublicKeyInfo();
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = url, Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private static Rsa OpenDevice(string files, string server)
    {
        const string alias = "SecureVideo.DeviceProtection";
        using var keyStore = KeyStore.GetInstance("AndroidKeyStore")!;
        keyStore.Load(null);
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        string path = Path.Combine(files, server + ".device");
        if (!keyStore.ContainsAlias(alias))
        {
            using var generator = KeyGenerator.GetInstance("AES", "AndroidKeyStore")!;
            using var builder = new KeyGenParameterSpec.Builder(alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt);
            using var spec = builder.SetBlockModes(KeyProperties.BlockModeGcm)!
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!.SetKeySize(256)!.Build();
            generator.Init(spec);
            using var generated = generator.GenerateKey();
        }

        using var key = keyStore.GetKey(alias, null)!;
        var rsa = Rsa.Create(2048);
        byte[]? privateBytes = null;
        try
        {
            if (File.Exists(path))
            {
                byte[] encrypted = File.ReadAllBytes(path);
                if (encrypted.Length <= 28) throw new CryptographicException("设备密钥文件无效。");
                using var parameters = new GCMParameterSpec(128, encrypted[..12]);
                cipher.Init(CipherMode.DecryptMode, key, parameters);
                privateBytes = cipher.DoFinal(encrypted[12..])!;
                rsa.ImportPkcs8PrivateKey(privateBytes, out _);
            }
            else
            {
                privateBytes = rsa.ExportPkcs8PrivateKey();
                cipher.Init(CipherMode.EncryptMode, key);
                byte[] nonce = cipher.GetIV()!;
                if (nonce.Length != 12) throw new CryptographicException("设备密钥加密失败。");
                File.WriteAllBytes(path + ".tmp", [.. nonce, .. cipher.DoFinal(privateBytes)!]);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            return rsa;
        }
        catch { rsa.Dispose(); throw; }
        finally { if (privateBytes is not null) CryptographicOperations.ZeroMemory(privateBytes); }
    }

    public async Task Login(string account, string password, CancellationToken token)
    {
        Session = null;
        Offline = false;
        string cache = "login-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Protocol.Account(account))));
        try
        {
            var session = await Send<LoginResponse>("api/login",
                new LoginRequest(account, password, Convert.ToBase64String(publicKey)), null, token);
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Protocol.Password(password, salt);
            try
            {
                Save(cache, new CachedLogin(session, salt, hash,
                    session.IsAdministrator ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddHours(8)));
            }
            finally { CryptographicOperations.ZeroMemory(hash); }
            Session = session;
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            File.Delete(Path.Combine(cacheDirectory, cache));
            throw new InvalidOperationException("账号、设备或授权未通过。", error);
        }
        catch (Exception error) when (IsUnavailable(error, token))
        {
            var cached = Load<CachedLogin>(cache);
            if (cached is null || cached.Until <= DateTimeOffset.UtcNow || !Protocol.CheckPassword(password, cached.Salt, cached.Hash))
                throw new InvalidOperationException("无法连接授权服务器，请先联网登录。", error);
            Session = cached.Session;
            Offline = true;
        }
    }

    public async Task<(GrantPayload Grant, bool Offline)> Grant(Guid media, CancellationToken token)
    {
        var session = Session ?? throw new InvalidOperationException("请先登录授权账号。");
        string cache = $"grant-{session.Subject:N}-{media:N}";
        try
        {
            var challenge = await Send<ChallengeResponse>("api/challenge", null, session.Token, token);
            byte[] proof;
            lock (keyLock)
                proof = device.SignData(Protocol.Proof(media, challenge.Nonce), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            var signed = await Send<SignedGrant>("api/grant", new GrantRequest(media, challenge.Id, proof), session.Token, token);
            var grant = Verify(signed, session, media, offline: false);
            Save(cache, signed);
            return (grant, false);
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            // An expired online session needs a fresh login. A denial of the video itself clears its cached grant.
            if (error.StatusCode == HttpStatusCode.Forbidden) File.Delete(Path.Combine(cacheDirectory, cache));
            throw new InvalidOperationException(error.StatusCode == HttpStatusCode.Forbidden
                ? "权限不足，当前账号无权播放此加密视频。"
                : "登录已失效，请重新登录。", error);
        }
        catch (Exception error) when (IsUnavailable(error, token))
        {
            var cached = Load<SignedGrant>(cache) ??
                throw new InvalidOperationException("这个视频尚未在此设备取得离线授权。", error);
            return (Verify(cached, session, media, offline: true), true);
        }
    }

    private GrantPayload Verify(SignedGrant signed, LoginResponse session, Guid media, bool offline)
    {
        GrantPayload grant;
        lock (keyLock) grant = Protocol.Verify(signed, signer);
        var now = DateTimeOffset.UtcNow;
        bool permanent = Protocol.IsPermanentOwnerGrant(grant);
        if (offline && grant.OfflineUntil <= grant.Issued)
            throw new InvalidOperationException("此账号需要联网播放加密视频。");
        if (grant.Media != media || grant.Subject != session.Subject || grant.Device != Protocol.Device(publicKey) ||
            grant.MediaOwner is not Guid owner || owner == Guid.Empty || (session.IsAdministrator && owner != session.Subject) ||
            (!permanent && grant.OfflineUntil > grant.Issued.AddHours(1)) ||
            (!(offline && permanent) && (grant.Issued > now.AddMinutes(2) ||
                now >= (offline ? grant.OfflineUntil : grant.RenewBy))))
            throw new CryptographicException("播放许可证无效或已到期。");
        return grant;
    }

    public byte[] Unwrap(GrantPayload grant)
    {
        lock (keyLock) return device.Decrypt(grant.WrappedKey, RSAEncryptionPadding.OaepSHA256);
    }

    private async Task<T> Send<T>(string path, object? payload, string? session, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        if (session is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
        using var response = await http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token) ??
            throw new InvalidDataException("授权服务器返回了无效数据。");
    }

    private static bool IsUnavailable(Exception error, CancellationToken token) =>
        !token.IsCancellationRequested && (error is OperationCanceledException ||
        error is HttpRequestException { StatusCode: null, HttpRequestError: HttpRequestError.ConnectionError or
            HttpRequestError.NameResolutionError or HttpRequestError.Unknown } ||
        error is HttpRequestException { StatusCode: HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout });

    private T? Load<T>(string name) where T : class
    {
        try
        {
            var encrypted = File.ReadAllBytes(Path.Combine(cacheDirectory, name));
            if (encrypted.Length is <= 28 or > 16384) return null;
            using var store = KeyStore.GetInstance("AndroidKeyStore")!;
            store.Load(null);
            using var key = store.GetKey("SecureVideo.DeviceProtection", null)!;
            using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            using var parameters = new GCMParameterSpec(128, encrypted[..12]);
            cipher.Init(CipherMode.DecryptMode, key, parameters);
            cipher.UpdateAAD(Encoding.UTF8.GetBytes(Path.GetFileName(cacheDirectory) + ":" + name));
            var plain = cipher.DoFinal(encrypted[12..])!;
            try { return JsonSerializer.Deserialize<T>(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception error) when (error is IOException or Java.Security.GeneralSecurityException or JsonException)
        {
            return null;
        }
    }

    private void Save<T>(string name, T value)
    {
        using var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        using var key = store.GetKey("SecureVideo.DeviceProtection", null)!;
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.EncryptMode, key);
        cipher.UpdateAAD(Encoding.UTF8.GetBytes(Path.GetFileName(cacheDirectory) + ":" + name));
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        var path = Path.Combine(cacheDirectory, name);
        try
        {
            File.WriteAllBytes(path + ".tmp", [.. cipher.GetIV()!, .. cipher.DoFinal(plain)!]);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            File.Delete(path + ".tmp");
        }
    }

    public void Dispose()
    {
        http.Dispose();
        lock (keyLock) { signer.Dispose(); device.Dispose(); }
    }
}
