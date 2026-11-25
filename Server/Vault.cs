using System.Security.Cryptography;
using System.Text.Json;

namespace SecureVideo.Server;

internal sealed record User(Guid Id, byte[] Salt, byte[] PasswordHash, DateTimeOffset Expires,
    int MaxDevices, bool Enabled, Dictionary<string, byte[]> Devices, HashSet<Guid> Media, bool IsAdministrator = false,
    bool? AllowOffline = null, Dictionary<Guid, DateTimeOffset>? MediaExpirations = null);
internal sealed record VaultData(byte[] SigningKey, Dictionary<string, User> Users, Dictionary<Guid, byte[]> Media)
{
    public Dictionary<Guid, Guid> MediaOwners { get; init; } = [];
}

internal sealed class Vault
{
    private readonly string directory = Path.GetFullPath(Environment.GetEnvironmentVariable("SVIDEO_VAULT") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SecureVideo.Server"));
    private string Database => Path.Combine(directory, "vault.dat");

    public void Initialize()
    {
        using var gate = Lock();
        if (File.Exists(Database)) return;
        using var rsa = RSA.Create(3072);
        var state = new VaultData(rsa.ExportPkcs8PrivateKey(), [], []);
        try { Save(state); }
        finally { CryptographicOperations.ZeroMemory(state.SigningKey); }
    }

    public T Use<T>(Func<VaultData, T> action, bool write = false, Func<T, bool>? shouldSave = null)
    {
        using var gate = Lock();
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(Database), null, DataProtectionScope.CurrentUser);
        VaultData state;
        try { state = JsonSerializer.Deserialize<VaultData>(bytes) ?? throw new InvalidDataException("数据库无效。"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        try
        {
            var result = action(state);
            if (write && (shouldSave?.Invoke(result) ?? true)) Save(state);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(state.SigningKey);
            foreach (var key in state.Media.Values) CryptographicOperations.ZeroMemory(key);
        }
    }

    private FileStream Lock()
    {
        Directory.CreateDirectory(directory);
        for (var i = 0;; i++)
        {
            try { return new FileStream(Path.Combine(directory, "vault.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (i < 100) { Thread.Sleep(25); }
        }
    }

    private void Save(VaultData state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        var temporary = Database + ".tmp";
        try
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(encrypted);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, Database, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            File.Delete(temporary);
        }
    }
}
