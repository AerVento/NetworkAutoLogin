namespace NetworkAutoLogin;

internal enum NetworkStatus
{
    /// <summary>探测成功，能正常访问外网。</summary>
    Online,

    /// <summary>被门户劫持（返回重定向或非预期内容），需要登录。</summary>
    CaptivePortal,

    /// <summary>连不通（DNS/TCP 失败、超时），物理层面无网，登录也没用。</summary>
    Offline,
}

/// <summary>
/// 通过请求一个“结果已知”的轻量 URL 判断当前网络状态。
/// 关键点：禁用自动重定向，这样门户的 302 跳转才能被识别为“被劫持”。
/// </summary>
internal sealed class NetworkProbe
{
    private readonly Config _config;
    private readonly Logger _log;

    public NetworkProbe(Config config, Logger log)
    {
        _config = config;
        _log = log;
    }

    public async Task<NetworkStatus> CheckAsync()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };

        try
        {
            using var resp = await client.GetAsync(_config.ProbeUrl);

            // 门户通常用 3xx 把请求重定向到登录页。
            if ((int)resp.StatusCode is >= 300 and < 400)
            {
                _log.Info($"探测：收到重定向 {(int)resp.StatusCode} → 判定被门户劫持。");
                return NetworkStatus.CaptivePortal;
            }

            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode && body.Contains(_config.ProbeExpectedText))
            {
                _log.Info("探测：内容符合预期 → 在线。");
                return NetworkStatus.Online;
            }

            // 200 但内容不对，多半是门户直接返回了登录页。
            _log.Info($"探测：状态 {(int)resp.StatusCode} 但内容不符 → 判定被门户劫持。");
            return NetworkStatus.CaptivePortal;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.Warn($"探测：请求失败（{ex.GetType().Name}）→ 判定离线/无网。");
            return NetworkStatus.Offline;
        }
    }
}
