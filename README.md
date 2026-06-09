# NetworkAutoLogin

> 浙江大学校园网（`net.zju.edu.cn` 强制门户）自动重登小工具。

校园网的「无感知认证」每 **14 天** 强制过期一次，到期后需要手动到门户页面重新输入账号密码。
如果你常常**远程连接**自己的电脑，一旦网络在你不在场时掉线，就再也连不回去了。

NetworkAutoLogin 通过 Windows 计划任务定时检查，在认证到期**之前**自动用浏览器完成
「注销 → 重新登录」，刷新 14 天周期，让你的机器始终在线。

> ⚠️ 本工具是为浙大门户写的，但结构通用。换一套门户的 URL 和页面元素选择器即可适配其它
> 强制门户（见 [适配其它门户](#适配其它门户)）。

---

## 工作原理

- **触发**：Windows 计划任务，「用户登录时」+「每 60 分钟」各运行一次。机器长开机也能覆盖。
  每次运行完即退出，不常驻、不吃内存，比后台守护进程更抗崩溃。
- **决策**：读取本地持久化的「上次成功登录时间」（UTC 墙上时钟，关机数天也算得准）。
  - 距今 ≥ **13 天**（留 1 天余量，不等 14 天硬过期）→ 主动刷新；
  - 未到期，但 HTTP 探测发现被门户劫持（计划外掉线）→ 立即补登录；
  - 在线且未到期 → 只做一次探测就退出，**不启动浏览器**。
- **执行**：Selenium 驱动 Chrome，统一流程「先尝试注销（容错），再登录」。
- **凭据**：账号密码用 Windows DPAPI 加密存本地（仅**当前用户、本机**可解密），绝不明文落盘。
- **日志**：写到 `%LOCALAPPDATA%\NetworkAutoLogin\logs\`，远程时可据此判断脚本是否正常工作。

> 真正启动浏览器的只有「到期刷新」或「掉线补登」这两种少数情况——平时每小时的检查只是一次
> 轻量 HTTP 探测。所以浏览器窗口大约每 13 天才会出现几秒钟。

---

## 前置条件

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- 已安装 **Google Chrome**（chromedriver 由 Selenium Manager 自动匹配下载，无需手动安装）
- 首次运行需联网，以便 Selenium Manager 下载 driver（可先手动登录一次校园网）

---

## 快速开始

```powershell
# 1. 构建
cd "src\NetworkAutoLogin"
dotnet build -c Release

# 2. 配置门户地址、账号、密码（交互式，密码不回显）
.\bin\Release\net10.0-windows\NetworkAutoLogin.exe setup

# 3. 手动测一次登录，确认流程跑通
.\bin\Release\net10.0-windows\NetworkAutoLogin.exe login

# 4. 注册计划任务（回到项目根目录执行）
cd ..\..
powershell -ExecutionPolicy Bypass -File .\install-task.ps1
```

> **关于门户 URL**：`setup` 时要填门户登录页的**直接地址**。因为已登录状态下访问普通网站不会
> 跳转到门户，刷新流程必须知道门户真实 URL 才能打开它去点「注销」。请在浏览器手动登录后，从
> 地址栏复制门户页面 URL 填入（浙大为 `https://net2.zju.edu.cn/`）。

---

## 安装 / 卸载计划任务

`install-task.ps1` 会注册一个名为 `NetworkAutoLogin` 的计划任务：以**当前用户**身份、隐藏窗口运行
`NetworkAutoLogin.exe run`，触发器为「登录时 + 每 60 分钟」。

```powershell
# 安装（间隔可自定义，默认 60 分钟）
powershell -ExecutionPolicy Bypass -File .\install-task.ps1
powershell -ExecutionPolicy Bypass -File .\install-task.ps1 -IntervalMinutes 30

# 查看
Get-ScheduledTask -TaskName NetworkAutoLogin

# 手动触发一次
Start-ScheduledTask -TaskName NetworkAutoLogin

# 卸载
Unregister-ScheduledTask -TaskName NetworkAutoLogin -Confirm:$false
```

> 该脚本只注册一个用户级计划任务，通常**无需管理员权限**。建议运行前先打开 `install-task.ps1`
> 通读一遍，确认它做了什么再执行。

---

## 命令

| 命令 | 说明 |
|------|------|
| `setup`  | 交互式配置门户 URL、账号、密码（会弹出一个专属控制台窗口录入）|
| `run`    | 决策并按需登录（计划任务调用，默认命令）|
| `login`  | 立即强制刷新（注销 → 重登）|

> 程序以 **WinExe（GUI 子系统）** 构建，计划任务后台运行时**不会弹出黑色控制台窗口**。
> `run`/`login` 的运行情况（含「距下次刷新约 N 天」）都写入日志文件，自行查看即可。

---

## 配置说明

配置文件位于 `%LOCALAPPDATA%\NetworkAutoLogin\config.json`，可手动编辑：

| 字段 | 默认 | 说明 |
|------|------|------|
| `PortalUrl` | — | 门户登录页直接地址（必填）|
| `RefreshThresholdDays` | `13` | 距上次登录多少天后主动刷新（给 14 天硬上限留余量）|
| `ProbeUrl` | msftconnecttest | 判断是否真能上外网的探测地址 |
| `ProbeExpectedText` | `Microsoft Connect Test` | 探测响应中应出现的文本 |
| `WindowWidth` / `WindowHeight` | `500` / `400` | Chrome 窗口尺寸（小窗口减少遮挡）|
| `WindowX` / `WindowY` | `0` / `0` | 窗口左上角位置 |
| `MaxRetries` | `3` | 登录失败时整体重试次数 |
| `ElementWaitSeconds` | `3` | 各等待步骤的超时上限（内网很快，取小即可）|
| `PageLoadRetries` | `3` | 打开门户后页面未就绪（含偶发 503）时重新导航的次数 |
| `PageSettleMs` | `1000` | 页面跳转后操作前的固定沉降等待（毫秒）|

---

## 数据位置

`%LOCALAPPDATA%\NetworkAutoLogin\`

| 文件 | 内容 |
|------|------|
| `config.json` | 配置 |
| `credentials.dat` | DPAPI 加密的账号密码 |
| `state.json` | 上次成功登录时间戳 |
| `logs\` | 按天日志 |

---

## 适配其它门户

本工具的浏览器自动化集中在 [`PortalClient.cs`](src/NetworkAutoLogin/PortalClient.cs)。适配新门户主要改两处：

1. **`config.json` 的 `PortalUrl`** —— 换成新门户地址。
2. **`PortalClient.cs` 顶部的元素选择器** —— 账号框、密码框、登录按钮、注销按钮的定位（`By.Id`/`By.CssSelector`）。

如果新门户没有「14 天周期」这种机制，可把 `RefreshThresholdDays` 设大、主要依赖「掉线探测补登」逻辑。

---

## 已知坑（针对浙大门户）

- **不能用 headless**：门户在 headless 模式下不渲染登录表单（疑似检测 `HeadlessChrome`）。
- **窗口不能最小化/进后台**：会被 Chrome 后台节流，门户直接返回 **503**。故用前台小窗口。
- **门户偶发误报弹窗**「网络连接错误」：在全新浏览器 profile（无 cookie）下会反复弹出，且其遮罩
  会拦截点击。工具已自动识别并关闭它。

---

## 安全说明

- 账号密码经 Windows DPAPI（`CurrentUser` 作用域）加密后存于本机，**不会**写入仓库或明文文件。
- `credentials.dat` 只能由保存它的那个 Windows 用户在本机解密，拷到别的机器/账户无法还原。
- 计划任务以你的普通用户身份运行，不提权。

---

## 许可

[MIT](LICENSE)
