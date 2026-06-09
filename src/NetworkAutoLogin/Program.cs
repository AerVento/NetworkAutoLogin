using NetworkAutoLogin;

// 退出码：0 = 正常（无需动作 / 登录成功）；1 = 失败/异常；2 = 配置缺失。
return await App.RunAsync(args);

internal static class App
{
    public static async Task<int> RunAsync(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 重定向时可能失败，忽略 */ }

        AppPaths.EnsureDirectories();
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "run";

        try
        {
            return command switch
            {
                "setup"  => Setup(),
                "status" => Status(),
                "probe"  => await Probe(),
                "login"  => await RunDecision(forceRefresh: true),
                "run"    => await RunDecision(forceRefresh: false),
                _        => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"未捕获异常：{ex}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            NetworkAutoLogin —— 浙大校园网自动重登

            用法：
              NetworkAutoLogin setup     交互式配置门户地址与账号密码
              NetworkAutoLogin run       决策并按需登录（计划任务调用，默认命令）
              NetworkAutoLogin login     立即强制刷新（注销→重登）
              NetworkAutoLogin status    查看当前状态
              NetworkAutoLogin probe     仅探测当前网络状态
            """);
        return 0;
    }

    // ---- setup ----------------------------------------------------------

    private static int Setup()
    {
        Console.WriteLine("=== NetworkAutoLogin 配置 ===");

        var config = Config.LoadOrDefault();

        Console.Write($"门户登录页 URL [{config.PortalUrl}]：");
        var url = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(url)) config.PortalUrl = url;

        if (string.IsNullOrWhiteSpace(config.PortalUrl))
        {
            Console.Error.WriteLine("门户 URL 不能为空。");
            return 2;
        }

        Console.Write("账号：");
        var user = Console.ReadLine()?.Trim() ?? "";
        Console.Write("密码：");
        var pass = ReadPassword();

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            Console.Error.WriteLine("账号或密码为空，已取消。");
            return 2;
        }

        config.Save();
        CredentialStore.Save(new Credentials(user, pass));

        Console.WriteLine($"已保存配置到：{AppPaths.DataDir}");
        Console.WriteLine("提示：凭据已用 Windows DPAPI 加密（仅当前用户、本机可解密）。");
        Console.WriteLine("下一步可运行 `NetworkAutoLogin login` 测试一次登录。");
        return 0;
    }

    private static string ReadPassword()
    {
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0) buffer.Remove(buffer.Length - 1, 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
        return buffer.ToString();
    }

    // ---- status ---------------------------------------------------------

    private static int Status()
    {
        var config = Config.LoadOrDefault();
        var state = LoginState.Load();

        Console.WriteLine("=== 状态 ===");
        Console.WriteLine($"数据目录   : {AppPaths.DataDir}");
        Console.WriteLine($"门户 URL   : {(string.IsNullOrEmpty(config.PortalUrl) ? "(未配置)" : config.PortalUrl)}");
        Console.WriteLine($"凭据已保存 : {(CredentialStore.Exists() ? "是" : "否")}");
        Console.WriteLine($"刷新阈值   : {config.RefreshThresholdDays} 天");

        if (state.LastSuccessfulLoginUtc is { } t)
        {
            var days = state.DaysSinceLastLogin() ?? 0;
            Console.WriteLine($"上次登录   : {t.ToLocalTime():yyyy-MM-dd HH:mm:ss}（{days:F1} 天前）");
            Console.WriteLine($"距下次刷新 : {Math.Max(0, config.RefreshThresholdDays - days):F1} 天");
        }
        else
        {
            Console.WriteLine("上次登录   : (无记录)");
        }
        return 0;
    }

    // ---- probe ----------------------------------------------------------

    private static async Task<int> Probe()
    {
        using var log = new Logger();
        var config = Config.LoadOrDefault();
        var status = await new NetworkProbe(config, log).CheckAsync();
        Console.WriteLine($"网络状态：{status}");
        return 0;
    }

    // ---- 决策层 ----------------------------------------------------------

    private static async Task<int> RunDecision(bool forceRefresh)
    {
        using var log = new Logger();

        if (!File.Exists(AppPaths.ConfigFile) || !CredentialStore.Exists())
        {
            log.Error("尚未配置（缺少 config.json 或凭据）。请先运行 `NetworkAutoLogin setup`。");
            return 2;
        }

        var config = Config.Load();
        var creds = CredentialStore.Load();
        var state = LoginState.Load();
        var probe = new NetworkProbe(config, log);

        var days = state.DaysSinceLastLogin();
        bool needAction;

        if (forceRefresh)
        {
            log.Info("收到强制刷新指令。");
            needAction = true;
        }
        else if (days is null)
        {
            log.Info("无上次登录记录 → 执行首次登录。");
            needAction = true;
        }
        else if (days.Value >= config.RefreshThresholdDays)
        {
            log.Info($"距上次登录 {days.Value:F1} 天 ≥ 阈值 {config.RefreshThresholdDays} 天 → 主动刷新。");
            needAction = true;
        }
        else
        {
            // 未到刷新期：探测是否计划外掉线。
            var status = await probe.CheckAsync();
            if (status == NetworkStatus.Online)
            {
                log.Info($"距上次登录 {days.Value:F1} 天，未到期且在线 → 无需动作。");
                return 0;
            }
            if (status == NetworkStatus.Offline)
            {
                log.Warn("探测为离线（物理无网），登录无意义，等待下个周期。");
                return 1;
            }
            log.Warn("未到刷新期但探测到被门户劫持（计划外掉线）→ 补登录。");
            needAction = true;
        }

        if (!needAction)
            return 0;

        var success = await TryLoginWithRetry(config, creds, log);
        if (!success)
        {
            log.Error("多次尝试后仍登录失败。");
            return 1;
        }

        state.MarkLoginNow();
        log.Info("登录成功，已更新时间戳。");

        // 收尾确认（仅记录，不影响成功判定）。
        var confirm = await probe.CheckAsync();
        if (confirm != NetworkStatus.Online)
            log.Warn($"登录后确认探测结果为 {confirm}，请留意日志。");

        return 0;
    }

    private static async Task<bool> TryLoginWithRetry(Config config, Credentials creds, Logger log)
    {
        var portal = new PortalClient(config, log);
        for (var attempt = 1; attempt <= config.MaxRetries; attempt++)
        {
            log.Info($"登录尝试 {attempt}/{config.MaxRetries} …");
            if (portal.RefreshSession(creds))
                return true;

            if (attempt < config.MaxRetries)
            {
                // 固定 3 秒后重试。
                var delay = TimeSpan.FromSeconds(3);
                log.Warn($"本次失败，{delay.TotalSeconds:F0} 秒后重试。");
                await Task.Delay(delay);
            }
        }
        return false;
    }
}
