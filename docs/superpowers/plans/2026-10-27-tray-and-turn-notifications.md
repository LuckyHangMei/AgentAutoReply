# 托盘最小化与回合返回通知 · 实施计划

> **For agentic workers:** 本计划按任务顺序执行，每个任务自带测试循环与验证检查点。步骤用 `- [ ]` 跟踪。

**Goal:** 让主窗口最小化到系统托盘继续监听，并在被监听会话成功返回一轮回答时弹出系统通知（仅后台时）。

**Architecture:** 判定逻辑放进纯逻辑 `TurnWatcher`，由每轮 `ConsoleMonitor.Poll` 喂终端画面，结果随 `MonitorResult` 冒泡到 UI；托盘与气泡用 WinForms 自带 `NotifyIcon`，通知出口通过构造参数注入以便 GUI 测试。

**Tech Stack:** C# 14 / .NET 10 / WinForms；手写测试程序 `tests/Program.cs`；无第三方依赖。

## Global Constraints

- 目标框架 `net10.0-windows`，不得新增 NuGet 包。
- UI 文案、日志、注释一律简体中文，风格与现有代码一致。
- 工作区不是 git 仓库，**没有 commit 步骤**；每个任务以「构建 + 过滤测试通过」为检查点。
- 测试命令统一使用构建目录，避免工作区 `obj/` 的权限问题：
  - 构建：`dotnet build tests/CodexAutoYes.Tests.csproj -p:ArtifactsPath="D:\MyStudyFiles\aiPro\codexautoyes\build" -v q --nologo`
  - 运行：先设置 `TEMP`/`TMP` 到 `D:\MyStudyFiles\aiPro\codexautoyes\build\fixturetmp`（夹具子进程需要可写目录），再运行 `build\bin\CodexAutoYes.Tests\debug\CodexAutoYes.Tests.exe`。
- 依赖真实 Codex 会话的既有用例（`TestDiscovery*`、`TestConsole(..., true)`）在本机没有 codex 进程时必然失败，验证时用 `--filter` 只跑相关分组。
- 判定只读终端画面，不引入 Codex 结构化事件；不改自动确认与续跑规则。

---

### Task 0: 测试程序支持按名称过滤

**Files:**
- Modify: `tests/Program.cs:81-82`

**Interfaces:**
- Produces: 命令行参数 `--filter <子串>`，按用例名不区分大小写过滤；`--keep-alive` 保留为别名。

- [ ] **Step 1: 改过滤分支**

把现有的：

```csharp
if (args is ["--keep-alive"])
    tests = tests.Where(test => test.Name.Contains("keep-alive", StringComparison.Ordinal)).ToArray();
```

替换为：

```csharp
if (args is ["--keep-alive"])
    tests = tests.Where(test => test.Name.Contains("keep-alive", StringComparison.Ordinal)).ToArray();
if (args.Length == 2 && args[0] == "--filter")
    tests = tests.Where(test => test.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase)).ToArray();
```

- [ ] **Step 2: 构建并确认过滤生效**

Run: 构建命令 + `CodexAutoYes.Tests.exe --filter "GUI rendering"`
Expected: 只运行 1 项，输出 `1/1 checks passed.`，`artifacts/test-results.txt` 只含 `keep-alive: GUI rendering`。

---

### Task 1: 回合返回判定（TurnWatcher）

**Files:**
- Modify: `KeepAlive.cs`（`LooksBusy` 改 internal，新增 `FooterHasProblem`）
- Create: `TurnWatcher.cs`
- Modify: `tests/Program.cs`（新增 3 个纯逻辑用例 + 注册）

**Interfaces:**
- Consumes: `KeepAliveWatch.Normalize(string)`, `KeepAliveWatch.LooksBusy(string)`, `ApprovalPrompt.Find(string)`
- Produces:
  - `internal sealed record TurnReturn(string Summary);`
  - `internal sealed class TurnWatcher { internal TurnReturn? Observe(string screen); internal static string Summarize(string signature); }`
  - `internal static bool KeepAliveWatch.FooterHasProblem(string signature);`

- [ ] **Step 1: 先写失败测试**

在 `tests/Program.cs` 里新增三个方法，并把它们注册到 `tests` 列表（放在 `keep-alive` 组之后）：

```csharp
/// <summary>执行态结束并稳定后只报告一次成功返回。</summary>
private static void TestTurnWatcher()
{
    var watcher = new TurnWatcher();
    Assert(watcher.Observe(IdleScreen) is null, "启动时已经空闲的画面不得算返回。");
    // 执行提示必须落在画面尾部，否则 LooksBusy 看不到它。
    Assert(watcher.Observe("▌ Working (3s · esc to interrupt)\r\n") is null, "执行中不得算返回。");
    const string done = "已完成：登录页样式已修复。\r\n" +
                        "详情：按钮颜色与间距已按设计稿调整。\r\n" +
                        "共 3 个文件已更新。\r\n\r\n> \r\n\r\n? for shortcuts\r\n";
    Assert(watcher.Observe(done) is null, "稳定帧数不足时不得返回。");
    Assert(watcher.Observe(done) is null, "稳定帧数不足时不得返回。");
    var returned = watcher.Observe(done);
    Assert(returned is not null, "连续三帧稳定空闲应报告一次返回。");
    Assert(returned!.Summary.Contains("登录页样式已修复"), $"摘要应包含回答内容，实际：{returned.Summary}");
    for (var i = 0; i < 5; i++)
        Assert(watcher.Observe(done) is null, "同一回合不得重复通知。");
}

/// <summary>报错、重连与审批菜单都不算成功返回。</summary>
private static void TestTurnWatcherRejectsFailures()
{
    var watcher = new TurnWatcher();
    watcher.Observe("▌ Working (esc to interrupt)\r\n" + ResponseErrorScreen);
    for (var i = 0; i < 5; i++)
        Assert(watcher.Observe(ResponseErrorScreen) is null, "报错画面不得算成功返回。");

    var demand = new TurnWatcher();
    demand.Observe("▌ Working (esc to interrupt)\r\n" + HighDemandScreen);
    for (var i = 0; i < 5; i++)
        Assert(demand.Observe(HighDemandScreen) is null, "高负载画面不得算成功返回。");

    var reconnect = new TurnWatcher();
    reconnect.Observe("▌ Working (esc to interrupt)\r\nReconnecting... 3/5\r\n" + IdleScreen);
    for (var i = 0; i < 5; i++)
        Assert(reconnect.Observe("Reconnecting... 3/5\r\n" + IdleScreen) is null, "重连画面不得算成功返回。");

    // 审批菜单先出现、确认后回合才真正结束：菜单本身不通知，结束后通知一次。
    var approval = new TurnWatcher();
    approval.Observe(Prompt);
    approval.Observe(Prompt);
    Assert(approval.Observe(IdleScreen) is null, "刚离开菜单时还需要稳定帧。");
    Assert(approval.Observe(IdleScreen) is null, "刚离开菜单时还需要稳定帧。");
    Assert(approval.Observe(IdleScreen) is not null, "审批后的成功回合应报告返回。");
}

/// <summary>摘要去掉界面装饰、折叠空白并截断到 120 字。</summary>
private static void TestTurnSummary()
{
    var longLine = new string('好', 300);
    var screen = "结论：登录页修复完成。\r\n\r\n> \r\n\r\n? for shortcuts\r\n" + longLine + "\r\n";
    var summary = TurnWatcher.Summarize(KeepAliveWatch.Normalize(screen));
    Assert(summary.Length == 120 && summary.EndsWith('…'), "超长摘要应截断到 120 字并以省略号结尾。");
    Assert(TurnWatcher.Summarize("›\r\n\r\n? for shortcuts\r\n") == "已返回回答", "没有内容时应回退为固定摘要。");
    Assert(TurnWatcher.Summarize("结论：登录页修复完成。\r\n\r\n> \r\n\r\n? for shortcuts\r\n")
        == "结论：登录页修复完成。", "摘要不得包含提示符与快捷键说明。");
}
```

注册：

```csharp
("turn: reports each successful return exactly once", TestTurnWatcher),
("turn: errors, reconnects and approval menus are not returns", TestTurnWatcherRejectsFailures),
("turn: summary drops chrome and caps length", TestTurnSummary),
```

- [ ] **Step 2: 运行，确认测试失败**

Run: 构建 + `CodexAutoYes.Tests.exe --filter "turn:"`
Expected: 编译失败（`TurnWatcher` 不存在）。这是本任务的失败基线。

- [ ] **Step 3: 实现 KeepAlive 复用点**

`KeepAlive.cs` 中：

1. 把 `private static bool LooksBusy(string screen)` 改为 `internal static bool LooksBusy(string screen)`。
2. 在 `LooksBusy` 旁边新增：

```csharp
/// <summary>归一化画面的尾部（12 条非空行）是否出现响应异常、高负载或重连提示。</summary>
/// <param name="signature">归一化后的画面。</param>
/// <returns>true 表示这一回合没有正常收尾。</returns>
internal static bool FooterHasProblem(string signature) =>
    ResponseError.IsMatch(Footer(signature)) ||
    HighDemand.IsMatch(Footer(signature)) ||
    Reconnect.IsMatch(Footer(signature));

/// <summary>取画面尾部用于规则判断的非空行。</summary>
/// <param name="signature">归一化后的画面。</param>
/// <returns>以换行连接的尾部内容。</returns>
private static string Footer(string signature) => string.Join('\n', signature.Split('\n')
    .Select(line => line.Trim()).Where(line => line.Length != 0).TakeLast(12));
```

- [ ] **Step 4: 实现 TurnWatcher**

新建 `TurnWatcher.cs`：

```csharp
using System.Text.RegularExpressions;

namespace CodexAutoYes;

/// <summary>一次成功返回：摘要用于通知正文。</summary>
/// <param name="Summary">从画面尾部提炼的一行回答摘要。</param>
internal sealed record TurnReturn(string Summary);

/// <summary>
/// 从终端画面判定「一轮回答成功结束」：先看到执行态（或审批菜单），
/// 再连续观察到稳定空闲且底部没有报错，才算一次正确返回；一个回合最多返回一次。
/// 只做字符串比较，不依赖 WinForms，可脱离真实终端单测。
/// </summary>
internal sealed class TurnWatcher
{
    /// <summary>稳定帧数。400ms 轮询下约 1.2 秒，足以避开流式输出的接缝。</summary>
    private const int StablePolls = 3;

    /// <summary>摘要长度上限，避免气泡被系统截断得莫名其妙。</summary>
    private const int SummaryLength = 120;

    /// <summary>界面装饰行：提示符、快捷键说明、Token 统计等。</summary>
    private static readonly Regex Chrome = new(
        @"^(?:[›❯>]\s*|\?\s*for\s+shortcuts|send\s+with\s+enter|press\s+enter\s+to\b.*|" +
        @"esc\s+to\s+cancel|token\s+usage\b.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>纯边框/分隔线。</summary>
    private static readonly Regex Decoration = new(
        @"^[\s─━│┃┄┅┆┇┈┉┊┋╭╮╯╰┌┐└┘├┤┬┴┼=_\-]+$",
        RegexOptions.CultureInvariant);

    /// <summary>当前这一回合是否见过执行态；没见过的空闲不算返回。</summary>
    private bool _sawBusy;
    private string? _idleSignature;
    private int _idlePolls;

    /// <summary>喂入一帧画面。</summary>
    /// <param name="screen">刚读到的终端画面原文。</param>
    /// <returns>本帧构成一次成功返回时返回摘要，否则 null。</returns>
    internal TurnReturn? Observe(string screen)
    {
        var signature = KeepAliveWatch.Normalize(screen);
        // 空画面（拖动终端尺寸、读取失败）不能当依据，也不改变状态。
        if (signature.Length == 0)
            return null;
        if (KeepAliveWatch.LooksBusy(screen) || ApprovalPrompt.Find(screen) is not null)
        {
            _sawBusy = true;
            _idleSignature = null;
            _idlePolls = 0;
            return null;
        }
        if (!_sawBusy)
            return null;
        if (signature != _idleSignature)
        {
            _idleSignature = signature;
            _idlePolls = 1;
            return null;
        }
        if (++_idlePolls < StablePolls)
            return null;
        _sawBusy = false;
        _idleSignature = null;
        _idlePolls = 0;
        return KeepAliveWatch.FooterHasProblem(signature) ? null : new TurnReturn(Summarize(signature));
    }

    /// <summary>从归一化画面尾部提炼一行摘要。</summary>
    /// <param name="signature">归一化后的画面。</param>
    /// <returns>去装饰、折叠空白后的摘要；没有内容时返回「已返回回答」。</returns>
    internal static string Summarize(string signature)
    {
        var lines = signature.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length != 0 && !Chrome.IsMatch(line) && !Decoration.IsMatch(line))
            .TakeLast(12)
            .ToArray();
        var text = string.Join(' ', lines);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length == 0)
            return "已返回回答";
        return text.Length <= SummaryLength ? text : text[..(SummaryLength - 1)] + "…";
    }
}
```

- [ ] **Step 5: 运行本组测试**

Run: 构建 + `CodexAutoYes.Tests.exe --filter "turn:"`
Expected: `3/3 checks passed.`（只跑新增 3 项）。

---

### Task 2: 监听结果带上返回标记

**Files:**
- Modify: `ConsoleMonitor.cs`

**Interfaces:**
- Consumes: `TurnWatcher.Observe`
- Produces: `MonitorResult` 新增 `bool Returned = false, string? Summary = null`；`ConsoleMonitor` 每轮把 `TurnReturn` 附加到结果上。

- [ ] **Step 1: 扩展记录**

```csharp
internal sealed record MonitorResult(
    string Status, string Screen, bool Sent, bool KeptAlive = false, int Resumed = 0,
    string? Prompt = null, string? Notice = null, bool Returned = false, string? Summary = null);
```

- [ ] **Step 2: Poll 中喂 watcher 并附加结果**

在 `ConsoleMonitor` 增加字段：

```csharp
/// <summary>判定「一轮回答成功结束」，每个会话独立。</summary>
private readonly TurnWatcher _turn = new();
```

`Poll()` 开头读画面后：

```csharp
var screen = TerminalConsole.Read(target);
var turn = _turn.Observe(screen);
// 同一帧上的成功返回附加到所有返回路径，避免漏掉审批、续跑等分支。
MonitorResult Attach(MonitorResult result) =>
    turn is null ? result : result with { Returned = true, Summary = turn.Summary };
```

把该方法里除 `已停止` 之外的每个 `return new(...)` 改成 `return Attach(new(...))`，包括 `Idle(...)` 与 `KeepAlive(screen)` 的调用点：

```csharp
if (!TerminalConsole.ConfirmIfUnchanged(target, prompt!.Signature))
    return Attach(Idle("监听中 · 等待终端状态稳定", screen));
_gate.MarkSent(prompt.Signature);
return Attach(new("监听中 · 已发送 y", screen, true, false, _watch.Sent));
...
if (prompt is not null)
    return Attach(Idle("监听中 · 确认菜单状态", screen));
return Attach(KeepAlive(screen));
```

- [ ] **Step 3: 构建确认无编译错误**

Run: 构建命令
Expected: `0 个错误`。

---

### Task 3: 托盘 + 通知界面

**Files:**
- Modify: `MainForm.cs`
- Modify: `tests/Program.cs`（新增 2 个 GUI 用例 + 注册）

**Interfaces:**
- Consumes: `MonitorResult.Returned`, `MonitorResult.Summary`, `TurnWatcher` 测试
- Produces（测试依赖的入口）:
  - `internal MainForm(Action<string, string>? notify = null)`
  - `internal NotifyIcon Tray => _tray;`
  - `internal void RestoreFromTray()`
  - `internal void Apply(MonitorSet monitors, SessionPoll poll)`（原 private 改 internal）

- [ ] **Step 1: 先写失败的 GUI 测试**

在 `tests/Program.cs` 新增：

```csharp
/// <summary>托盘图标常驻；最小化隐藏、恢复回任务栏；菜单启停与主界面同步。</summary>
private static void TestTrayGui()
{
    using var form = new MainForm((_, _) => { });
    form.StartPosition = FormStartPosition.Manual;
    form.Location = new Point(-20000, -20000);
    form.Opacity = 0;
    form.Show();
    var controls = Descendants(form).ToArray();
    var status = controls.OfType<Label>().Single(label => label.Name == "status");
    WaitUntil(() => status.Text != "未启动" && status.Text != "正在查找 Codex 窗口…", "窗口发现未完成。");

    Assert(form.Tray.Visible, "托盘图标应在启动后常驻。");
    var menu = form.Tray.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().ToArray();
    Assert(menu.Any(item => item.Text == "显示主窗口") && menu.Any(item => item.Text == "开始监听") &&
           menu.Any(item => item.Text == "停止监听") && menu.Any(item => item.Text == "退出"),
        "托盘菜单应含显示、开始、停止、退出。");
    var start = menu.Single(item => item.Text == "开始监听");
    var stop = menu.Single(item => item.Text == "停止监听");
    Assert(!start.Enabled && !stop.Enabled, "没有勾选会话时开始不可用，未监听时停止不可用。");

    form.WindowState = FormWindowState.Minimized;
    Application.DoEvents();
    Assert(!form.Visible && !form.ShowInTaskbar, "最小化后应隐藏到托盘且不在任务栏。");
    form.RestoreFromTray();
    Application.DoEvents();
    Assert(form.Visible && form.ShowInTaskbar &&
           form.WindowState == FormWindowState.Normal, "恢复后应回到任务栏。");

    var sessions = controls.OfType<ListView>().Single(list => list.Name == "sessions");
    sessions.Items.Clear();
    var target = new TerminalTarget(424242, 0, "Fixture tray", 0);
    var row = new ListViewItem(target.Title) { Tag = target };
    row.SubItems.Add("424242");
    for (var i = 0; i < 3; i++)
        row.SubItems.Add("0");
    sessions.Items.Add(row);
    row.Checked = true;
    Application.DoEvents();
    Assert(start.Enabled, "勾选会话后托盘开始项应可用。");
    start.PerformClick();
    Assert(stop.Enabled && !start.Enabled, "开始监听后托盘菜单应同步切换。");
    stop.PerformClick();
    Assert(start.Enabled && !stop.Enabled, "停止后托盘菜单应恢复。");
    form.Close();
}
```

```csharp
/// <summary>后台收到成功返回时通知一次；关闭开关后不再通知，但计数与日志照旧。</summary>
private static void TestReturnNotificationGui()
{
    var notifications = new List<(string Title, string Body)>();
    using var form = new MainForm((title, body) => notifications.Add((title, body)));
    form.ShowInTaskbar = false;
    form.Opacity = 0;
    form.StartPosition = FormStartPosition.Manual;
    form.Location = new Point(-20000, -20000);
    form.Show();
    var controls = Descendants(form).ToArray();
    var history = controls.OfType<TextBox>().Single(box => box.Name == "log");
    var returned = controls.OfType<Label>().Single(label => label.Name == "returned");
    var notify = controls.OfType<CheckBox>().Single(box => box.Name == "notifyOnReturn");
    var sessions = controls.OfType<ListView>().Single(list => list.Name == "sessions");
    var status = controls.OfType<Label>().Single(label => label.Name == "status");
    WaitUntil(() => status.Text != "未启动" && status.Text != "正在查找 Codex 窗口…", "窗口发现未完成。");
    Assert(notify.Checked, "通知开关默认开启。");

    sessions.Items.Clear();
    var target = new TerminalTarget(424243, 0, "Fixture return", 0);
    var row = new ListViewItem(target.Title) { Tag = target };
    row.SubItems.Add("424243");
    for (var i = 0; i < 3; i++)
        row.SubItems.Add("0");
    sessions.Items.Add(row);

    form.HideToTray();
    var monitors = new MonitorSet([]);
    var poll = new SessionPoll(new ConsoleMonitor(target),
        new MonitorResult("监听中 · 等待审批提示", "screen", false, Returned: true, Summary: "登录页样式已修复。"), null);
    form.Apply(monitors, poll);
    Assert(notifications.Count == 1, "后台返回应通知一次。");
    Assert(notifications[0].Title.Contains("Fixture return") && notifications[0].Body.Contains("登录页样式已修复"),
        $"通知应包含会话标题与摘要，实际：{notifications[0]}");
    Assert(returned.Text == "已返回 1 次", "计数应累加。");
    Assert(history.Text.Contains("已返回回答"), "操作记录应写入返回摘要。");

    notify.Checked = false;
    form.Apply(monitors, poll);
    Assert(notifications.Count == 1, "关闭开关后不得再通知。");
    Assert(returned.Text == "已返回 2 次", "关闭通知只影响气泡，计数仍应累加。");
    form.Close();
}
```

注册：

```csharp
("tray: minimize hides, restore returns and menu stays in sync", TestTrayGui),
("tray: successful return notifies only in background", TestReturnNotificationGui),
```

- [ ] **Step 2: 运行，确认失败**

Run: 构建 + `CodexAutoYes.Tests.exe --filter "tray:"`
Expected: 编译失败（`Tray`、`RestoreFromTray`、`Apply`、`HideToTray`、`notifyOnReturn` 不存在）。

- [ ] **Step 3: 加字段与构造参数**

在 `MainForm` 字段区新增：

```csharp
/// <summary>托盘图标常驻；最小化后靠它恢复。</summary>
private readonly NotifyIcon _tray = new();
/// <summary>托盘菜单的启停项，需要与主界面按钮同步可用状态。</summary>
private readonly ToolStripMenuItem _trayStart = new("开始监听");
private readonly ToolStripMenuItem _trayStop = new("停止监听");
/// <summary>返回时是否弹系统通知；监听期间也可改。</summary>
private readonly CheckBox _notify = new()
{
    Name = "notifyOnReturn", Text = "回合返回时通知（仅后台时弹窗）",
    Checked = true, AutoSize = true, Margin = new Padding(0, 5, 0, 0)
};
private readonly Label _returnedCount = new()
{
    Name = "returned", Text = "已返回 0 次", AutoSize = true, Margin = new Padding(16, 10, 0, 0)
};
/// <summary>测试注入的通知出口；默认走托盘气泡。</summary>
private readonly Action<string, string>? _notifyReturn;
/// <summary>最小化前是普通还是最大化，恢复时还原。</summary>
private FormWindowState _trayRestoreState = FormWindowState.Normal;
/// <summary>从托盘恢复时不得重跑窗口发现。</summary>
private bool _shown;
private int _returned;
private int _lastReturnPid;
private Icon? _ownedTrayIcon;
```

构造函数改为：

```csharp
internal MainForm(Action<string, string>? notify = null)
{
    _notifyReturn = notify;
    ...
    BuildLayout();
    BuildTray();
    WireEvents();
}
```

- [ ] **Step 4: 托盘初始化与窗口恢复**

```csharp
/// <summary>创建常驻托盘图标与右键菜单。</summary>
private void BuildTray()
{
    _ownedTrayIcon = TryExtractIcon();
    _tray.Icon = _ownedTrayIcon ?? SystemIcons.Application;
    _tray.Text = "Codex 自动确认";
    _tray.Visible = true;
    var show = new ToolStripMenuItem("显示主窗口", null, (_, _) => RestoreFromTray());
    _trayStart.Click += (_, _) => StartMonitoring();
    _trayStop.Click += (_, _) => StopMonitoring("已停止");
    var exit = new ToolStripMenuItem("退出", null, (_, _) => Close());
    _trayStart.Enabled = false;
    _trayStop.Enabled = false;
    _tray.ContextMenuStrip = new ContextMenuStrip();
    _tray.ContextMenuStrip.Items.AddRange(
        [show, _trayStart, _trayStop, new ToolStripSeparator(), exit]);
    _tray.DoubleClick += (_, _) => RestoreFromTray();
    _tray.BalloonTipClicked += (_, _) => SelectSession(_lastReturnPid);
}

/// <summary>取可执行文件自带的图标；取不到时回退系统图标。</summary>
/// <returns>需要由本窗体释放的图标；回退时为 null。</returns>
private static Icon? TryExtractIcon()
{
    try
    {
        return Environment.ProcessPath is null ? null : Icon.ExtractAssociatedIcon(Environment.ProcessPath);
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
    {
        return null;
    }
}

/// <summary>最小化时隐藏到托盘；监听定时器继续运行。</summary>
internal void HideToTray()
{
    ShowInTaskbar = false;
    Hide();
}

/// <summary>从托盘恢复窗口，并保持原来的普通/最大化状态。</summary>
internal void RestoreFromTray()
{
    ShowInTaskbar = true;
    Show();
    WindowState = _trayRestoreState;
    Activate();
    BringToFront();
}

/// <summary>选中指定 PID 的会话行，并让预览框显示它的画面。</summary>
/// <param name="processId">要选中的会话进程号。</param>
private void SelectSession(int processId)
{
    var row = _sessions.Items.Cast<ListViewItem>()
        .FirstOrDefault(item => TargetOf(item).ProcessId == processId);
    if (row is null)
        return;
    row.Selected = true;
    row.EnsureVisible();
    _sessions.Focus();
}
```

事件接线新增：

```csharp
Shown += async (_, _) =>
{
    if (_shown)
        return;
    _shown = true;
    await RefreshWindows();
};
Resize += (_, _) =>
{
    // 最小化前先把普通/最大化状态记下来，恢复时不能还原成 Minimized。
    if (WindowState == FormWindowState.Minimized)
        HideToTray();
    else
        _trayRestoreState = WindowState;
};
```

（把原来 `Shown += async (_, _) => await RefreshWindows();` 替换掉。）

- [ ] **Step 5: 状态栏、计数与通知开关**

布局里把 `layout.Controls.Add(_status, 0, 6);` 换成两列状态行：

```csharp
var statusRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
_status.Margin = new Padding(0, 8, 0, 0);
statusRow.Controls.Add(_status, 0, 0);
statusRow.Controls.Add(_notify, 1, 0);
layout.Controls.Add(statusRow, 0, 6);
```

动作行改为：

```csharp
actions.Controls.AddRange([_start, _stop, _count, _resumedCount, _returnedCount]);
```

可访问性补一行：

```csharp
_notify.AccessibleName = "回合成功返回时是否弹系统通知，仅在窗口不在前台时弹出";
```

- [ ] **Step 6: 通知与计数落地**

在 `Apply` 处理完续跑分支之后新增：

```csharp
if (result.Returned)
{
    _returned++;
    _returnedCount.Text = $"已返回 {_returned} 次";
    _lastReturnPid = target.ProcessId;
    var summary = result.Summary ?? "已返回回答";
    Log($"PID {target.ProcessId}（{target.Title}）已返回回答：{summary}");
    // 只在后台打扰用户；前台时列表、计数与日志已经足够。
    if (_notify.Checked && IsInBackground)
        ShowReturnNotice($"Codex 已返回：{target.Title}",
            $"{summary}{Environment.NewLine}PID {target.ProcessId} · {DateTime.Now:HH:mm:ss}");
}
```

新增方法：

```csharp
/// <summary>窗口是否不在前台：隐藏、最小化或已被其它程序抢走焦点。</summary>
private bool IsInBackground =>
    !Visible || WindowState == FormWindowState.Minimized || Form.ActiveForm != this;

/// <summary>弹返回通知；测试可注入收集器，避免真的打扰桌面。</summary>
/// <param name="title">通知标题。</param>
/// <param name="body">通知正文。</param>
private void ShowReturnNotice(string title, string body)
{
    if (_notifyReturn is not null)
    {
        _notifyReturn(Fit(title, 63), Fit(body, 255));
        return;
    }
    if (_tray.Visible)
        _tray.ShowBalloonTip(5000, Fit(title, 63), Fit(body, 255), ToolTipIcon.Info);
}

/// <summary>按系统通知的上限截断文本。</summary>
/// <param name="text">原文。</param>
/// <param name="max">最大字符数。</param>
/// <returns>超长时以省略号结尾的文本。</returns>
private static string Fit(string text, int max) =>
    text.Length <= max ? text : text[..(max - 1)] + "…";
```

`Apply` 的签名从 `private void Apply(` 改为 `internal void Apply(`。

- [ ] **Step 7: 托盘菜单状态同步**

新增：

```csharp
/// <summary>同步托盘菜单与提示文本；任何启停变化后都要调用。</summary>
private void UpdateTrayMenu()
{
    _trayStart.Enabled = _monitors is null && _sessions.CheckedItems.Count > 0;
    _trayStop.Enabled = _monitors is not null;
    _tray.Text = _monitors is null
        ? "Codex 自动确认"
        : $"Codex 自动确认 · 监听 {_monitors.Count} 个会话";
}
```

调用点：

- `UpdateStartState()` 末尾加 `UpdateTrayMenu();`
- `StartMonitoring()` 末尾（`_timer.Start();` 之后）加 `UpdateTrayMenu();`
- `StopMonitoring(...)` 末尾加 `UpdateTrayMenu();`
- `ToggleAll()` 已经调用 `UpdateStartState`，无需重复。

- [ ] **Step 8: 释放**

删除 `FormClosed += (_, _) => _timer.Dispose();`，改为重写：

```csharp
protected override void Dispose(bool disposing)
{
    if (disposing)
    {
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        _ownedTrayIcon?.Dispose();
        _timer.Dispose();
    }
    base.Dispose(disposing);
}
```

- [ ] **Step 9: 运行 GUI 测试**

Run: 构建 + `CodexAutoYes.Tests.exe --filter "tray:"`
Expected: `2/2 checks passed.`

---

### Task 4: 真实控制台的回合夹具

**Files:**
- Modify: `tests/Program.cs`（新增夹具模式 `turn`、常量、测试 + 注册）

**Interfaces:**
- Consumes: `StartFixture(string mode)`、`ConsoleMonitor.Poll()`、`MonitorResult.Returned/Summary`
- Produces: 夹具模式 `turn`；用例 `real console: turn fixture reports one successful return`。

- [ ] **Step 1: 加常量与测试**

在测试常量区新增：

```csharp
private const string BusyScreen = "▌ Working (1s · esc to interrupt)\r\n";
/// <summary>
/// 夹具的「回合结束」画面。执行提示后必须至少 5 条非空行，
/// 把旧的执行提示挤出 LooksBusy 只检查的尾部 5 行。
/// </summary>
private const string TurnIdleScreen = "已完成：登录页样式已修复。\r\n" +
                                    "详情：按钮颜色与间距已按设计稿调整。\r\n" +
                                    "共 3 个文件已更新。\r\n\r\n> \r\n\r\n? for shortcuts\r\n";
```

新增用例：

```csharp
/// <summary>真实控制台：执行提示消失并稳定后，恰好报告一次成功返回。</summary>
private static void TestTurnConsole()
{
    using var fixture = StartFixture("turn");
    var monitor = new ConsoleMonitor(fixture.Target);
    MonitorResult? returned = null;
    var count = 0;
    var deadline = Stopwatch.StartNew();
    while (count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(20))
    {
        var result = monitor.Poll();
        if (result.Returned)
        {
            count++;
            returned = result;
        }
        Thread.Sleep(100);
    }
    Assert(count == 1, "夹具会话应恰好报告一次成功返回。");
    Assert(returned!.Summary.Contains("登录页样式已修复"), $"摘要应含回答内容，实际：{returned.Summary}");
    for (var i = 0; i < 5; i++)
        Assert(!monitor.Poll().Returned, "同一回合不得重复报告返回。");
    monitor.Stop();
    File.WriteAllText(Path.Combine(fixture.Directory, "stop"), "");
}
```

注册：

```csharp
("real console: turn fixture reports one successful return", TestTurnConsole),
```

- [ ] **Step 2: 运行，确认失败**

Run: 构建 + `CodexAutoYes.Tests.exe --filter "turn fixture"`
Expected: FAIL，`Fixture did not initialize`（`turn` 模式还没实现）或 `TimeoutException`。

- [ ] **Step 3: 夹具支持 turn 模式**

`RunFixture` 中把 `raw` 判断加上 `"turn"`：

```csharp
var raw = mode is "raw" or "idle" or "idle-paste" or "error" or "demand" or "reconnect" or "turn";
```

写第一帧的逻辑改为：

```csharp
var first = mode == "turn" ? BusyScreen : screen;
Assert(WriteConsoleW(output, first, (uint)first.Length, out _, 0) != 0, "WriteConsoleW failed.");
File.WriteAllText(Path.Combine(directory, "ready.tmp"), Environment.ProcessId.ToString());
File.Move(Path.Combine(directory, "ready.tmp"), Path.Combine(directory, "ready"));
if (mode == "turn")
{
    // 先让工具看到执行态，再追加稳定空闲画面；旧提示留在上方，不影响尾部判定。
    Thread.Sleep(1500);
    Assert(WriteConsoleW(output, TurnIdleScreen, (uint)TurnIdleScreen.Length, out _, 0) != 0,
        "WriteConsoleW failed.");
}
```

（`screen` 的既有三元表达式对 `turn` 会算出 `Prompt`，但被 `first` 覆盖，不影响其它模式。）

- [ ] **Step 4: 运行本组测试**

Run: 构建 + `TEMP`/`TMP` 指向工作区 + `CodexAutoYes.Tests.exe --filter "turn"`
Expected: `4/4 checks passed.`（3 个纯逻辑 + 1 个真实控制台）。

---

### Task 5: 回归与构建产物

**Files:**
- 无源码改动

- [ ] **Step 1: 跑相关分组**

Run:

```bash
cd "D:\MyStudyFiles\aiPro\codexautoyes"
dotnet build tests/CodexAutoYes.Tests.csproj -p:ArtifactsPath="D:\MyStudyFiles\aiPro\codexautoyes\build" -v q --nologo
TEMP="D:\MyStudyFiles\aiPro\codexautoyes\build\fixturetmp" TMP="D:\MyStudyFiles\aiPro\codexautoyes\build\fixturetmp" \
  build/bin/CodexAutoYes.Tests/debug/CodexAutoYes.Tests.exe --keep-alive
TEMP="D:\MyStudyFiles\aiPro\codexautoyes\build\fixturetmp" TMP="D:\MyStudyFiles\aiPro\codexautoyes\build\fixturetmp" \
  build/bin/CodexAutoYes.Tests/debug/CodexAutoYes.Tests.exe --filter "turn"
TEMP="D:\MyStudyFiles\aiPro\codexautoyes\build\fixturetmp" TMP="D:\MyStudyFiles\aiPro\codexautoyes\build\fixturetmp" \
  build/bin/CodexAutoYes.Tests/debug/CodexAutoYes.Tests.exe --filter "tray"
```

Expected: 三次都退出码 0，分别为 26/26、4/4、2/2。

- [ ] **Step 2: 确认没有回归其它纯逻辑用例**

Run: `CodexAutoYes.Tests.exe --filter "elevation"` 和 `--filter "native structure"`
Expected: 全部 PASS。

- [ ] **Step 3: 重新发布可执行文件**

Run:

```bash
dotnet publish CodexAutoYes.csproj -c Release --no-self-contained -o artifacts
```

Expected: `0 个错误`，`artifacts/CodexAutoYes.exe` 时间戳更新。

- [ ] **Step 4: 截图确认布局**

Run: `CodexAutoYes.Tests.exe --filter "GUI rendering"`
Expected: PASS，`artifacts/ui-preview.png` 更新；人工确认状态行右侧通知开关、动作行「已返回 0 次」不挤压。

---

## Self-Review

- 规格覆盖：托盘隐藏/恢复/菜单（Task 3）、回合判定与摘要（Task 1）、结果冒泡（Task 2）、后台通知与点击定位（Task 3）、真实终端验证（Task 4）、边界：报错/重连/审批不算返回（Task 1 测试）、空画面与重复通知（Task 1）。
- 类型一致性：`TurnReturn(string Summary)`、`TurnWatcher.Observe`、`FooterHasProblem(string)`、`MonitorResult.Returned/Summary`、`MainForm(Action<string,string>?)`、`Tray`、`Apply`、`HideToTray`、`RestoreFromTray` 在测试与实现中名称一致。
- 规格中的「点击通知选中会话」由 `BalloonTipClicked` + `SelectSession` 实现，测试只覆盖恢复与通知出口（系统气泡点击无法在无人值守测试中模拟），在计划的风险里注明。
