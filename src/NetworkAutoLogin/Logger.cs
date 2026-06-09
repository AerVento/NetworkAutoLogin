namespace NetworkAutoLogin;

/// <summary>
/// 极简文件日志。远程场景下用户主要靠它判断脚本有没有正常工作，
/// 所以同时写文件和控制台。按天分文件，自动清理超过保留期的旧日志。
/// </summary>
internal sealed class Logger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public Logger(int retentionDays = 30)
    {
        AppPaths.EnsureDirectories();
        CleanupOldLogs(retentionDays);

        var path = Path.Combine(AppPaths.LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");
        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception ex)
        => Write("ERROR", $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (_lock)
        {
            _writer.WriteLine(line);
            Console.WriteLine(line);
        }
    }

    private static void CleanupOldLogs(int retentionDays)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-retentionDays);
            foreach (var file in Directory.EnumerateFiles(AppPaths.LogDir, "*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // 清理失败无关紧要，忽略。
        }
    }

    public void Dispose() => _writer.Dispose();
}
