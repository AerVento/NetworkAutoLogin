namespace NetworkAutoLogin;

/// <summary>
/// 集中管理所有持久化文件的位置。统一放在
/// %LOCALAPPDATA%\NetworkAutoLogin 下，避免和程序目录混在一起，
/// 也保证计划任务以当前用户身份运行时能正常读写。
/// </summary>
internal static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetworkAutoLogin");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string CredentialsFile => Path.Combine(DataDir, "credentials.dat");
    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string LogDir => Path.Combine(DataDir, "logs");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
    }
}
