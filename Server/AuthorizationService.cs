using System.Security.Cryptography;

namespace SecureVideo.Server;

internal sealed class AuthorizationService(Vault vault, ILogger logger)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Session> sessions = [];
    private readonly Dictionary<Guid, PendingChallenge> challenges = [];
    private static readonly byte[] FakeSalt = RandomNumberGenerator.GetBytes(16);
    private static readonly byte[] FakeHash = RandomNumberGenerator.GetBytes(32);
    private sealed record Session(string Account, Guid Subject, string Device, DateTimeOffset Expires);
    private sealed record PendingChallenge(Session Session, byte[] Nonce, DateTimeOffset Expires);

    public IResult Login(LoginRequest request)
    {
        if (!Admin.ValidAccount(request.Account) || request.Password is not { Length: > 0 and <= 256 } ||
            request.DevicePublicKey is not { Length: > 0 and <= 4096 })
            return Results.Unauthorized();

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(request.DevicePublicKey);
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKey, out int consumed);
            if (consumed != publicKey.Length || rsa.KeySize is < 2048 or > 4096) return Results.Unauthorized();
            publicKey = rsa.ExportSubjectPublicKeyInfo();
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return Results.Unauthorized();
        }

        string account = Protocol.Account(request.Account);
        string device = Protocol.Device(publicKey);
        var now = DateTimeOffset.UtcNow;
        var user = vault.Use(state => state.Users.GetValueOrDefault(account));
        var valid = Protocol.CheckPassword(request.Password, user?.Salt ?? FakeSalt, user?.PasswordHash ?? FakeHash);
        if (!valid || user is null || !user.Enabled || user.Expires <= now)
        {
            logger.LogInformation("Login denied");
            return Results.Unauthorized();
        }

        bool registered = user.Devices.ContainsKey(device) || vault.Use(state =>
        {
            if (!state.Users.TryGetValue(account, out var current) || current.Id != user.Id ||
                !current.Enabled || current.Expires <= now) return false;
            if (current.Devices.ContainsKey(device)) return true;
            if (current.Devices.Count >= current.MaxDevices) return false;
            current.Devices.Add(device, publicKey);
            return true;
        }, write: true);
        if (!registered)
        {
            logger.LogInformation("Device enrollment denied for {Subject}", user.Id);
            return Results.StatusCode(403);
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (gate)
        {
            Prune(now);
            if (sessions.Count >= 20_000) return Results.StatusCode(429);
            foreach (var previous in sessions.Where(item => item.Value.Subject == user.Id && item.Value.Device == device).ToArray())
                sessions.Remove(previous.Key);
            sessions.Add(token, new(account, user.Id, device, now.AddHours(8)));
        }
        logger.LogInformation("Login accepted for {Subject}", user.Id);
        return Results.Ok(new LoginResponse(token, user.Id, user.IsAdministrator));
    }

    public IResult Challenge(HttpContext context)
    {
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            Prune(now);
            if (Find(context) is not { } session) return Results.Unauthorized();
            if (challenges.Count >= 20_000) return Results.StatusCode(429);
            var id = Guid.NewGuid();
            var nonce = RandomNumberGenerator.GetBytes(32);
            challenges.Add(id, new(session, nonce, now.AddSeconds(45)));
            return Results.Ok(new ChallengeResponse(id, nonce));
        }
    }

    public IResult Grant(HttpContext context, GrantRequest request)
    {
        if (request.Proof is not { Length: >= 256 and <= 512 }) return Results.Unauthorized();
        var now = DateTimeOffset.UtcNow;
        var challenge = ConsumeChallenge(context, request.Challenge, now);
        if (challenge is null) return Results.Unauthorized();

        var signed = vault.Use<SignedGrant?>(state =>
        {
            var session = challenge.Session;
            if (!state.Users.TryGetValue(session.Account, out var user) || user.Id != session.Subject ||
                !user.Enabled || user.Expires <= now || !user.Devices.TryGetValue(session.Device, out var key) ||
                !state.Media.TryGetValue(request.Media, out var mediaKey) ||
                !state.MediaOwners.TryGetValue(request.Media, out var owner) || owner == Guid.Empty ||
                (user.IsAdministrator ? user.Id != owner : !user.Media.Contains(request.Media)))
                return null;

            using var device = RSA.Create();
            device.ImportSubjectPublicKeyInfo(key, out _);
            if (!device.VerifyData(Protocol.Proof(request.Media, challenge.Nonce), request.Proof,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) return null;
            using var signer = RSA.Create();
            signer.ImportPkcs8PrivateKey(state.SigningKey, out _);
            var deadline = Minimum(session.Expires, user.Expires);
            if (!user.IsAdministrator && user.MediaExpirations?.TryGetValue(request.Media, out var mediaExpiry) == true)
            {
                if (mediaExpiry <= now) return null;
                deadline = Minimum(deadline, mediaExpiry);
            }
            var renew = Minimum(now.AddMinutes(15), deadline);
            var offline = user.IsAdministrator ? DateTimeOffset.MaxValue :
                user.AllowOffline != false ? Minimum(now.AddHours(1), deadline) : now;
            return Protocol.Sign(new(session.Subject, request.Media, session.Device, now, renew, offline,
                device.Encrypt(mediaKey, RSAEncryptionPadding.OaepSHA256), owner), signer);
        });

        logger.LogInformation("Grant {Outcome} for {Subject} and {Media}",
            signed is null ? "denied" : "issued", challenge.Session.Subject, request.Media);
        return signed is null ? Results.StatusCode(403) : Results.Ok(signed);
    }

    public IResult Manage(HttpContext context, AdminRequest request)
    {
        try
        {
            if (request.Proof is not { Length: >= 256 and <= 512 } ||
                !Enum.IsDefined(request.Operation)) return Results.Unauthorized();
            var now = DateTimeOffset.UtcNow;
            var challenge = ConsumeChallenge(context, request.Challenge, now);
            if (challenge is null) return Results.Unauthorized();

            var result = vault.Use<IResult>(state =>
            {
                var session = challenge.Session;
                if (!state.Users.TryGetValue(session.Account, out var admin) || admin.Id != session.Subject ||
                    !admin.IsAdministrator || !admin.Enabled || admin.Expires <= now ||
                    !admin.Devices.TryGetValue(session.Device, out var publicKey)) return Results.StatusCode(403);
                using var device = RSA.Create();
                device.ImportSubjectPublicKeyInfo(publicKey, out _);
                if (!device.VerifyData(Protocol.AdminProof(request.Operation, request.Media, challenge.Nonce), request.Proof,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) return Results.StatusCode(403);

                switch (request.Operation)
                {
                    case AdminOperation.Register when request.Media != Guid.Empty && request.Key is { Length: 32 }:
                        if (state.Media.TryGetValue(request.Media, out var existing))
                        {
                            if (state.MediaOwners.GetValueOrDefault(request.Media) != admin.Id) return MediaOwnerDenied();
                            return CryptographicOperations.FixedTimeEquals(existing, request.Key) ? Results.NoContent() : Results.Conflict();
                        }
                        state.Media.Add(request.Media, request.Key);
                        state.MediaOwners.Add(request.Media, admin.Id);
                        return Results.NoContent();
                    case AdminOperation.Export:
                        if (!state.Media.TryGetValue(request.Media, out var key)) return Results.NotFound();
                        if (state.MediaOwners.GetValueOrDefault(request.Media) != admin.Id) return MediaOwnerDenied();
                        using (var signer = RSA.Create())
                        {
                            signer.ImportPkcs8PrivateKey(state.SigningKey, out _);
                            var deadline = Minimum(now.AddMinutes(2), Minimum(admin.Expires, session.Expires));
                            return Results.Ok(Protocol.Sign(new(admin.Id, request.Media, session.Device, now, deadline,
                                deadline, device.Encrypt(key, RSAEncryptionPadding.OaepSHA256), admin.Id), signer));
                        }
                    case AdminOperation.Assign when Admin.ValidAccount(request.Account) &&
                        request.MediaDays is null or >= 1 and <= 3650:
                        if (!state.Media.ContainsKey(request.Media)) return Results.NotFound();
                        if (state.MediaOwners.GetValueOrDefault(request.Media) != admin.Id) return MediaOwnerDenied();
                        if (!state.Users.TryGetValue(Protocol.Account(request.Account!), out var member) ||
                            member.IsAdministrator) return Results.NotFound();
                        member.Media.Add(request.Media);
                        if (request.MediaDays is int mediaDays)
                        {
                            var expirations = member.MediaExpirations ?? [];
                            expirations[request.Media] = now.AddDays(mediaDays);
                            state.Users[Protocol.Account(request.Account!)] = member with { MediaExpirations = expirations };
                        }
                        else
                        {
                            member.MediaExpirations?.Remove(request.Media);
                        }
                        return Results.NoContent();
                    case AdminOperation.CreateMember when Admin.ValidAccount(request.Account) &&
                        request.Password is { Length: >= Protocol.MinimumPasswordLength and <= Protocol.MaximumPasswordLength } &&
                        request.MemberDays is null or >= 1 and <= 3650 && request.MaxDevices is null or >= 1 and <= 20:
                        string account = Protocol.Account(request.Account!);
                        if (state.Users.ContainsKey(account)) return Results.Conflict();
                        var salt = RandomNumberGenerator.GetBytes(16);
                        state.Users.Add(account, new User(Guid.NewGuid(), salt, Protocol.Password(request.Password, salt),
                            now.AddDays(request.MemberDays ?? 365), request.MaxDevices ?? 1, true, [], [],
                            AllowOffline: request.AllowOffline ?? true));
                        return Results.NoContent();
                    default:
                        return Results.BadRequest();
                }
            }, write: request.Operation != AdminOperation.Export, shouldSave: result =>
                result is IStatusCodeHttpResult { StatusCode: 204 });
            logger.LogInformation("Admin operation {Operation} requested by {Subject} for {Media} returned {StatusCode}",
                request.Operation, challenge.Session.Subject, request.Media, (result as IStatusCodeHttpResult)?.StatusCode);
            return result;
        }
        finally
        {
            if (request.Key is { } key) CryptographicOperations.ZeroMemory(key);
        }
    }

    private PendingChallenge? ConsumeChallenge(HttpContext context, Guid id, DateTimeOffset now)
    {
        lock (gate)
        {
            Prune(now);
            if (Find(context) is not { } session ||
                !challenges.TryGetValue(id, out var pending) || !ReferenceEquals(session, pending.Session)) return null;
            challenges.Remove(id);
            return pending;
        }
    }

    private static DateTimeOffset Minimum(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static IResult MediaOwnerDenied() =>
        Results.Json(new { Code = Protocol.MediaOwnerRequired }, statusCode: StatusCodes.Status403Forbidden);

    private Session? Find(HttpContext context)
    {
        var value = context.Request.Headers.Authorization.ToString();
        return value.Length == 71 && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? sessions.GetValueOrDefault(value[7..]) : null;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var item in sessions.Where(item => item.Value.Expires <= now).ToArray()) sessions.Remove(item.Key);
        foreach (var item in challenges.Where(item => item.Value.Expires <= now).ToArray()) challenges.Remove(item.Key);
    }
}
