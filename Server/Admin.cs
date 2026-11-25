using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SecureVideo.Server;

internal static class Admin
{
    public static void Run(Vault vault, string[] args)
    {
        switch (args)
        {
            case ["init"]:
                vault.Initialize();
                Console.WriteLine("授权数据库已初始化。");
                break;
            case ["export-config", var url, var path]:
                var pem = vault.Use(state =>
                {
                    using var rsa = RSA.Create();
                    rsa.ImportPkcs8PrivateKey(state.SigningKey, out _);
                    return rsa.ExportSubjectPublicKeyInfoPem();
                });
                using (var file = new FileStream(path, FileMode.CreateNew))
                    JsonSerializer.Serialize(file, new { Server = url, SigningPublicKey = pem });
                break;
            case [var command, var account, var days, var devices] when command is "add-user" or "add-admin":
                if (!ValidAccount(account) || !int.TryParse(days, out var ttl) || ttl is < 1 or > 3650 ||
                    !int.TryParse(devices, out var deviceLimit) || deviceLimit is < 1 or > 20)
                    throw new ArgumentException("账号长度 1–100，到期天数 1–3650，设备上限 1–20。");
                var password = ReadPassword();
                if (password.Length is < Protocol.MinimumPasswordLength or > Protocol.MaximumPasswordLength)
                    throw new ArgumentException($"密码长度须为 {Protocol.MinimumPasswordLength}–{Protocol.MaximumPasswordLength} 个字符。");
                var salt = RandomNumberGenerator.GetBytes(16);
                var user = new User(Guid.NewGuid(), salt, Protocol.Password(password, salt),
                    DateTimeOffset.UtcNow.AddDays(ttl), deviceLimit, true, [], [], command == "add-admin");
                Update(vault, state => state.Users.Add(Protocol.Account(account), user));
                Console.WriteLine("账号已创建。");
                break;
            case ["disable-user", var account]:
                Update(vault, state => state.Users[Protocol.Account(account)] = Get(state, account) with { Enabled = false });
                break;
            case ["renew-user", var account, var days] when int.TryParse(days, out var renewal) && renewal is >= 1 and <= 3650:
                Update(vault, state => state.Users[Protocol.Account(account)] =
                    Get(state, account) with { Expires = DateTimeOffset.UtcNow.AddDays(renewal), Enabled = true });
                break;
            case ["reset-devices", var account]:
                Update(vault, state => Get(state, account).Devices.Clear());
                break;
            case ["grant", var account, var id] when Guid.TryParse(id, out var media):
                Update(vault, state =>
                {
                    if (!state.Media.ContainsKey(media)) throw new ArgumentException("媒体 ID 不存在。");
                    if (state.MediaOwners.GetValueOrDefault(media) == Guid.Empty)
                        throw new ArgumentException("无法确认此视频的加密管理员，请重新加密。");
                    var user = Get(state, account);
                    user.Media.Add(media);
                    user.MediaExpirations?.Remove(media);
                });
                break;
            case ["revoke", var account, var id] when Guid.TryParse(id, out var media):
                Update(vault, state =>
                {
                    var user = Get(state, account);
                    user.Media.Remove(media);
                    user.MediaExpirations?.Remove(media);
                });
                break;
            case ["import", var owner, var input, var output]:
                Import(vault, owner, input, output);
                break;
            default:
                throw new ArgumentException("命令：init、export-config URL PATH、add-user ACCOUNT DAYS DEVICES、add-admin ACCOUNT DAYS DEVICES、disable-user ACCOUNT、renew-user ACCOUNT DAYS、reset-devices ACCOUNT、grant ACCOUNT MEDIA_ID、revoke ACCOUNT MEDIA_ID、import ADMIN SOURCE OUTPUT");
        }
    }

    public static bool ValidAccount(string? account) => account?.Trim().Length is > 0 and <= 100 &&
        !account.Any(char.IsControl);

    private static void Update(Vault vault, Action<VaultData> update) =>
        vault.Use(state => { update(state); return true; }, write: true);

    private static User Get(VaultData state, string account) =>
        state.Users.TryGetValue(Protocol.Account(account), out var user) ? user : throw new ArgumentException("账号不存在。");

    private static string ReadPassword()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        Console.Write("初始密码：");
        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && password.Length > 0) password.Length--;
            if (!char.IsControl(key.KeyChar) && password.Length <= 256) password.Append(key.KeyChar);
        }
        Console.WriteLine();
        return password.ToString();
    }

    private static void Import(Vault vault, string account, string input, string output)
    {
        var owner = vault.Use(state => Get(state, account));
        if (!owner.IsAdministrator || !owner.Enabled || owner.Expires <= DateTimeOffset.UtcNow)
            throw new ArgumentException("请指定有效的加密管理员账号。");
        var id = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(32);
        var temporary = output + "." + id.ToString("N") + ".partial";
        try
        {
            if (!Path.GetExtension(output).Equals(".svideo", StringComparison.OrdinalIgnoreCase) || File.Exists(output))
                throw new ArgumentException("请选择一个尚不存在的 .svideo 输出路径。");
            using (var source = File.OpenRead(input))
            using (var destination = new FileStream(temporary, FileMode.CreateNew))
            {
                var format = LocalVideo.Detect(source, input);
                EncryptedVideoWriter.Write(source, destination, id, format, key);
                destination.Flush(flushToDisk: true);
            }
            Update(vault, state =>
            {
                var current = Get(state, account);
                if (current.Id != owner.Id || !current.IsAdministrator || !current.Enabled || current.Expires <= DateTimeOffset.UtcNow)
                    throw new ArgumentException("请指定有效的加密管理员账号。");
                state.Media.Add(id, key);
                state.MediaOwners.Add(id, owner.Id);
            });
            try { File.Move(temporary, output); }
            catch
            {
                Update(vault, state =>
                {
                    state.Media.Remove(id);
                    state.MediaOwners.Remove(id);
                });
                throw;
            }
            Console.WriteLine(id);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            File.Delete(temporary);
        }
    }
}
