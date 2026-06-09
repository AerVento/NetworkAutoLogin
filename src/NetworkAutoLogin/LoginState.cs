using System.Text.Json;

namespace NetworkAutoLogin;

/// <summary>
/// 持久化的“上次成功登录时间”。基于墙上时钟（存 UTC），
/// 这样即使机器关机数天，下次开机也能算出真实流逝的天数。
/// </summary>
internal sealed class LoginState
{
    public DateTime? LastSuccessfulLoginUtc { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static LoginState Load()
    {
        if (!File.Exists(AppPaths.StateFile))
            return new LoginState();

        try
        {
            var json = File.ReadAllText(AppPaths.StateFile);
            return JsonSerializer.Deserialize<LoginState>(json, JsonOptions) ?? new LoginState();
        }
        catch
        {
            // 状态文件损坏时当作“无记录”，会触发一次刷新，安全。
            return new LoginState();
        }
    }

    public void MarkLoginNow()
    {
        LastSuccessfulLoginUtc = DateTime.UtcNow;
        Save();
    }

    private void Save()
    {
        AppPaths.EnsureDirectories();
        File.WriteAllText(AppPaths.StateFile, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>距上次成功登录已过去的天数；无记录时返回 null。</summary>
    public double? DaysSinceLastLogin()
        => LastSuccessfulLoginUtc is { } t
            ? (DateTime.UtcNow - t).TotalDays
            : null;
}
