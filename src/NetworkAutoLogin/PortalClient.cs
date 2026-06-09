using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace NetworkAutoLogin;

internal enum PageState { Login, LoggedIn, Unknown }

/// <summary>
/// 用 Selenium 驱动 Chrome 完成门户的“注销→登录”。
/// 元素定位见 memory: portal-page-elements。
/// 统一逻辑：先尝试注销（已在登录页/找不到注销按钮则跳过，容错），再登录。
///
/// 门户在“无历史存储状态”（无痕/全新 profile）下会反复误弹“网络连接错误”
/// 对话框，且其 .mask 遮罩会拦截点击。Selenium 每次用全新临时 profile，
/// 所以该弹窗几乎每次都出现。为此本类做了几重兜底：
///   1) 按钮一律用 JS click，绕过遮罩拦截；
///   2) 页面跳转/SPA 重渲染后固定 settle 等待，让页面充分加载再操作；
///   3) 填值/点击带瞬态异常重试，每次重试前先清弹窗。
///
/// 不用 headless：门户在 headless 下不渲染登录表单。也不能最小化：窗口进后台会被
/// 节流导致门户返回 503（反节流参数也压不住，已验证）。故用前台小窗口，做小、置于角落减少遮挡。
///
/// 注意：登录页是 input#username，已登录页是 span#username（同 id 不同标签），
/// 故账号/密码一律用 input#xxx 选择器，避免误抓到只读的 span。
/// </summary>
internal sealed class PortalClient
{
    private readonly Config _config;
    private readonly Logger _log;

    // 门户 DOM 元素（账号/密码限定 input，避开已登录页的同名 span）。
    private static readonly By Username = By.CssSelector("input#username");
    private static readonly By Password = By.CssSelector("input#password");
    private static readonly By Protocol = By.Id("protocol");
    private static readonly By LoginButton = By.Id("login-account");
    private static readonly By LogoutButton = By.Id("logout");

    // “网络连接错误”等通知对话框（active 时显示）及其确认按钮。
    private static readonly By ActiveDialog = By.CssSelector("div.dialog.confirm.active");
    private static readonly By DialogConfirm = By.CssSelector("div.dialog.confirm.active .btn-confirm");

    private const int InteractRetries = 6;

    public PortalClient(Config config, Logger log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>执行一次完整的刷新/登录。返回是否登录成功。</summary>
    public bool RefreshSession(Credentials creds)
    {
        ChromeDriver? driver = null;
        try
        {
            driver = CreateDriver();

            var state = NavigateUntilKnownPage(driver);
            switch (state)
            {
                case PageState.LoggedIn:
                    _log.Info("当前为已登录页，先注销。");
                    TryLogout(driver);
                    break;
                case PageState.Login:
                    _log.Info("当前已在登录页，直接登录。");
                    break;
                default:
                    _log.Warn("未能识别页面状态，仍尝试定位登录表单。");
                    break;
            }

            if (!DoLogin(driver, creds))
                return false;

            var ok = WaitForLoggedIn(driver);
            _log.Info(ok
                ? "Selenium：检测到已登录页，登录动作完成。"
                : "Selenium：超时仍未出现已登录标志，登录可能失败。");
            return ok;
        }
        catch (Exception ex)
        {
            _log.Error("Selenium 流程异常", ex);
            return false;
        }
        finally
        {
            try { driver?.Quit(); }
            catch { /* 退出清理失败忽略 */ }
        }
    }

    private ChromeDriver CreateDriver()
    {
        // 门户在 headless 下不渲染登录表单（疑似检测 HeadlessChrome）；窗口最小化/进后台
        // 又会被节流导致 503（反节流参数也压不住，已验证）。故用前台小窗口，做小、置于角落减少遮挡。
        var options = new ChromeOptions();
        options.AddArgument("--disable-gpu");
        options.AddArgument($"--window-size={_config.WindowWidth},{_config.WindowHeight}");
        options.AddArgument($"--window-position={_config.WindowX},{_config.WindowY}");
        options.AddArgument("--ignore-certificate-errors");
        options.AddArgument("--no-first-run");
        options.AddArgument("--no-default-browser-check");
        // 强制直连，忽略系统代理/PAC——门户是校内服务器应直连，避免走代理被挡。
        options.AddArgument("--no-proxy-server");
        // 门户页面常含加载不全的外链资源，Eager 策略在 DOMContentLoaded 后即返回，
        // 避免 GoToUrl 因等待全部资源而超时挂死。
        options.PageLoadStrategy = PageLoadStrategy.Eager;

        var service = ChromeDriverService.CreateDefaultService();
        service.HideCommandPromptWindow = true;

        var driver = new ChromeDriver(service, options, TimeSpan.FromSeconds(60));
        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(30);
        return driver;
    }

    /// <summary>
    /// 反复以“地址栏重输网址”的方式导航到门户，直到出现登录页/已登录页。
    /// 每次先 about:blank 再访问门户，等价于清空地址栏重新输入；不重开窗口，只重新导航。
    /// 用于自愈偶发的 503 / 页面迟迟未就绪（这两种都表现为识别不出已知页面）。
    /// </summary>
    private PageState NavigateUntilKnownPage(IWebDriver driver)
    {
        for (var attempt = 1; attempt <= _config.PageLoadRetries; attempt++)
        {
            if (attempt == 1)
                _log.Info($"打开门户：{_config.PortalUrl}");
            else
                _log.Warn($"页面未到达登录/已登录状态，第 {attempt}/{_config.PageLoadRetries} 次重新访问门户。");

            try
            {
                driver.Navigate().GoToUrl("about:blank");
                driver.Navigate().GoToUrl(_config.PortalUrl);
            }
            catch (WebDriverException ex)
            {
                _log.Warn($"导航异常（{ex.GetType().Name}），稍后重试。");
                Thread.Sleep(500);
                continue;
            }

            var state = WaitForKnownPage(driver);
            if (state != PageState.Unknown)
                return state;
        }
        return PageState.Unknown;
    }

    /// <summary>等待页面进入“登录页”或“已登录页”二者之一。</summary>
    private PageState WaitForKnownPage(IWebDriver driver)
    {
        var result = PageState.Unknown;
        WaitUntil(driver, () =>
        {
            if (IsVisible(driver, LogoutButton)) { result = PageState.LoggedIn; return true; }
            if (IsVisible(driver, Username)) { result = PageState.Login; return true; }
            return false;
        });
        return result;
    }

    private void TryLogout(IWebDriver driver)
    {
        Settle(); // 等已登录页加载稳定再点注销。
        if (!ClickWithRetry(driver, LogoutButton, "注销按钮"))
        {
            // 容错：注销失败不应中断后续登录（可能会话已过期、按钮已不在）。
            _log.Warn("注销步骤未成功，跳过并继续尝试登录。");
            return;
        }

        // 注销后等登录框出现。
        if (WaitUntil(driver, () => IsVisible(driver, Username)))
            _log.Info("注销成功，已回到登录页。");
        else
            _log.Warn("点击注销后未在限时内回到登录页，仍尝试继续登录。");
    }

    private bool DoLogin(IWebDriver driver, Credentials creds)
    {
        if (!WaitUntil(driver, () => IsVisible(driver, Username)))
        {
            _log.Warn("未找到账号输入框，登录无法继续。");
            return false;
        }

        // 粗暴沉降：等 SPA 把登录面板彻底渲染好、输入框可用，再开始填。
        Settle();

        if (!TypeInto(driver, Username, creds.Username, "账号")) return false;
        if (!TypeInto(driver, Password, creds.Password, "密码")) return false;

        // 文明上网协议复选框默认勾选；若未勾选则补勾，否则登录会被拦。
        var protocols = driver.FindElements(Protocol);
        if (protocols.Count > 0 && !protocols[0].Selected)
        {
            JsClick(driver, protocols[0]);
            _log.Info("已勾选“文明上网”协议复选框。");
        }

        if (!ClickWithRetry(driver, LoginButton, "登录按钮"))
            return false;

        _log.Info("已提交登录表单。");
        return true;
    }

    private bool WaitForLoggedIn(IWebDriver driver)
        => WaitUntil(driver, () => IsVisible(driver, LogoutButton));

    // ---- 交互原语：均对弹窗与瞬态异常做兜底 -----------------------------

    /// <summary>
    /// 填写输入框，带重试。每次重试前清弹窗，写完校验 value 是否生效。
    /// </summary>
    private bool TypeInto(IWebDriver driver, By by, string text, string name)
    {
        for (var i = 0; i < InteractRetries; i++)
        {
            DismissDialogIfPresent(driver);
            try
            {
                var els = driver.FindElements(by);
                if (els.Count == 0 || !els[0].Displayed)
                {
                    Thread.Sleep(300);
                    continue;
                }

                var el = els[0];
                el.Clear();
                el.SendKeys(text);

                var actual = el.GetDomProperty("value");
                if (actual == text)
                    return true;

                _log.Warn($"填写“{name}”后回读不一致（读到长度 {actual?.Length ?? 0}），重试。");
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                // 瞬态（元素尚未可交互/页面切换）：下一轮重试。
            }
            Thread.Sleep(300);
        }
        _log.Warn($"填写“{name}”多次未成功。");
        return false;
    }

    /// <summary>点击按钮，带重试。统一用 JS click 绕过弹窗 .mask 遮罩拦截。</summary>
    private bool ClickWithRetry(IWebDriver driver, By by, string name)
    {
        for (var i = 0; i < InteractRetries; i++)
        {
            DismissDialogIfPresent(driver);
            try
            {
                var els = driver.FindElements(by);
                if (els.Count > 0 && els[0].Displayed)
                {
                    JsClick(driver, els[0]);
                    return true;
                }
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                // 瞬态：下一轮重试。
            }
            Thread.Sleep(300);
        }
        _log.Warn($"点击“{name}”多次未成功。");
        return false;
    }

    /// <summary>
    /// 手写轮询：每轮先点掉门户弹窗，再检查条件。返回条件是否在限时内满足。
    /// </summary>
    private bool WaitUntil(IWebDriver driver, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_config.ElementWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            DismissDialogIfPresent(driver);
            try
            {
                if (condition()) return true;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                // 页面切换瞬间元素失效，下一轮重试。
            }
            Thread.Sleep(300);
        }
        return false;
    }

    /// <summary>
    /// 若出现“网络连接错误”等通知对话框，点其“确认”按钮关闭。
    /// 该弹窗为门户误报且带遮罩会挡住其它点击，必须随时清掉。用 JS click 确保点得到。
    /// </summary>
    private void DismissDialogIfPresent(IWebDriver driver)
    {
        try
        {
            var dialogs = driver.FindElements(ActiveDialog);
            if (dialogs.Count == 0 || !dialogs[0].Displayed)
                return;

            var buttons = driver.FindElements(DialogConfirm);
            if (buttons.Count > 0 && buttons[0].Displayed)
            {
                JsClick(driver, buttons[0]);
                _log.Info("已关闭门户弹窗（网络连接错误，属误报）。");
                Thread.Sleep(300); // 等关闭动画/遮罩消失。
            }
        }
        catch (Exception)
        {
            // 弹窗处理是尽力而为，失败不影响主流程。
        }
    }

    /// <summary>页面跳转后的固定沉降等待。</summary>
    private void Settle()
    {
        if (_config.PageSettleMs > 0)
            Thread.Sleep(_config.PageSettleMs);
    }

    private static void JsClick(IWebDriver driver, IWebElement element)
        => ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", element);

    private static bool IsVisible(IWebDriver driver, By by)
    {
        var els = driver.FindElements(by);
        return els.Count > 0 && els[0].Displayed;
    }

    private static bool IsTransient(Exception ex)
        => ex is StaleElementReferenceException
              or InvalidElementStateException
              or ElementClickInterceptedException
              or ElementNotInteractableException;
}
