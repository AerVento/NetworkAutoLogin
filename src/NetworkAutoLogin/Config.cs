using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkAutoLogin;

/// <summary>
/// 运行配置。首次运行 `setup` 时生成，之后可手动编辑 config.json 调整。
/// </summary>
internal sealed class Config
{
    /// <summary>
    /// 校园网门户登录页的直接地址。这是必填项：
    /// 已登录状态下访问普通网站不会被重定向到门户，所以刷新流程
    /// 必须知道门户的真实 URL 才能打开它去点“注销”。
    /// </summary>
    public string PortalUrl { get; set; } = "";

    /// <summary>
    /// 距上次登录多少天后主动刷新。取 13，给 14 天硬上限留 1 天余量。
    /// </summary>
    public double RefreshThresholdDays { get; set; } = 13.0;

    /// <summary>
    /// 用于判断“当前是否真能上外网”的探测地址。默认用微软的连通性测试端点，
    /// 正常返回纯文本 "Microsoft Connect Test"。
    /// </summary>
    public string ProbeUrl { get; set; } = "http://www.msftconnecttest.com/connecttest.txt";

    /// <summary>探测时期望在响应体中出现的文本。</summary>
    public string ProbeExpectedText { get; set; } = "Microsoft Connect Test";

    /// <summary>
    /// Chrome 窗口尺寸。门户在窗口被最小化/遮挡（后台节流）时会返回 503，
    /// 故窗口必须保持前台真实可见——做小以减少遮挡，但不隐藏。可按需调整。
    /// </summary>
    public int WindowWidth { get; set; } = 500;
    public int WindowHeight { get; set; } = 400;

    /// <summary>窗口左上角位置（像素）。默认贴屏幕左上角，尽量少遮挡。</summary>
    public int WindowX { get; set; } = 0;
    public int WindowY { get; set; } = 0;

    /// <summary>登录失败时的最大重试次数。</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// 各 Selenium 显式等待的上限（秒）。内网加载极快，正常都在 1 秒内返回；
    /// 此值只是“卡住时多久放弃重试”的上限，故取小。注意别低于 ~2 秒：
    /// 等登录结果有服务器往返、清误报弹窗也需几个轮询周期。
    /// </summary>
    public int ElementWaitSeconds { get; set; } = 3;

    /// <summary>
    /// 打开门户后若迟迟未到达登录页/已登录页（含偶发 503），
    /// 以“地址栏重输网址”的方式重新导航的最大次数。
    /// </summary>
    public int PageLoadRetries { get; set; } = 3;

    /// <summary>
    /// 页面跳转/SPA 重渲染后，操作前的固定“沉降”等待（毫秒）。
    /// 门户是单页应用，注销→登录面板切换有动画与初始化延迟，
    /// 粗暴等一下让页面充分加载，比精细判断更稳。
    /// </summary>
    public int PageSettleMs { get; set; } = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Config Load()
    {
        if (!File.Exists(AppPaths.ConfigFile))
            throw new FileNotFoundException(
                $"找不到配置文件：{AppPaths.ConfigFile}。请先运行 `NetworkAutoLogin setup`。");

        var json = File.ReadAllText(AppPaths.ConfigFile);
        return JsonSerializer.Deserialize<Config>(json, JsonOptions)
               ?? throw new InvalidDataException("配置文件解析为空。");
    }

    public void Save()
    {
        AppPaths.EnsureDirectories();
        File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static Config LoadOrDefault()
        => File.Exists(AppPaths.ConfigFile) ? Load() : new Config();
}
