using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SecureVideo;

public sealed record LoginRequest(string Account, string Password, string DevicePublicKey);
public sealed record LoginResponse(string Token, Guid Subject, bool IsAdministrator = false);
public sealed record ChallengeResponse(Guid Id, byte[] Nonce);
public sealed record GrantRequest(Guid Media, Guid Challenge, byte[] Proof);
public enum AdminOperation { Register = 1, Export, Assign, CreateMember }
public sealed record AdminRequest(AdminOperation Operation, Guid Media, Guid Challenge, byte[] Proof,
    byte[]? Key = null, string? Account = null, string? Password = null, int? MemberDays = null, int? MaxDevices = null,
    bool? AllowOffline = null, int? MediaDays = null);
public sealed record GrantPayload(Guid Subject, Guid Media, string Device, DateTimeOffset Issued,
    DateTimeOffset RenewBy, DateTimeOffset OfflineUntil, byte[] WrappedKey, Guid? MediaOwner = null);
public sealed record SignedGrant(byte[] Payload, byte[] Signature);

public static class Protocol
{
    public static bool IsPermanentOwnerGrant(GrantPayload grant) =>
        grant.OfflineUntil == DateTimeOffset.MaxValue && grant.MediaOwner == grant.Subject && grant.Subject != Guid.Empty;

    public const int MinimumPasswordLength = 3;
    public const int MaximumPasswordLength = 256;

    public const string MediaOwnerRequired = "media_owner_required";

    public static string Account(string value) => value.Trim().ToUpperInvariant();
    public static string Device(byte[] publicKey) => Convert.ToHexString(SHA256.HashData(publicKey));
    public static byte[] Proof(Guid media, byte[] nonce) =>
        Encoding.ASCII.GetBytes($"SecureVideo grant v1:{media:N}:{Convert.ToBase64String(nonce)}");

    public static byte[] AdminProof(AdminOperation operation, Guid media, byte[] nonce) =>
        Encoding.ASCII.GetBytes($"SecureVideo admin v1:{(int)operation}:{media:N}:{Convert.ToBase64String(nonce)}");

    public static byte[] Password(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);

    public static bool CheckPassword(string password, byte[] salt, byte[] hash)
    {
        var candidate = Password(password, salt);
        try { return CryptographicOperations.FixedTimeEquals(candidate, hash); }
        finally { CryptographicOperations.ZeroMemory(candidate); }
    }

    public static SignedGrant Sign(GrantPayload grant, RSA key)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(grant);
        return new(payload, key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    }

    public static GrantPayload Verify(SignedGrant signed, RSA key)
    {
        if (!key.VerifyData(signed.Payload, signed.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("播放许可证签名无效。");
        return JsonSerializer.Deserialize<GrantPayload>(signed.Payload)
            ?? throw new CryptographicException("许可证内容无效。");
    }
}
