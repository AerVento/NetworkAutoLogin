using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetworkAutoLogin;

internal sealed record Credentials(string Username, string Password);

/// <summary>
/// 凭据本地加密存储。用 Windows DPAPI（ProtectedData, CurrentUser 作用域）：
/// 密文只能由“同一 Windows 用户”在“同一台机器”上解密，不写明文。
/// 计划任务以当前用户身份运行，因此能正常解密。
/// </summary>
internal static class CredentialStore
{
    // 附加的熵，进一步绑定到本应用；不是密钥，只是让密文更专属。
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("NetworkAutoLogin::zju::v1");

    public static void Save(Credentials creds)
    {
        AppPaths.EnsureDirectories();
        var plain = JsonSerializer.SerializeToUtf8Bytes(creds);
        var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(AppPaths.CredentialsFile, cipher);
    }

    public static bool Exists() => File.Exists(AppPaths.CredentialsFile);

    public static Credentials Load()
    {
        if (!Exists())
            throw new FileNotFoundException(
                "尚未保存凭据。请先运行 `NetworkAutoLogin setup`。");

        var cipher = File.ReadAllBytes(AppPaths.CredentialsFile);
        var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<Credentials>(plain)
               ?? throw new InvalidDataException("凭据解密后为空。");
    }
}
