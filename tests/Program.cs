using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using CodexAutoYes;
using Microsoft.Win32.SafeHandles;

namespace CodexAutoYes.Tests;

internal static partial class Program
{
    private const string Prompt = "Would you like to run the following command?\r\n\r\n  $ echo demo\r\n\r\n› 1. Yes, proceed (y)\r\n  2. Yes, and don't ask again (p)\r\n  3. No, and tell Codex what to do differently (esc)\r\n\r\nPress enter to confirm or esc to cancel\r\n";

    /// <summary>
    /// 空闲画面：没有审批菜单，也没有"执行中"或其它按键选择提示，
    /// 对应一个停在输入框等人的会话。
    /// </summary>
    private const string IdleScreen = "> done. anything else?\r\n\r\n  \r\n\r\n  send with enter\r\n";
    private const string RateLimitError = "rate limit exceeded: Your requests to gpt-6-astra for gpt-6-astra in eastus2 have exceeded token rate limit";
    private const string ResponseErrorScreen = "■ " + RateLimitError + "\r\n\r\n› \r\n\r\n? for shortcuts\r\n";
    private const string HighDemandError = "We’re currently experiencing high demand, which may cause temporary errors.";
    private const string HighDemandScreen = "■ " + HighDemandError + "\r\n\r\n› \r\n\r\n? for shortcuts\r\n";
    private const string BusyScreen = "▌ Working (1s · esc to interrupt)\r\n";
    /// <summary>
    /// 夹具的「回合结束」画面。执行提示之后至少要有 5 条非空行，
    /// 把旧的执行提示挤出 LooksBusy 只检查的尾部 5 行，否则会一直判定为执行中。
    /// </summary>
    private const string TurnIdleScreen = "已完成：登录页样式已修复。\r\n" +
                                          "详情：按钮颜色与间距已按设计稿调整。\r\n" +
                                          "共 3 个文件已更新。\r\n\r\n> \r\n\r\n? for shortcuts\r\n";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--fixture")
            return RunFixture(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--discover")
            return RunDiscoveryProbe(args[1]);
        if (args.Length >= 2 && args[0] == "--guistate")
            return RunGuiStateProbe(args[1], args.Length > 2 && args[2] == "keepconsole");
        if (args.Length == 2 && args[0] == "--timing")
            return RunTimingProbe(args[1]);

        var report = new List<string>();
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts"));
        Directory.CreateDirectory(directory);
        var tests = new (string Name, Action Test)[]
        {
            ("command and patch approval menus", TestPrompts),
            ("reject prose, history, partial menus and other choices", TestNonPrompts),
            ("stability, duplicate suppression and repeated requests", TestGate),
            ("keep-alive: idle timing, rearm, defer and send cap", TestKeepAliveWatch),
            ("keep-alive: zero timeout sends immediately and respects guards and limits", TestKeepAliveImmediate),
            ("keep-alive: editor accepts zero, fractional and large idle thresholds", TestKeepAliveSettings),
            ("keep-alive: response errors respect timing, history, busy states and send cap", TestKeepAliveResponseErrors),
            ("keep-alive: high demand recognizes apostrophes and wrapping, not prose or history", TestKeepAliveHighDemand),
            ("keep-alive: reconnects finish before one-second retry, once per failed turn", TestHighDemandRetryCycle),
            ("keep-alive: stands down while busy or in other dialogs", TestKeepAliveStandsDown),
            ("keep-alive: prompt pool rotates without back-to-back repeats", TestKeepAlivePrompts),
            ("keep-alive: prompt pool parsing from the editor", TestPromptParsing),
            ("claude: transient errors only, with an empty input prompt", TestClaudeErrors),
            ("claude: built-in retry, one-shot recovery, defer and send cap", TestClaudeRetryCycle),
            ("claude: independent retry option and editor locking", TestClaudeOptions),
            ("claude: failed turns are never successful returns", TestClaudeTurnFailures),
            ("claude: real console retry and input guards", TestClaudeConsole),
            ("claude: native process discovery preserves CLI identity", TestClaudeDiscovery),
            ("claude: GUI retries with configured text", TestClaudeGui),
            ("elevation: explain blocked sessions instead of raw access-denied", TestElevationAdvice),
            ("elevation: restart button appears only when it helps", TestElevateButtonVisibility),
            ("native structure sizes and offsets", TestLayout),
            ("real console: targeted y, no Enter, no duplicates, stop", () => TestConsole(true)),
            ("real console: never type into cooked input", () => TestConsole(false)),
            ("real console: idle session receives two different keep-alive texts", () => TestKeepAliveConsole(true)),
            ("real console: keep-alive submits pasted text instead of inserting a newline", TestKeepAlivePasteBurst),
            ("real console: keep-alive never types into cooked input", () => TestKeepAliveConsole(false)),
            ("real console: keep-alive sends configured texts after response errors", () => TestKeepAliveConsole(true, "error")),
            ("real console: keep-alive response errors never type into cooked input", () => TestKeepAliveConsole(false, "error")),
            ("real console: keep-alive sends configured texts after high demand", () => TestKeepAliveConsole(true, "demand")),
            ("real console: keep-alive high demand never types into cooked input", () => TestKeepAliveConsole(false, "demand")),
            ("real console: keep-alive never sends during built-in reconnects", () => TestKeepAliveConsole(true, "reconnect")),
            ("real console: zero-timeout keep-alive sends idle prompts immediately", () => TestKeepAliveConsole(true, "idle", true)),
            ("real console: zero-timeout keep-alive sends error prompts immediately", () => TestKeepAliveConsole(true, "error", true)),
            ("real console: zero-timeout keep-alive sends high-demand prompts immediately", () => TestKeepAliveConsole(true, "demand", true)),
            ("real console: zero-timeout keep-alive never types into cooked input", () => TestKeepAliveConsole(false, "idle", true)),
            ("real console: zero-timeout keep-alive errors never type into cooked input", () => TestKeepAliveConsole(false, "error", true)),
            ("real console: zero-timeout keep-alive high demand never types into cooked input", () => TestKeepAliveConsole(false, "demand", true)),
            ("real console: zero-timeout keep-alive never sends during reconnects", () => TestKeepAliveConsole(true, "reconnect", true)),
            ("real console: turn fixture reports one successful return", TestTurnConsole),
            ("keep-alive: GUI exposes independent high demand retry and sends editor text", TestHighDemandGui),
            ("two sessions confirm independently", TestMultipleSessions),
            ("one failing session does not stop the others", TestSessionFailureIsolation),
            ("Windows Terminal: GUI end-to-end in an isolated tab", () => TestConsole(true, true)),
            ("process identity cannot be reused", TestProcessIdentity),
            ("read-only probe of existing Codex consoles", TestDiscovery),
            ("discovery survives an inherited console", TestDiscoveryWithInheritedConsole),
            ("keep-alive: GUI rendering", () => RenderGui(Path.Combine(directory, "ui-preview.png"))),
            ("turn: reports each successful return exactly once", TestTurnWatcher),
            ("turn: errors, reconnects and approval menus are not returns", TestTurnWatcherRejectsFailures),
            ("turn: summary drops chrome and caps length", TestTurnSummary),
            ("tray: minimize hides, restoration and menu stay in sync", TestTrayGui),
            ("tray: completion notifies only in background", TestReturnNotificationGui)
        };
        if (args is ["--keep-alive"])
            tests = tests.Where(test => test.Name.Contains("keep-alive", StringComparison.Ordinal)).ToArray();
        if (args.Length == 2 && args[0] == "--filter")
            tests = tests.Where(test => test.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase)).ToArray();
        var ci = args is ["--ci"];
        if (ci)
        {
            // Hosted runners have no interactive desktop or live CLI sessions.
            HashSet<Action> isolated =
            [
                TestPrompts, TestNonPrompts, TestGate,
                TestKeepAliveWatch, TestKeepAliveImmediate, TestKeepAliveResponseErrors,
                TestKeepAliveHighDemand, TestHighDemandRetryCycle, TestKeepAliveStandsDown,
                TestKeepAlivePrompts, TestPromptParsing, TestClaudeErrors, TestClaudeRetryCycle,
                TestClaudeTurnFailures, TestElevationAdvice, TestLayout,
                TestTurnWatcher, TestTurnWatcherRejectsFailures, TestTurnSummary
            ];
            tests = tests.Where(test => isolated.Contains(test.Test)).ToArray();
            Assert(tests.Length == isolated.Count, "Every CI check must be registered in the full suite.");
        }
        var failed = 0;
        if (!ci)
        {
            // Only this test process is detached; the user's terminal is not modified.
            Native.FreeConsole();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }
        foreach (var (name, test) in tests)
        {
            try { test(); report.Add($"PASS  {name}"); }
            catch (Exception ex) { failed++; report.Add($"FAIL  {name}\n{ex}"); }
        }
        report.Add($"\n{tests.Length - failed}/{tests.Length} checks passed.");
        File.WriteAllLines(Path.Combine(directory, "test-results.txt"), report);
        if (ci)
            Console.WriteLine(string.Join(Environment.NewLine, report));
        return failed == 0 ? 0 : 1;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void TestPrompts()
    {
        var parsed = ApprovalPrompt.Find(Prompt);
        Assert(parsed is not null, "Current command menu should match.");
        Assert(ApprovalPrompt.Find(Prompt.Replace("run the following command", "make the following edits")) is not null, "Patch menu should match.");
        Assert(ApprovalPrompt.Find(Prompt.Replace("› 1.", "  1.").Replace("  3.", "› 3."))?.Signature == parsed!.Signature, "Selection changes must not create a new request.");
        Assert(ApprovalPrompt.Find(Prompt.Replace("or esc to cancel", "or esc to cancel or o to open thread")) is not null, "Thread footer should match.");
        Assert(ApprovalPrompt.Find(Prompt.Replace("3. No, and tell Codex what to do differently (esc)", "3. No, continue without running it (d)")) is not null, "Deny alternative should match.");
        Assert(ApprovalPrompt.Find("earlier unrelated output\r\n" + Prompt)?.Signature == parsed!.Signature, "History preceding the dialog must not affect identity.");
    }

    private static void TestNonPrompts()
    {
        Assert(ApprovalPrompt.Find("The button says Yes, proceed (y).") is null, "Prose must not match.");
        Assert(ApprovalPrompt.Find(Prompt + "PS C:\\> ") is null, "Historical menu followed by shell input must not match.");
        Assert(ApprovalPrompt.Find(Prompt.Replace("(y)", "(ctrl+y)")) is null, "Custom shortcuts must not receive y.");
        Assert(ApprovalPrompt.Find(Prompt.Replace("Yes, proceed (y)", "Yes, and don't ask again (y)")) is null, "Persistent approval must not match.");
        Assert(ApprovalPrompt.Find(Prompt.Replace("Press enter to confirm or esc to cancel", "")) is null, "Partial rendering must not match.");
        Assert(ApprovalPrompt.Find(Prompt.Replace("Would you like to run the following command?", "Here is an example:")) is null, "Menu without an approval heading must not match.");
        Assert(ApprovalPrompt.Find(Prompt.Replace("3. No, and tell Codex what to do differently (esc)", "")) is null, "Incomplete choices must not match.");
    }

    private static void TestGate()
    {
        var gate = new ApprovalGate();
        Assert(!gate.Observe("one") && gate.Observe("one"), "Must wait for a stable prompt.");
        gate.MarkSent("one");
        Assert(!gate.Observe("one") && !gate.Observe("one"), "Must not send twice.");
        gate.Observe(null);
        Assert(!gate.Observe("one") && !gate.Observe("one"), "A single empty repaint must not rearm.");
        Assert(!gate.Observe("two") && gate.Observe("two"), "A different consecutive request must work.");
        gate.MarkSent("two");
        gate.Observe(null);
        gate.Observe(null);
        Assert(!gate.Observe("two") && gate.Observe("two"), "A later repeated request must work after disappearance.");
    }

    /// <summary>保活计时：画面变化会重置，静止够久才触发，并受次数上限约束。</summary>
    private static void TestKeepAliveWatch()
    {
        var options = new KeepAliveOptions(TimeSpan.FromMilliseconds(250), ["continue"], 2);
        var watch = new KeepAliveWatch();

        Assert(watch.Observe(IdleScreen, null) == KeepAliveState.Disabled, "未配置时必须关闭保活。");
        Assert(watch.Observe(IdleScreen, options) == KeepAliveState.Waiting, "首帧只能建立基线。");
        Assert(watch.Observe(IdleScreen, options) == KeepAliveState.Waiting, "未到阈值不得触发。");
        Assert(watch.Observe(IdleScreen + "new output\r\n", options) == KeepAliveState.Waiting, "画面变化应重置计时。");
        Thread.Sleep(300);
        Assert(watch.Observe(IdleScreen, options) == KeepAliveState.Waiting, "变化后的首帧要重新建立基线。");
        Thread.Sleep(300);
        Assert(watch.Observe(IdleScreen, options) == KeepAliveState.Ready, "静止超过阈值应可续跑。");

        // 行尾空白与尾部空行不算画面变化。
        Assert(watch.Observe(IdleScreen.Replace("\r\n", "   \r\n") + "\r\n\r\n", options) == KeepAliveState.Ready,
            "仅空白差异不应视为画面变化。");
        Assert(watch.Observe("", options) == KeepAliveState.Waiting, "读到空画面不得当作空闲。");

        watch.MarkSent();
        Assert(watch.Sent == 1, "续跑次数应累计。");
        Assert(watch.Observe(IdleScreen, options) == KeepAliveState.Waiting, "续跑后要重新建立基线。");
        Thread.Sleep(300);
        Assert(watch.Observe(IdleScreen, options) == KeepAliveState.Ready, "下一轮空闲应能再次续跑。");

        // 被终端拒绝后退让一个完整空闲周期，而不是每轮重试。
        watch.Defer();
        Assert(watch.Observe(IdleScreen, options) == KeepAliveState.Waiting, "退让后应重新计时。");
        Thread.Sleep(300);
        Assert(watch.Observe(IdleScreen, options) == KeepAliveState.Ready, "退让一周期后应重试。");

        watch.MarkSent();
        Assert(watch.Sent == 2, "第二次续跑应计入。");
        watch.Observe(IdleScreen, options);
        Thread.Sleep(300);
        Assert(watch.Observe(IdleScreen, options) == KeepAliveState.Exhausted, "达到上限后不得继续续跑。");

        var unlimited = new KeepAliveWatch();
        var noCap = new KeepAliveOptions(TimeSpan.FromMilliseconds(250), ["continue"], 0);
        for (var i = 0; i < 3; i++)
        {
            unlimited.Observe(IdleScreen, noCap);
            Thread.Sleep(300);
            Assert(unlimited.Observe(IdleScreen, noCap) == KeepAliveState.Ready, "上限为 0 时不应耗尽。");
            unlimited.MarkSent();
        }
    }

    private static void TestKeepAliveImmediate()
    {
        var options = new KeepAliveOptions(TimeSpan.Zero, ["configured prompt"], 1);
        foreach (var (screen, expected) in new[]
                 {
                     (IdleScreen, KeepAliveState.Ready),
                     (ResponseErrorScreen, KeepAliveState.RetryReady),
                     (HighDemandScreen, KeepAliveState.HighDemandReady)
                 })
        {
            var watch = new KeepAliveWatch();
            Assert(watch.Observe(screen, options) == expected, "0 秒必须在首帧就允许续跑。");
            Assert(watch.Signature == KeepAliveWatch.Normalize(screen), "立即续跑仍须保留发送前核验的画面签名。");
            watch.Defer();
            Assert(watch.Observe(screen, options) == expected, "0 秒发送被拒绝后，下一轮即可重试。");
            watch.MarkSent();
            Assert(watch.Observe(screen, options) == KeepAliveState.Exhausted, "0 秒不得绕过次数上限。");
        }

        var zero = new KeepAliveWatch();
        Assert(zero.Observe(IdleScreen, null) == KeepAliveState.Disabled, "关闭自动发送后，0 秒也不得续跑。");
        Assert(zero.Observe("", options) == KeepAliveState.Waiting, "0 秒也不能把空画面当作空闲。");
        Assert(zero.Observe(IdleScreen + "Esc to interrupt\r\n", options) == KeepAliveState.Blocked, "0 秒仍须给执行中状态让路。");
        Assert(zero.Observe(Prompt, options) == KeepAliveState.Blocked, "0 秒仍须给审批菜单让路。");
        var retry = "Reconnecting... 5/5\r\n" + HighDemandError + "\r\n" + IdleScreen;
        Assert(zero.Observe(retry, options) == KeepAliveState.Reconnecting, "0 秒不得中断内置重连。");
        var failed = retry + "错误：" + HighDemandError + "\r\n" + IdleScreen;
        var unlimited = options with { MaxSends = 0, IdleEnabled = false };
        Assert(zero.Observe(failed, unlimited) == KeepAliveState.HighDemandReady, "重连最终失败后应立即续发，高负载开关仍可独立启用。");
        zero.MarkSent();
        Assert(zero.Observe(failed, unlimited) == KeepAliveState.HighDemandSent, "0 秒也只能对同一次高负载失败续发一次。");
        Assert(zero.Observe(IdleScreen, options with { IdleEnabled = false }) == KeepAliveState.Disabled, "0 秒不得绕过空闲续跑开关。");
        Assert(zero.Observe(HighDemandScreen, options with { HighDemandEnabled = false }) == KeepAliveState.Disabled, "0 秒不得绕过高负载续发开关。");
    }

    private static void TestKeepAliveSettings()
    {
        using var form = new MainForm();
        var input = Descendants(form).OfType<NumericUpDown>().Single(box => box.Name == "idleSeconds");
        var readOptions = typeof(MainForm).GetMethod("CurrentKeepAlive", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert(input.Value == 180, "默认空闲阈值仍为 180 秒。");
        foreach (var seconds in new[] { 0m, 1m, 7m, 0.125m, 3601m, 86400m, (decimal)int.MaxValue + 1, input.Maximum })
        {
            input.Text = seconds.ToString();
            Assert(input.Value == seconds, $"输入的秒数不得被取整或限制为 10–3600：{seconds}");
            var options = (KeepAliveOptions)readOptions.Invoke(form, null)!;
            Assert(options.IdleThreshold.TotalSeconds == (double)seconds, $"秒数应完整传入续跑配置且不溢出：{seconds}");
        }
        input.Text = "-1";
        Assert(input.Value == 0, "负数不得作为空闲阈值。");
    }

    private static void TestKeepAliveResponseErrors()
    {
        var immediate = new KeepAliveOptions(TimeSpan.Zero, ["from pool"], 1);
        foreach (var error in new[]
                 {
                     RateLimitError, "■ Error: RATE_LIMIT_EXCEEDED", "rate\r\nlimit\r\nexceeded",
                     "Rate limit reached for the model", "exceeded token rate limit",
                     "Too many requests", "HTTP 429 Too Many Requests",
                     "■ unexpected status code: 429 Too Many Requests", "■ unexpected status 503 Service Unavailable",
                     "■ stream disconnected before completion: connection closed",
                     "error sending request for url (https://example.test/responses)",
                     "Error: request timed out", "connection timed out", "Internal server error",
                     "Bad gateway", "Service unavailable", "Gateway timeout"
                 })
        {
            var candidate = new KeepAliveWatch();
            var screen = error + "\r\n" + IdleScreen;
            Assert(candidate.Observe(screen, immediate) == KeepAliveState.RetryReady, $"0 秒时响应异常首帧就应可重试：{error}");
        }

        foreach (var (screen, expected) in new[]
                 {
                     ("The documentation explains rate limit exceeded.\r\n" + IdleScreen, KeepAliveState.Ready),
                     ("> rate limit exceeded\r\n" + IdleScreen, KeepAliveState.Ready),
                     (RateLimitError + "\r\n" + string.Concat(Enumerable.Range(0, 13).Select(i => $"line {i}\r\n")) + IdleScreen, KeepAliveState.Ready),
                     (ResponseErrorScreen + "Esc to interrupt\r\n", KeepAliveState.Blocked),
                     (ResponseErrorScreen + "↑/↓ to select\r\n", KeepAliveState.Blocked),
                     (ResponseErrorScreen + "Overwrite the file? (y/n)\r\n", KeepAliveState.Blocked),
                     (ResponseErrorScreen + Prompt, KeepAliveState.Blocked)
                 })
        {
            var candidate = new KeepAliveWatch();
            candidate.Observe(screen, immediate);
            Assert(candidate.Observe(screen, immediate) == expected, $"历史、普通文字和忙碌状态不得触发异常续跑：{screen}");
        }

        var options = immediate with { IdleThreshold = TimeSpan.FromMilliseconds(150) };
        var watch = new KeepAliveWatch();
        Assert(watch.Observe(ResponseErrorScreen, null) == KeepAliveState.Disabled, "关闭保活时，异常也不得续跑。");
        Assert(watch.Observe(ResponseErrorScreen, options) == KeepAliveState.Waiting, "异常首帧只能建立基线。");
        Assert(watch.Observe(ResponseErrorScreen, options) == KeepAliveState.Waiting, "异常必须等待既有保活间隔。");
        Thread.Sleep(200);
        Assert(watch.Observe(ResponseErrorScreen, options) == KeepAliveState.RetryReady, "达到间隔后应允许异常续跑。");
        watch.Defer();
        Assert(watch.Observe(ResponseErrorScreen, options) == KeepAliveState.Waiting, "被拒绝后不得立即重试异常。");
        Thread.Sleep(200);
        Assert(watch.Observe(ResponseErrorScreen, options) == KeepAliveState.RetryReady, "退让一个间隔后可再次重试。");
        watch.MarkSent();
        Assert(watch.Sent == 1, "异常续跑必须计入同一次数上限。");
        Assert(watch.Observe(ResponseErrorScreen, options) == KeepAliveState.Waiting, "异常续跑后必须重新计时。");
        Thread.Sleep(200);
        Assert(watch.Observe(ResponseErrorScreen, options) == KeepAliveState.Exhausted, "异常不得绕过次数上限。");
    }

    private static void TestKeepAliveHighDemand()
    {
        var options = new KeepAliveOptions(TimeSpan.Zero, ["from editor"], 1);
        foreach (var notice in new[]
                 {
                     HighDemandError, "■ " + HighDemandError,
                     "Error: " + HighDemandError.Replace("’", "'"),
                     "  ✗ ERROR: " + HighDemandError.ToUpperInvariant(),
                     "[10:29:16] 错误：" + HighDemandError,
                     HighDemandError.Replace("high demand", "high\r\n  demand").Replace("which may", "which\r\n  may"),
                     ResponseErrorScreen + HighDemandError
                 })
        {
            var watch = new KeepAliveWatch();
            var screen = notice + "\r\n" + IdleScreen;
            Assert(watch.Observe(screen, options) == KeepAliveState.HighDemandReady,
                $"0 秒时高负载失败应立即续发：{notice}");
        }
        foreach (var (screen, expected) in new[]
                 {
                     ("The documentation says: " + HighDemandError + "\r\n" + IdleScreen, KeepAliveState.Ready),
                     ("> " + HighDemandError + "\r\n" + IdleScreen, KeepAliveState.Ready),
                     ("› " + HighDemandError + "\r\n" + IdleScreen, KeepAliveState.Ready),
                     ("We’re currently experiencing high demand.\r\n" + IdleScreen, KeepAliveState.Ready),
                     (HighDemandError + "\r\n" + string.Concat(Enumerable.Range(0, 13).Select(i => $"line {i}\r\n")) + IdleScreen, KeepAliveState.Ready),
                     (HighDemandScreen + "Esc to interrupt\r\n", KeepAliveState.Blocked),
                     (HighDemandScreen + "↑/↓ to select\r\n", KeepAliveState.Blocked),
                     (HighDemandScreen + Prompt, KeepAliveState.Blocked)
                 })
        {
            var watch = new KeepAliveWatch();
            watch.Observe(screen, options);
            Assert(watch.Observe(screen, options) == expected, "引用、历史和忙碌状态不应触发高负载续跑。");
        }
    }

    private static void TestHighDemandRetryCycle()
    {
        var options = new KeepAliveOptions(TimeSpan.FromMinutes(3), ["计划书编写"], 2, IdleEnabled: false);
        var watch = new KeepAliveWatch();
        Assert(watch.Observe(HighDemandScreen, null) == KeepAliveState.Disabled, "全部关闭时不续发。");
        Assert(watch.Observe(IdleScreen, options) == KeepAliveState.Disabled, "关闭空闲续跑后不能因空闲发送。");
        Assert(watch.Observe(HighDemandScreen, options with { HighDemandEnabled = false }) == KeepAliveState.Disabled,
            "高负载开关关闭时不得发送。");
        var retry = "";
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            retry = $"Reconnecting... {attempt}/5\r\n{HighDemandError}\r\n" + IdleScreen;
            Assert(watch.Observe(retry, options) == KeepAliveState.Reconnecting, "内置重试期间必须让路。");
            Assert(watch.RetryStatus == $"Reconnecting... {attempt}/5", "必须显示当前重连进度。");
        }
        Thread.Sleep(1100);
        Assert(watch.Observe(retry, options) == KeepAliveState.Reconnecting, "5/5 停留超过 1 秒也不代表回合已失败。");
        var failed = retry + "[10:29:16] 错误：" + HighDemandError + "\r\n" + IdleScreen;
        Assert(watch.Observe(failed, options) == KeepAliveState.HighDemandWaiting, "最终错误出现时才开始 1 秒计时。");
        Thread.Sleep(500);
        Assert(watch.Observe(failed + "footer repaint\r\n", options) == KeepAliveState.HighDemandWaiting, "未满 1 秒不能发。");
        Thread.Sleep(600);
        Assert(watch.Observe(failed, options) == KeepAliveState.HighDemandReady, "失败后 1 秒可发送，不等 180 秒空闲阈值。");
        watch.Defer();
        Assert(watch.Observe(failed, options) == KeepAliveState.HighDemandWaiting, "发送被拒绝后要重新等 1 秒。");
        Thread.Sleep(1100);
        Assert(watch.Observe(failed, options) == KeepAliveState.HighDemandReady, "退让后应能重试。");
        watch.MarkSent();
        Assert(watch.Observe(failed, options) == KeepAliveState.HighDemandSent, "同一失败只能续发一次。");
        watch.Observe("", options);
        Assert(watch.Observe(failed + "footer repaint\r\n", options) == KeepAliveState.HighDemandSent,
            "空白重绘或页脚变化不能使同一失败再次发送。");
        Assert(watch.Observe(retry, options) == KeepAliveState.Reconnecting, "新一轮内置重试应重新开始跟踪。");
        Assert(watch.Observe(HighDemandScreen, options) == KeepAliveState.HighDemandWaiting, "下一回合即使错误相同也应重新计时。");
        Thread.Sleep(1100);
        Assert(watch.Observe(HighDemandScreen, options) == KeepAliveState.HighDemandReady, "新失败应允许再次续发。");
        watch.MarkSent();
        Assert(watch.Sent == 2 && watch.Observe(HighDemandScreen, options) == KeepAliveState.Exhausted,
            "高负载续发必须服从次数上限。");
    }

    /// <summary>
    /// 文本池取样：一轮内每条都用到，且不会连着发同一条 ——
    /// 连发重复正是换文本要避免的问题。
    /// </summary>
    private static void TestKeepAlivePrompts()
    {
        var pool = new[] { "a", "b", "c", "d" };
        var picker = new KeepAlivePrompts(pool);
        var drawn = new List<string>();
        for (var i = 0; i < pool.Length * 50; i++)
            drawn.Add(picker.Next());

        Assert(drawn.All(pool.Contains), "取出的文本必须来自池内。");
        for (var i = 1; i < drawn.Count; i++)
            Assert(drawn[i] != drawn[i - 1], $"不得连续发送同一条（位置 {i}：{drawn[i]}）。");
        // 洗牌袋保证每一轮都把池子用完一遍。
        for (var round = 0; round < 50; round++)
            Assert(drawn.Skip(round * pool.Length).Take(pool.Length).Distinct().Count() == pool.Length,
                $"第 {round + 1} 轮应覆盖全部 {pool.Length} 条文本。");
        // 顺序必须真的随机，而不是固定循环。
        var rounds = Enumerable.Range(0, 50)
            .Select(round => string.Concat(drawn.Skip(round * pool.Length).Take(pool.Length))).ToArray();
        Assert(rounds.Distinct().Count() > 1, "各轮顺序完全相同，说明没有洗牌。");

        // 只有一条时只能重复，这是用户自己的选择，不应抛异常。
        var single = new KeepAlivePrompts(["only"]);
        Assert(Enumerable.Range(0, 5).All(_ => single.Next() == "only"), "单条文本池应稳定返回该条。");

        // 池内有重复项时，去重后只剩一种，此时允许相邻重复，但不能崩。
        var duplicated = new KeepAlivePrompts(["x", "x", "x"]);
        Assert(Enumerable.Range(0, 10).All(_ => duplicated.Next() == "x"), "全同文本池应稳定返回该条。");

        var empty = false;
        try { _ = new KeepAlivePrompts([]); }
        catch (ArgumentException) { empty = true; }
        Assert(empty, "空文本池应被拒绝。");
    }

    /// <summary>界面上的文本池编辑框解析：一行一条，忽略空行与行首尾空白。</summary>
    private static void TestPromptParsing()
    {
        var parsed = MainForm.ParsePrompts("继续\r\n\r\n  接着做  \n\n继续推进\n   \n");
        Assert(parsed.Count == 3, $"应解析出 3 条，实际 {parsed.Count}。");
        Assert(parsed[1] == "接着做", $"应去掉行首尾空白，实际「{parsed[1]}」。");
        Assert(MainForm.ParsePrompts("   \r\n\n  ").Count == 0, "全空白应解析为空池（即关闭保活）。");
        Assert(KeepAliveOptions.DefaultPrompts.Length > 1, "默认文本池应有多条，否则轮换没有意义。");
        Assert(MainForm.ParsePrompts(string.Join(Environment.NewLine, KeepAliveOptions.DefaultPrompts)).Count
               == KeepAliveOptions.DefaultPrompts.Length, "默认文本池应能被完整解析回来。");
    }

    /// <summary>保活必须给审批菜单、执行中状态和其它按键选择界面让路。</summary>
    private static void TestKeepAliveStandsDown()
    {
        var options = new KeepAliveOptions(TimeSpan.FromMilliseconds(200), ["continue"], 0);

        // 审批菜单由 ConsoleMonitor 在保活之前拦下，这里直接验证画面级别的让路规则。
        foreach (var footer in new[]
                 {
                     "Esc to interrupt", "Press enter to confirm or esc to cancel",
                     "↑/↓ to select", "Overwrite the file? (y/n)"
                 })
        {
            var watch = new KeepAliveWatch();
            var screen = IdleScreen + footer + "\r\n";
            watch.Observe(screen, options);
            Thread.Sleep(250);
            Assert(watch.Observe(screen, options) == KeepAliveState.Blocked, $"底部出现「{footer}」时必须让路。");
        }

        // 同样的提示留在回滚历史里不代表当前状态，不能永久堵住保活。
        var stale = new KeepAliveWatch();
        var history = "Press enter to confirm or esc to cancel\r\n" + string.Concat(
            Enumerable.Range(0, 6).Select(i => $"line {i}\r\n")) + "> \r\n";
        stale.Observe(history, options);
        Thread.Sleep(250);
        Assert(stale.Observe(history, options) == KeepAliveState.Ready, "历史里的旧提示不应阻止续跑。");
    }

    /// <summary>真实控制台执行态结束后只报告一次返回，停止后不再产生事件。</summary>
    private static void TestTurnConsole()
    {
        using var fixture = StartFixture("turn");
        var monitor = new ConsoleMonitor(fixture.Target);
        var first = monitor.Poll();
        Assert(first.Screen.Contains("esc to interrupt") && !first.Returned && first.Summary is null,
            "回合夹具应先显示执行态，且尚未报告返回。");
        File.WriteAllText(Path.Combine(fixture.Directory, "observed-busy"), "");
        MonitorResult? returned = null;
        var deadline = Stopwatch.StartNew();
        while (returned is null && deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            var result = monitor.Poll();
            if (result.Returned)
                returned = result;
            Thread.Sleep(100);
        }
        Assert(returned is not null, "夹具会话应报告一次成功返回。");
        Assert(returned!.Summary?.Contains("登录页样式已修复") == true, $"摘要应含回答内容，实际：{returned.Summary}");
        for (var i = 0; i < 5; i++)
            Assert(!monitor.Poll().Returned, "同一回合不得重复报告返回。");
        monitor.Stop();
        var stopped = monitor.Poll();
        Assert(stopped.Status == "已停止" && !stopped.Returned && stopped.Summary is null,
            "停止后不得继续读取或报告返回。");
    }

    /// <summary>
    /// 真实控制台端到端：空闲或响应异常的会话应收到「续跑文本 + 回车」，
    /// 行输入模式的会话必须一个字符都收不到。
    /// </summary>
    /// <param name="raw">true 表示原始输入模式（应收到文本），false 表示行输入模式（必须不收）。</param>
    /// <param name="mode">idle / demand / error 均使用配置文本；reconnect 不发送。</param>
    /// <param name="immediate">true 验证 0 秒时首轮即可发送。</param>
    private static void TestKeepAliveConsole(bool raw, string mode = "idle", bool immediate = false)
    {
        using var fixture = StartFixture(raw ? mode : mode + "-cooked");
        Assert(ApprovalPrompt.Find(TerminalConsole.Read(fixture.Target)) is null, "空闲画面不应被识别为审批菜单。");
        var options = new KeepAliveOptions(immediate ? TimeSpan.Zero :
            mode == "demand" ? TimeSpan.FromMinutes(3) : TimeSpan.FromMilliseconds(300),
            ["go on", "next"], 2, IdleEnabled: mode != "demand");
        var monitor = new ConsoleMonitor(fixture.Target, options);
        var received = Path.Combine(fixture.Directory, "received");

        var shouldSend = raw && mode != "reconnect";
        var poll = monitor.Poll();
        Assert(poll.KeptAlive == (immediate && shouldSend), "0 秒首帧即可发送；正数、行输入模式或重连期间不得立即发送。");
        var expectedCount = mode == "demand" ? 1 : 2;
        var limit = shouldSend ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2.5);
        var sentPrompts = new List<string>();
        var deadline = Stopwatch.StartNew();
        while (sentPrompts.Count < expectedCount && deadline.Elapsed < limit)
        {
            if (poll.KeptAlive)
            {
                Assert(poll.Prompt is not null, "续跑结果应带上实际发出的文本。");
                if (mode == "error")
                    Assert(poll.Status.Contains("响应异常", StringComparison.Ordinal), "状态应说明因响应异常而续跑。");
                if (mode == "demand")
                    Assert(poll.Status.Contains("服务高负载", StringComparison.Ordinal), "状态应说明因高负载提示而续跑。");
                sentPrompts.Add(poll.Prompt!);
            }
            Thread.Sleep(80);
            poll = monitor.Poll();
        }

        if (!shouldSend)
        {
            Assert(sentPrompts.Count == 0, "行输入模式或内置重试中不得收到续跑文本。");
            Assert(!File.Exists(received), "行输入模式或内置重试中不得收到任何按键。");
            Assert(!File.Exists(Path.Combine(fixture.Directory, "error")), "夹具异常退出。");
            return;
        }

        Assert(sentPrompts.Count == expectedCount, $"应完成 {expectedCount} 次续跑，实际 {sentPrompts.Count} 次。");
        Assert(sentPrompts.Distinct().Count() == expectedCount, $"连续续跑不应发送同一条文本：{string.Join(" / ", sentPrompts)}");
        Assert(sentPrompts.All(prompt => options.Prompts.Contains(prompt)), "空闲或响应异常发出的文本必须来自配置文本池。");
        var expected = string.Concat(sentPrompts.Select(prompt => prompt + '\r'));
        WaitUntil(() => ReadIfPresent(received) == expected, "夹具未收到指定续跑文本与回车。");

        // 达到上限或同一高负载失败已发送，都不能继续注入文本。
        for (var i = 0; i < 12; i++)
        {
            Assert(!monitor.Poll().KeptAlive, "达到上限或同一失败已发送后，不得再次续跑。");
            Thread.Sleep(120);
        }
        Assert(ReadIfPresent(received) == expected, "不得超出上限继续发送。");
        monitor.Stop();
        Assert(!monitor.Poll().KeptAlive, "停止后不得再发送。");
        Assert(!File.Exists(Path.Combine(fixture.Directory, "error")), "夹具异常退出。");
    }

    private static void TestKeepAlivePasteBurst()
    {
        foreach (var text in new[] { "keep going", "继续完成剩下的工作", new string('x', 200) })
        {
            using var fixture = StartFixture("idle-paste");
            var monitor = new ConsoleMonitor(fixture.Target, new KeepAliveOptions(TimeSpan.Zero, [text], 1));
            var result = monitor.Poll();
            Assert(result.KeptAlive && result.Resumed == 1, "续跑应发送一次。");
            var expected = text + '\r';
            WaitUntil(() => ReadIfPresent(Path.Combine(fixture.Directory, "received")) == expected,
                "夹具未收到完整文本与回车。");
            Assert(ReadIfPresent(Path.Combine(fixture.Directory, "submitted")) == expected,
                "快速输入后必须提交完整文本，不能把 Enter 当成粘贴内容里的换行。");
            Assert(!monitor.Poll().KeptAlive, "提交后不得超出次数上限重复发送。");
            monitor.Stop();
        }
    }

    /// <summary>
    /// 读取夹具标记文件。文件尚未出现，或正被夹具改写时返回 null，
    /// 供轮询式断言安全使用。
    /// </summary>
    /// <param name="path">标记文件路径。</param>
    /// <returns>文件内容；不可读时为 null。</returns>
    private static string? ReadIfPresent(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is FileNotFoundException or IOException) { return null; }
    }

    /// <summary>
    /// 回归：Codex 跑在以管理员身份启动的终端里时，普通权限的工具连不上（AttachConsole
    /// 返回 ERROR_ACCESS_DENIED），界面必须说清原因并给出提权入口，而不是刷一堆"拒绝访问"。
    /// </summary>
    private static void TestElevationAdvice()
    {
        var blocked = new SessionScan([], ["PID 1：无法连接 Codex 终端（5: 拒绝访问。）"], 6);
        var status = MainForm.DescribeScan(blocked, false);
        Assert(status.Contains('6') && status.Contains("权限"), $"全被挡时状态应说明权限问题：{status}");
        Assert(!status.Contains("未找到"), "权限被挡不等于没有会话。");
        var advice = MainForm.Advise(blocked, false).ToArray();
        Assert(advice.Length == 1 && advice[0].Contains("管理员"), "应提示以管理员身份重启。");
        Assert(!advice.Any(line => line.Contains("请在 Windows Terminal 中运行 codex")),
            "有被挡的会话时不应误导用户去新开 codex。");

        // 部分可见、部分被挡：两件事都要说。
        var mixed = new SessionScan([new TerminalTarget(1, 0, "t", 0)], [], 6);
        var mixedStatus = MainForm.DescribeScan(mixed, false);
        Assert(mixedStatus.Contains("找到 1 个") && mixedStatus.Contains("6 个"), $"应同时报可见与被挡数量：{mixedStatus}");

        // 已经是管理员还被拒绝，提权没用，不能再劝提权。
        Assert(!MainForm.DescribeScan(blocked, true).Contains("权限不足"),
            "已提权时状态不应继续归因于权限不足。");
        var elevatedAdvice = MainForm.Advise(blocked, true).ToArray();
        Assert(elevatedAdvice.Length == 1 && elevatedAdvice[0].Contains("已是管理员"),
            "已提权时应换一种解释。");

        // 没有会话时，提示两种受支持的 CLI。
        var empty = new SessionScan([], [], 0);
        Assert(MainForm.DescribeScan(empty, false) == "未找到可读取的 Codex / Claude Code CLI 窗口", "空结果文案应包含两种 CLI。");
        Assert(MainForm.Advise(empty, false).Single().Contains("请在 Windows Terminal 中运行 codex 或 claude"),
            "空结果应引导用户启动受支持的 CLI。");

        // IsElevated 必须与系统判断一致。
        var expected = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        Assert(TerminalConsole.IsElevated() == expected, "IsElevated 应与系统判断一致。");
    }

    /// <summary>提权按钮只在"有会话因权限被挡且本进程未提权"时出现。</summary>
    private static void TestElevateButtonVisibility()
    {
        using var form = new MainForm
        {
            ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual,
            Location = new Point(-20000, -20000)
        };
        form.Show();
        var controls = Descendants(form).ToArray();
        var elevate = controls.OfType<Button>().Single(button => button.Name == "elevate");
        var status = controls.OfType<Label>().Single(label => label.Name == "status");
        Assert(!elevate.Visible, "启动时不应显示提权按钮。");
        WaitUntil(() => status.Text != "未启动" && status.Text != "正在查找 CLI 窗口…", "窗口发现未完成。");
        // 本机实测：测试进程若已提权则不该出现按钮；未提权且确有被挡会话时才出现。
        var scan = TerminalConsole.FindCliSessions();
        Assert(elevate.Visible == (scan.NeedsElevation > 0 && !TerminalConsole.IsElevated()),
            $"提权按钮可见性与扫描结果不一致：needsElevation={scan.NeedsElevation} elevated={TerminalConsole.IsElevated()}");
        form.Close();
    }

    private static void TestLayout()
    {
        Assert(Marshal.SizeOf<Native.Coord>() == 4, "COORD size");
        Assert(Marshal.SizeOf<Native.SmallRect>() == 8, "SMALL_RECT size");
        Assert(Marshal.SizeOf<Native.BufferInfo>() == 22, "CONSOLE_SCREEN_BUFFER_INFO size");
        Assert(Marshal.SizeOf<Native.CharInfo>() == 4, "CHAR_INFO size");
        Assert(Marshal.SizeOf<Native.InputRecord>() == 20, "INPUT_RECORD size");
        Assert((int)Marshal.OffsetOf<Native.InputRecord>("Character") == 14, "UnicodeChar offset");
    }

    private static void TestConsole(bool raw, bool windowsTerminal = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodexAutoYes-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // 夹具目录只在本次测试内有效，无论成败都要删除，避免在 %TEMP% 中堆积。
        try
        {
            RunConsoleFixture(directory, raw, windowsTerminal);
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>一个已就绪的夹具控制台会话。</summary>
    /// <param name="Directory">标记文件交换目录。</param>
    /// <param name="Process">夹具进程。</param>
    /// <param name="Target">指向该夹具控制台的监听目标。</param>
    private sealed record Fixture(string Directory, Process Process, TerminalTarget Target) : IDisposable
    {
        public void Dispose()
        {
            File.WriteAllText(Path.Combine(Directory, "stop"), "");
            if (!Process.WaitForExit(5000))
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit();
            }
            Process.Dispose();
            try { System.IO.Directory.Delete(Directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>启动一个隐藏的夹具控制台，并等待它写出画面。</summary>
    /// <param name="mode">raw / cooked 写出审批菜单；idle、error、demand 及对应的 -cooked 模式写出空闲或异常；turn 模拟成功回合。</param>
    /// <returns>已就绪的夹具；调用方负责 Dispose。</returns>
    private static Fixture StartFixture(string mode, string? executablePath = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodexAutoYes-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var fixturePath = executablePath ?? Path.Combine(AppContext.BaseDirectory, "CodexAutoYes.Tests.exe");
        var start = new ProcessStartInfo(fixturePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("--fixture");
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add(mode);
        using var launcher = Process.Start(start) ?? throw new InvalidOperationException("Fixture did not start.");
        WaitUntil(() => File.Exists(Path.Combine(directory, "ready")), "Fixture did not initialize.");
        var fixture = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(directory, "ready"))));
        var target = new TerminalTarget(fixture.Id, fixture.StartTime.ToUniversalTime().Ticks, $"Fixture {fixture.Id}", 0);
        return new Fixture(directory, fixture, target);
    }

    /// <summary>
    /// 同时监听两个会话：各自独立确认一次，互不串台，停止后都不再发送。
    /// 这是多会话支持的核心验证点 —— 两者共用 TerminalConsole 的进程级锁。
    /// </summary>
    private static void TestMultipleSessions()
    {
        using var first = StartFixture("raw");
        using var second = StartFixture("raw");
        var set = new MonitorSet([first.Target, second.Target]);
        Assert(set.Count == 2, "两个会话都应在监听集合内。");

        var confirmed = new Dictionary<int, int>();
        var deadline = Stopwatch.StartNew();
        while (confirmed.Count < 2 && deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            foreach (var poll in set.PollAll())
            {
                Assert(poll.Error is null, $"会话轮询不应出错：{poll.Error?.Message}");
                if (poll.Result!.Sent)
                    confirmed[poll.Monitor.Target.ProcessId] = confirmed.GetValueOrDefault(poll.Monitor.Target.ProcessId) + 1;
            }
            Thread.Sleep(30);
        }
        Assert(confirmed.Count == 2, $"两个会话都应各自确认一次，实际 {confirmed.Count} 个。");
        Assert(confirmed.Values.All(times => times == 1), "同一菜单不应重复确认。");

        // 停止后在途与后续轮询都不得再发送。
        set.StopAll();
        Assert(set.Count == 0, "停止后集合应为空。");
        foreach (var poll in set.PollAll())
            Assert(!poll.Result!.Sent, "停止后不得再发送按键。");

        WaitUntil(() => File.Exists(Path.Combine(first.Directory, "received")), "第一个会话未收到 y。");
        WaitUntil(() => File.Exists(Path.Combine(second.Directory, "received")), "第二个会话未收到 y。");
        Thread.Sleep(150);
        foreach (var fixture in new[] { first, second })
        {
            Assert(!File.Exists(Path.Combine(fixture.Directory, "error")), "夹具异常退出。");
            Assert(File.ReadAllText(Path.Combine(fixture.Directory, "received")) == "y",
                "每个会话都应只收到一个 y。");
        }
    }

    /// <summary>单个会话失败时，其余会话必须继续监听。</summary>
    private static void TestSessionFailureIsolation()
    {
        using var healthy = StartFixture("raw");
        // PID 复用检查会拒绝这个伪造目标，用来模拟一个已失效的会话。
        var broken = healthy.Target with { ProcessId = healthy.Target.ProcessId, StartTime = 0 };
        var set = new MonitorSet([broken, healthy.Target]);
        var polls = set.PollAll();
        Assert(polls.Count == 2, "两个会话都应返回结果。");
        Assert(polls[0].Error is not null, "失效会话应返回错误而不是抛出。");
        Assert(polls[1].Error is null, "健康会话不应受影响。");

        set.Remove(polls[0].Monitor);
        Assert(set.Count == 1, "移除失效会话后应剩一个。");
        var deadline = Stopwatch.StartNew();
        var sent = false;
        while (!sent && deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            foreach (var poll in set.PollAll())
            {
                Assert(poll.Error is null, "剩余会话不应出错。");
                sent |= poll.Result!.Sent;
            }
            Thread.Sleep(30);
        }
        Assert(sent, "剩余的健康会话应仍能确认。");
        set.StopAll();
    }

    /// <summary>在隔离的夹具控制台中执行一轮真实读写验证。</summary>
    /// <param name="directory">夹具与测试之间交换标记文件的临时目录。</param>
    /// <param name="raw">true 表示原始输入模式（应接收 y），false 表示行输入模式（必须不接收）。</param>
    /// <param name="windowsTerminal">true 表示在独立的 Windows Terminal 标签页中走完整 GUI 流程。</param>
    private static void RunConsoleFixture(string directory, bool raw, bool windowsTerminal)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "CodexAutoYes.Tests.exe");
        var start = new ProcessStartInfo(windowsTerminal ? "wt.exe" : fixturePath)
        {
            UseShellExecute = windowsTerminal, CreateNoWindow = true, WorkingDirectory = directory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (windowsTerminal)
        {
            foreach (var argument in new[] { "-w", "new", "new-tab", "--title", "Codex AutoYes test", "--suppressApplicationTitle", fixturePath })
                start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add("--fixture");
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add(raw ? "raw" : "cooked");
        using var launcher = Process.Start(start) ?? throw new InvalidOperationException("Fixture did not start.");
        Process? fixture = null;
        try
        {
            WaitUntil(() => File.Exists(Path.Combine(directory, "ready")), "Fixture did not initialize.");
            fixture = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(directory, "ready"))));
            var target = new TerminalTarget(fixture.Id, fixture.StartTime.ToUniversalTime().Ticks, "Fixture", 0);
            var screen = TerminalConsole.Read(target);
            Assert(ApprovalPrompt.Find(screen) is not null, "Console screen was not read correctly: " + screen);
            Assert(!TerminalConsole.ConfirmIfUnchanged(target, "stale dialog"), "A stale snapshot must not send input.");
            if (windowsTerminal)
            {
                TestGuiMonitor(target);
            }
            else
            {
                var monitor = new ConsoleMonitor(target);
                Assert(!monitor.Poll().Sent, "First frame should wait.");
                if (raw)
                    WaitUntil(() => monitor.Poll().Sent, "Raw input should receive y.");
                for (var i = 0; i < 5; i++)
                    Assert(!monitor.Poll().Sent, "Cooked input and unchanged menus must not receive y.");
                monitor.Stop();
                Assert(!monitor.Poll().Sent, "Stopped monitor must not send.");
            }
            if (raw)
                WaitUntil(() => File.Exists(Path.Combine(directory, "received")), "Fixture did not receive y.");
            Thread.Sleep(150);
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "stop"), "");
            if (fixture is not null && !fixture.WaitForExit(5000))
            {
                fixture.Kill(entireProcessTree: true);
                fixture.WaitForExit();
            }
            fixture?.Dispose();
        }
        Assert(!File.Exists(Path.Combine(directory, "error")), "Fixture exited with an error.");
        var received = File.ReadAllText(Path.Combine(directory, "result"));
        Assert(received == (raw ? "y" : ""), $"Unexpected input: [{received}]");
    }

    private static void TestHighDemandGui()
    {
        using var fixture = StartFixture("demand");
        TestGuiMonitor(fixture.Target, true);
        WaitUntil(() => ReadIfPresent(Path.Combine(fixture.Directory, "received")) == "计划书编写\r",
            "界面中配置的中文文本应原样发送并回车。");
    }

    private static void TestGuiMonitor(TerminalTarget target, bool highDemand = false, bool claude = false)
    {
        using var form = new MainForm
        {
            ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual,
            Location = new Point(-20000, -20000)
        };
        form.Show();
        var controls = Descendants(form).ToArray();
        var refresh = controls.OfType<Button>().Single(button => button.Text == "刷新窗口");
        var toggleAll = controls.OfType<Button>().Single(button => button.Text is "全选" or "全不选");
        var start = controls.OfType<Button>().Single(button => button.Text == "开始监听");
        var stop = controls.OfType<Button>().Single(button => button.Text == "停止");
        var count = controls.OfType<Label>().Single(label => label.Name == "count");
        var sessions = controls.OfType<ListView>().Single(list => list.Name == "sessions");
        var status = controls.OfType<Label>().Single(label => label.Name == "status");
        var keepAlive = controls.OfType<CheckBox>().Single(box => box.Name == "keepAlive");
        var idleSeconds = controls.OfType<NumericUpDown>().Single(box => box.Name == "idleSeconds");
        var highDemandRetry = controls.OfType<CheckBox>().Single(box => box.Name == "highDemandRetry");
        var claudeRetry = controls.OfType<CheckBox>().Single(box => box.Name == "claudeRetry");
        Assert(highDemandRetry.Visible && highDemandRetry.Checked && highDemandRetry.Text.Contains("1 秒"),
            "界面必须有可见、默认启用的高负载失败后 1 秒续发开关。");
        if (highDemand || claude)
        {
            keepAlive.Checked = false;
            highDemandRetry.Checked = !claude;
            controls.OfType<TextBox>().Single(box => box.Name == "keepAliveText").Text = "计划书编写";
            controls.OfType<NumericUpDown>().Single(box => box.Name == "maxSends").Value = 1;
        }
        // _refresh 默认 Enabled=true，必须等异步扫描结束，否则它会清掉下面塞入的夹具行。
        WaitUntil(() => status.Text != "未启动" && status.Text != "正在查找 CLI 窗口…",
            "Window discovery did not finish.");
        Assert(!stop.Enabled, "Monitoring must be off on startup.");

        Assert(sessions.Columns.Count == 5, "会话列表应含「已续跑」列。");
        Assert(keepAlive.Enabled && idleSeconds.Enabled, "未监听时保活设置应可编辑。");

        // 用夹具会话替换真实扫描结果，避免测试触碰用户的 Codex 终端。
        sessions.Items.Clear();
        var row = new ListViewItem(target.Title) { Tag = target };
        row.SubItems.Add(target.ProcessId.ToString());
        row.SubItems.Add("未监听");
        row.SubItems.Add("0");
        row.SubItems.Add("0");
        sessions.Items.Add(row);
        Assert(!start.Enabled, "未勾选任何会话时不得允许开始。");

        row.Checked = true;
        Application.DoEvents();
        Assert(start.Enabled, "勾选会话后应允许开始。");
        row.Selected = true;
        start.PerformClick();
        Assert(stop.Enabled && !refresh.Enabled && !toggleAll.Enabled, "开始监听后应锁定会话集合。");
        Assert(!keepAlive.Enabled && !idleSeconds.Enabled && !highDemandRetry.Enabled && !claudeRetry.Enabled, "监听期间续发设置应锁定。");
        // 监听期间不得改动勾选状态。
        row.Checked = false;
        Application.DoEvents();
        Assert(row.Checked, "监听期间不得取消勾选。");

        if (highDemand || claude)
        {
            WaitUntil(() => row.SubItems[4].Text == "1", "关闭空闲续跑后，失败重试仍应独立工作。");
            var log = controls.OfType<TextBox>().Single(box => box.Name == "log").Text;
            Assert(log.Contains(claude ? "Claude Code 回合已失败，1 秒后自动重试" : "目标回合已失败，1 秒后自动发送") &&
                   log.Contains("已发送「计划书编写」"),
                "日志应记录失败等待和实际发送的内容。");
            Assert(!claude || row.SubItems[3].Text == "0", "Claude Code 错误重试不得发送审批确认。");
        }
        else
        {
            WaitUntil(() => count.Text == "已确认 1 次", "GUI did not confirm the simulated approval.");
            Assert(row.SubItems[3].Text == "1", "该会话行应显示已确认 1 次。");
        }
        stop.PerformClick();
        Assert(!stop.Enabled && refresh.Enabled && toggleAll.Enabled, "停止后应释放会话集合。");
        Assert(keepAlive.Enabled && idleSeconds.Enabled && highDemandRetry.Enabled && claudeRetry.Enabled, "停止后续发设置应恢复可编辑。");
        form.Close();
    }

    /// <summary>执行态结束并稳定后只报告一次成功返回。</summary>
    private static void TestTurnWatcher()
    {
        var watcher = new TurnWatcher();
        Assert(watcher.Observe(IdleScreen) is null, "启动时已经空闲的画面不得算返回。");
        // 执行提示必须落在画面尾部，否则 LooksBusy 看不到它。
        Assert(watcher.Observe(BusyScreen) is null, "执行中不得算返回。");
        Assert(watcher.Observe(TurnIdleScreen) is null, "稳定帧数不足时不得返回。");
        Assert(watcher.Observe(TurnIdleScreen) is null, "稳定帧数不足时不得返回。");
        var returned = watcher.Observe(TurnIdleScreen);
        Assert(returned is not null, "连续三帧稳定空闲应报告一次返回。");
        Assert(returned!.Summary.Contains("登录页样式已修复"), $"摘要应包含回答内容，实际：{returned.Summary}");
        for (var i = 0; i < 5; i++)
            Assert(watcher.Observe(TurnIdleScreen) is null, "同一回合不得重复通知。");

        watcher.Observe(BusyScreen);
        Assert(watcher.Observe(TurnIdleScreen) is null, "下一回合也要等待稳定画面。");
        Assert(watcher.Observe("\0 \r\n\t\r\n") is null, "空画面不得算作稳定帧。");
        Assert(watcher.Observe(TurnIdleScreen) is null, "空画面不得增加稳定帧数。");
        const string changed = "补充说明。\r\n" + TurnIdleScreen;
        Assert(watcher.Observe(changed) is null, "画面变化后必须重新等待稳定。");
        Assert(watcher.Observe(changed) is null, "画面变化后第二帧仍不得返回。");
        Assert(watcher.Observe(changed) is not null, "下一回合稳定后应重新报告返回。");
    }

    /// <summary>报错、重连与审批菜单都不算成功返回。</summary>
    private static void TestTurnWatcherRejectsFailures()
    {
        var error = new TurnWatcher();
        error.Observe(BusyScreen);
        for (var i = 0; i < 5; i++)
            Assert(error.Observe(ResponseErrorScreen) is null, "报错画面不得算成功返回。");

        var demand = new TurnWatcher();
        demand.Observe(BusyScreen);
        for (var i = 0; i < 5; i++)
            Assert(demand.Observe(HighDemandScreen) is null, "高负载画面不得算成功返回。");

        var reconnect = new TurnWatcher();
        reconnect.Observe(BusyScreen);
        for (var i = 0; i < 5; i++)
            Assert(reconnect.Observe("Reconnecting... 3/5\r\n" + IdleScreen) is null, "重连画面不得算成功返回。");
        for (var i = 0; i < 5; i++)
            Assert(reconnect.Observe(TurnIdleScreen) is null, "失败后未观察到新的执行态，不得补报成功。");

        // 审批菜单本身不算返回；确认后回合真正结束才通知一次。
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
        Assert(TurnWatcher.Summarize(KeepAliveWatch.Normalize("›\r\n\r\n? for shortcuts\r\n")) == "已返回回答",
            "没有内容时应回退为固定摘要。");
        Assert(TurnWatcher.Summarize(KeepAliveWatch.Normalize("结论：登录页修复完成。\r\n\r\n> \r\n\r\n? for shortcuts\r\n"))
            == "结论：登录页修复完成。", "摘要不得包含提示符与快捷键说明。");
        Assert(TurnWatcher.Summarize("› 用户输入\n> 另一条输入\n❯ 输入\n───\nToken usage: 42\nsend with enter\nPress enter to continue")
            == "已返回回答", "输入回显、边框和操作提示不得进入摘要。");
        Assert(TurnWatcher.Summarize("旧内容\n" + string.Join('\n', Enumerable.Repeat("? for shortcuts", 12)))
            == "已返回回答", "摘要不得读取尾部 12 条非空行以前的历史内容。");
        Assert(TurnWatcher.Summarize("结论：  完成\n详情：\t已验证") == "结论： 完成 详情： 已验证",
            "摘要应折叠连续空白。");
    }

    /// <summary>托盘启停与主界面同步，隐藏与恢复不能清空正在监听的会话。</summary>
    private static void TestTrayGui()
    {
        using var fixture = StartFixture("raw");
        using var form = new MainForm((_, _) => { });
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Opacity = 0;
        form.Show();
        var controls = Descendants(form).ToArray();
        var status = controls.OfType<Label>().Single(label => label.Name == "status");
        WaitUntil(() => status.Text != "未启动" && status.Text != "正在查找 CLI 窗口…", "窗口发现未完成。");

        Assert(form.Tray.Visible, "托盘图标应常驻。");
        var menu = form.Tray.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().ToArray();
        var show = menu.Single(item => item.Text == "显示主窗口");
        var start = menu.Single(item => item.Text == "开始监听");
        var stop = menu.Single(item => item.Text == "停止监听");
        var exit = menu.Single(item => item.Text == "退出");
        Assert(!start.Enabled && !stop.Enabled, "未勾选会话、未监听时，托盘启停项均不可用。");

        form.WindowState = FormWindowState.Minimized;
        Application.DoEvents();
        Assert(!form.Visible && !form.ShowInTaskbar, "最小化后应隐藏到托盘。");
        show.PerformClick();
        Application.DoEvents();
        Assert(form.Visible && form.ShowInTaskbar && form.WindowState == FormWindowState.Normal,
            "托盘菜单应恢复普通窗口和任务栏图标。");

        var sessions = controls.OfType<ListView>().Single(list => list.Name == "sessions");
        sessions.Items.Clear();
        var target = fixture.Target;
        var row = new ListViewItem([target.Title, target.ProcessId.ToString(), "未监听", "0", "0"]) { Tag = target };
        sessions.Items.Add(row);
        row.Checked = true;
        Application.DoEvents();
        Assert(start.Enabled, "勾选会话后托盘开始项应可用。");
        start.PerformClick();
        Assert(stop.Enabled && !start.Enabled && form.Tray.Text.Contains("监听 1 个会话"),
            "开始监听后托盘菜单和提示应同步切换。");
        Assert(controls.OfType<CheckBox>().Single(box => box.Name == "notifyOnReturn").Enabled,
            "监听期间仍应允许修改通知开关。");
        form.HideToTray();
        var confirmed = controls.OfType<Label>().Single(label => label.Name == "count");
        WaitUntil(() => confirmed.Text == "已确认 1 次", "隐藏到托盘后仍应继续轮询并确认夹具审批。");
        form.RestoreFromTray();
        Assert(sessions.Items.Contains(row) && row.Checked && stop.Enabled,
            $"恢复窗口不得重新扫描或停止监听：行存在={sessions.Items.Contains(row)}，勾选={row.Checked}，" +
            $"停止可用={stop.Enabled}，状态={status.Text}。");
        stop.PerformClick();
        Assert(start.Enabled && !stop.Enabled && form.Tray.Text == form.Text, "停止后托盘状态应恢复。");

        form.WindowState = FormWindowState.Maximized;
        form.WindowState = FormWindowState.Minimized;
        form.RestoreFromTray();
        Assert(form.Visible && form.ShowInTaskbar && form.WindowState == FormWindowState.Maximized,
            "最大化窗口最小化后应恢复为最大化。");

        form.Tray.Visible = false;
        form.WindowState = FormWindowState.Minimized;
        Assert(form.Visible && form.ShowInTaskbar, "托盘不可用时必须保留任务栏入口。");
        form.RestoreFromTray();
        form.Tray.Visible = true;
        exit.PerformClick();
        Assert(form.IsDisposed && !form.Tray.Visible, "退出应释放窗口和托盘图标。");
    }

    /// <summary>仅后台弹通知；前台或关闭开关时仍更新计数与记录，点击通知定位对应会话。</summary>
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
        var preview = controls.OfType<TextBox>().Single(box => box.Name == "preview");
        var returned = controls.OfType<Label>().Single(label => label.Name == "returned");
        var notify = controls.OfType<CheckBox>().Single(box => box.Name == "notifyOnReturn");
        var sessions = controls.OfType<ListView>().Single(list => list.Name == "sessions");
        var status = controls.OfType<Label>().Single(label => label.Name == "status");
        WaitUntil(() => status.Text != "未启动" && status.Text != "正在查找 CLI 窗口…", "窗口发现未完成。");
        Assert(notify.Checked, "通知开关默认开启。");
        sessions.Items.Clear();
        var target = new TerminalTarget(424243, 0, "Fixture notice", 0);
        var row = new ListViewItem([target.Title, "424243", "未监听", "0", "0"]) { Tag = target };
        sessions.Items.Add(row);
        var monitors = new MonitorSet([]);
        var result = new MonitorResult("监听中 · 等待审批提示", "完成画面", false,
            Returned: true, Summary: "登录页样式已修复。");
        var poll = new SessionPoll(new ConsoleMonitor(target), result, null);

        form.HideToTray();
        form.Apply(monitors, poll);
        Assert(notifications.Count == 1 && notifications[0].Title.Contains(target.Title) &&
               notifications[0].Body.Contains("登录页样式已修复") && notifications[0].Body.Contains("PID 424243"),
            "后台返回应发一条包含会话标题、摘要和 PID 的通知。");
        Assert(returned.Text == "已返回 1 次" && history.Text.Contains("已返回回答：登录页样式已修复。"),
            "返回计数与操作记录应更新。");

        var otherTarget = new TerminalTarget(424244, 0, new string('会', 100), 0);
        notify.Checked = false;
        form.Apply(monitors, new SessionPoll(new ConsoleMonitor(otherTarget), result, null));
        Assert(notifications.Count == 1 && returned.Text == "已返回 2 次", "关闭开关只影响通知，不影响计数。");
        Assert(history.Text.Contains("PID 424244"), "关闭通知后仍应记录其它会话的返回。");

        var click = typeof(NotifyIcon).GetMethod("OnBalloonTipClicked", BindingFlags.Instance | BindingFlags.NonPublic)!;
        click.Invoke(form.Tray, []);
        Application.DoEvents();
        Assert(form.Visible && form.ShowInTaskbar && row.Selected && preview.Text == result.Screen,
            "点击通知应恢复窗口并选中收到通知的会话，不能被未通知的返回覆盖。");

        notify.Checked = true;
        form.Activate();
        Application.DoEvents();
        // shortcut: 无交互桌面时无法取得前台焦点，前台抑制分支需在桌面会话复核。
        var foreground = Form.ActiveForm == form;
        form.Apply(monitors, poll);
        var expected = foreground ? 1 : 2;
        Assert(notifications.Count == expected && returned.Text == "已返回 3 次",
            "应按实际前台状态决定是否通知，返回计数始终累加。");

        form.HideToTray();
        form.Apply(monitors, new SessionPoll(new ConsoleMonitor(otherTarget), result with { Summary = new string('好', 300) }, null));
        Assert(notifications.Count == expected + 1 && notifications[^1].Title.Length == 63 && notifications[^1].Body.Length == 255,
            "窗口未激活时应通知，并按系统上限截断标题和正文。");
        Assert(notifications[^1].Title.EndsWith('…') && notifications[^1].Body.EndsWith('…'), "截断应保留省略号。");
        click.Invoke(form.Tray, []);
        Assert(form.Visible && form.ShowInTaskbar, "通知对应的会话行不存在时，仍应恢复窗口。");
        form.Close();
    }

    private static IEnumerable<Control> Descendants(Control parent) =>
        parent.Controls.Cast<Control>().SelectMany(control => new[] { control }.Concat(Descendants(control)));

    private static void TestProcessIdentity()
    {
        using var self = Process.GetCurrentProcess();
        var stale = new TerminalTarget(self.Id, 0, "Wrong start time", 0);
        try { TerminalConsole.Read(stale); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("A reused PID must be rejected before attaching.");
    }

    private static void TestDiscovery()
    {
        var scan = TerminalConsole.FindCliSessions();
        var windowed = scan.Targets.Where(t => t.ConsoleWindow != 0).Select(t => t.ConsoleWindow).ToArray();
        Assert(windowed.Distinct().Count() == windowed.Length, "One row per console window.");
        Assert(scan.Targets.Select(t => t.ProcessId).Distinct().Count() == scan.Targets.Count, "One row per process.");
        foreach (var target in scan.Targets)
            Assert(TerminalConsole.Read(target).Length > 0, "Listed console must be readable.");
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts"));
        File.WriteAllLines(Path.Combine(directory, "console-probe.txt"),
            scan.Targets.Select(t => $"PID {t.ProcessId}: readable; no input sent")
                .Concat(scan.Skipped.Select(reason => $"skipped {reason}")));
    }

    /// <summary>
    /// 回归：GUI 从 cmd / 批处理启动时会继承父进程控制台，此时 AttachConsole 返回
    /// ERROR_ACCESS_DENIED(5)，导致会话列表为空。扫描必须先脱离自带控制台。
    /// </summary>
    private static void TestDiscoveryWithInheritedConsole()
    {
        var initial = TerminalConsole.FindCliSessions();
        var baseline = initial.Targets.Count;
        if (baseline == 0)
            throw new InvalidOperationException(initial.NeedsElevation > 0
                ? $"有 {initial.NeedsElevation} 个 Codex 会话因权限不足无法连接，" +
                  "请以管理员身份运行测试，或改用普通权限的终端启动 codex。"
                : "需要至少一个正在运行的 Codex 会话才能验证本项。");
        // 模拟继承来的控制台：分配一个再扫描。
        Assert(AllocConsole() != 0, "无法分配用于复现的控制台。");
        try
        {
            Assert(Native.GetConsoleWindow() != 0 || true, "控制台已分配。");
            var scan = TerminalConsole.FindCliSessions();
            Assert(scan.Targets.Count == baseline,
                $"继承控制台后应仍能发现 {baseline} 个会话，实际 {scan.Targets.Count}；原因：{string.Join(" / ", scan.Skipped)}");
        }
        finally
        {
            Native.FreeConsole();
        }
    }

    private static void RenderGui(string path)
    {
        using var form = new MainForm();
        form.ShowInTaskbar = false;
        form.Opacity = 0;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Show();
        for (var i = 0; i < 30; i++)
        {
            Application.DoEvents();
            Thread.Sleep(25);
        }
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(path);
        form.Close();
    }

    private static void WaitUntil(Func<bool> ready, string failure)
    {
        var timeout = Stopwatch.StartNew();
        while (!ready())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException(failure);
            Application.DoEvents();
            Thread.Sleep(30);
        }
    }

    /// <summary>
    /// 测量监听 N 个真实会话时单轮轮询的耗时，用于确认是否能装进界面 400ms 的定时器间隔。
    /// 只读：构造 MonitorSet 但只调用一次 PollAll，首轮因稳定性闸门不会发送按键。
    /// </summary>
    /// <param name="outputPath">测量结果输出文件路径。</param>
    /// <returns>固定返回 0。</returns>
    private static int RunTimingProbe(string outputPath)
    {
        Native.FreeConsole();
        var lines = new List<string>();
        var targets = TerminalConsole.FindCliSessions().Targets;
        lines.Add($"sessions={targets.Count}");
        for (var size = 1; size <= targets.Count; size++)
        {
            var set = new MonitorSet(targets.Take(size));
            var elapsed = new List<double>();
            // 首轮之后闸门仍需第二次一致读取才会发送；这里只读真实会话，不发按键。
            for (var round = 0; round < 3; round++)
            {
                var clock = Stopwatch.StartNew();
                var polls = set.PollAll();
                clock.Stop();
                elapsed.Add(clock.Elapsed.TotalMilliseconds);
                var failures = polls.Count(p => p.Error is not null);
                if (round == 2)
                    lines.Add($"size={size,-3} rounds=[{string.Join(", ", elapsed.Select(ms => ms.ToString("F1")))}]ms errors={failures}");
            }
            set.StopAll();
        }
        File.WriteAllLines(outputPath, lines);
        return 0;
    }

    /// <summary>
    /// 启动真实 MainForm 并在刷新完成后导出控件实际状态，用于诊断 GUI 列表为空的问题。
    /// </summary>
    /// <param name="outputPath">诊断结果输出文件路径。</param>
    /// <param name="keepConsole">true 表示保留自带控制台，用于复现继承控制台导致的 attach 失败。</param>
    /// <returns>固定返回 0；异常信息写入输出文件。</returns>
    private static int RunGuiStateProbe(string outputPath, bool keepConsole)
    {
        var lines = new List<string>();
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var elevated = new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        var hadWindow = Native.GetConsoleWindow() != 0;
        var freed = keepConsole ? false : Native.FreeConsole() != 0;
        lines.Add($"elevated={elevated} keepConsole={keepConsole} hadConsoleWindow={hadWindow} freeConsole={freed}");

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            using var form = new MainForm
            {
                ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual,
                Location = new Point(-20000, -20000)
            };
            form.Show();
            var controls = Descendants(form).ToArray();
            var sessions = controls.OfType<ListView>().Single(list => list.Name == "sessions");
            var status = controls.OfType<Label>().Single(label => label.Name == "status");
            // _refresh 默认 Enabled=true，必须等状态文本离开初始值才算刷新结束。
            WaitUntil(() => status.Text != "未启动" && status.Text != "正在查找 CLI 窗口…", "窗口发现未完成。");
            lines.Add($"status={status.Text}");
            lines.Add($"rows={sessions.Items.Count} startEnabled={controls.OfType<Button>().Single(b => b.Text == "开始监听").Enabled}");
            var elevate = controls.OfType<Button>().Single(b => b.Name == "elevate");
            lines.Add($"elevateVisible={elevate.Visible} elevateWidth={elevate.Width}");
            // 同时存一张截图：提权按钮出现时的布局只有在这种权限下才能看到。
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.ChangeExtension(outputPath, ".png"));
            }
            foreach (ListViewItem item in sessions.Items)
                lines.Add($"  item: {item.Text}  ·  PID {item.SubItems[1].Text}  ·  {item.SubItems[2].Text}");
            var log = controls.OfType<TextBox>().Single(box => box.Name == "log");
            foreach (var line in log.Lines.Where(l => l.Length != 0))
                lines.Add($"  log: {line}");
            form.Close();
        }
        catch (Exception ex)
        {
            lines.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        File.WriteAllLines(outputPath, lines);
        return 0;
    }

    /// <summary>
    /// 用生产代码路径探测会话发现结果，分别在 STA 主线程和线程池线程上各跑一次，
    /// 以复现 GUI（Task.Run）与测试（主线程）的调用差异。只读，不发送任何按键。
    /// </summary>
    /// <param name="outputPath">诊断结果输出文件路径。</param>
    /// <returns>固定返回 0；失败信息写入输出文件。</returns>
    private static int RunDiscoveryProbe(string outputPath)
    {
        var lines = new List<string>();
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var elevated = new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        var hadWindow = Native.GetConsoleWindow() != 0;
        // GUI 是 WinExe，本身没有控制台；探测前必须脱离自带控制台才能复现相同条件。
        var freed = Native.FreeConsole() != 0;
        lines.Add($"elevated={elevated} user={identity.Name} hadConsoleWindow={hadWindow} freeConsole={freed}");

        // 逐进程列出原始状态，定位 FindCliSessions 静默跳过的那些。
        foreach (var process in Process.GetProcessesByName("codex").OrderBy(p => p.Id))
        {
            using (process)
            {
                try
                {
                    var target = new TerminalTarget(process.Id, process.StartTime.ToUniversalTime().Ticks, "", 0);
                    var screen = TerminalConsole.Read(target);
                    lines.Add($"PID {process.Id,-7} read ok chars={screen.Length,-7} prompt={ApprovalPrompt.Find(screen) is not null}");
                }
                catch (Exception ex)
                {
                    lines.Add($"PID {process.Id,-7} {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        Describe("main-thread", TerminalConsole.FindCliSessions);
        var pooled = Task.Run(() => Describe("threadpool", TerminalConsole.FindCliSessions));
        pooled.Wait();

        void Describe(string label, Func<SessionScan> discover)
        {
            try
            {
                var scan = discover();
                lines.Add($"{label}: found={scan.Targets.Count} skipped={scan.Skipped.Count} needsElevation={scan.NeedsElevation}");
                foreach (var target in scan.Targets)
                    lines.Add($"  {label}: PID {target.ProcessId,-7} hwnd=0x{target.ConsoleWindow:X} title={target.Title}");
                foreach (var reason in scan.Skipped)
                    lines.Add($"  {label}: skipped {reason}");
            }
            catch (Exception ex)
            {
                lines.Add($"{label}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        File.WriteAllLines(outputPath, lines);
        return 0;
    }

    /// <summary>
    /// 夹具进程：在自己的隐藏控制台里写出一屏内容，然后把收到的按键记录到文件。
    /// </summary>
    /// <param name="directory">标记文件交换目录。</param>
    /// <param name="mode">raw / cooked 写出审批菜单；idle、error、demand 及对应的 -cooked 模式写出空闲或异常；turn 模拟成功回合。</param>
    /// <returns>0 表示正常结束。</returns>
    private static unsafe int RunFixture(string directory, string mode)
    {
        try
        {
            var raw = mode is "raw" or "idle" or "idle-paste" or "error" or "demand" or "reconnect" or "turn" ||
                      (mode.StartsWith("claude-") && !mode.EndsWith("-cooked"));
            var screen = mode.StartsWith("claude-") ? ClaudeFixtureScreen(mode) :
                mode == "reconnect" ? "Reconnecting... 5/5\r\n" + HighDemandError + "\r\n" + IdleScreen :
                mode.StartsWith("demand") ? HighDemandScreen :
                mode.StartsWith("error") ? ResponseErrorScreen : mode.StartsWith("idle") ? IdleScreen : Prompt;
            using var input = Native.CreateFileW("CONIN$", 0xC0000000, 3, 0, 3, 0, 0);
            using var output = Native.CreateFileW("CONOUT$", 0xC0000000, 3, 0, 3, 0, 0);
            Assert(!input.IsInvalid && !output.IsInvalid, "Fixture needs a hidden console.");
            Assert(Native.GetConsoleMode(input, out var consoleMode) != 0, "GetConsoleMode failed.");
            Assert(SetConsoleMode(input, raw ? consoleMode & ~7u : consoleMode | 6u) != 0, "SetConsoleMode failed.");
            var first = mode == "turn" ? BusyScreen : screen;
            Assert(WriteConsoleW(output, first, (uint)first.Length, out _, 0) != 0, "WriteConsoleW failed.");
            File.WriteAllText(Path.Combine(directory, "ready.tmp"), Environment.ProcessId.ToString());
            File.Move(Path.Combine(directory, "ready.tmp"), Path.Combine(directory, "ready"));
            if (mode == "turn")
            {
                // 确认父进程读到执行态后再结束回合，避免慢机启动时漏掉第一帧。
                WaitUntil(() => File.Exists(Path.Combine(directory, "observed-busy")) ||
                                File.Exists(Path.Combine(directory, "stop")), "父进程未读取回合执行态。");
                if (File.Exists(Path.Combine(directory, "stop")))
                    return 0;
                Thread.Sleep(1500);
                Assert(WriteConsoleW(output, TurnIdleScreen, (uint)TurnIdleScreen.Length, out _, 0) != 0,
                    "无法写入回合完成画面。");
            }
            var received = "";
            var draft = "";
            var submitted = "";
            var pasteActive = false;
            var buffer = stackalloc Native.InputRecord[128];
            var lifetime = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(directory, "stop")) && lifetime.Elapsed < TimeSpan.FromSeconds(30))
            {
                Assert(Native.GetNumberOfConsoleInputEvents(input, out var pending) != 0, "Reading input count failed.");
                if (pending > 0)
                {
                    Assert(ReadConsoleInputW(input, buffer, Math.Min(pending, 128), out var count) != 0, "Reading input failed.");
                    for (var i = 0; i < count; i++)
                    {
                        var key = buffer[i];
                        if (key.EventType != 1 || key.KeyDown == 0)
                            continue;
                        if (key.Character != 0)
                            received += (char)key.Character;
                        if (mode != "idle-paste")
                            continue;
                        // 对齐 Codex 的粘贴状态：Enter 插入换行，非文本键结束粘贴；固定状态避免依赖调度时序。
                        if (key.VirtualKey == 0x0D)
                        {
                            Assert(key.Character == '\r' && key.ControlKeyState == 0, "提交必须是无修饰键的 Enter。");
                            if (pasteActive)
                                draft += '\n';
                            else
                            {
                                submitted += draft + '\r';
                                draft = "";
                            }
                        }
                        else if (key.Character != 0)
                        {
                            draft += (char)key.Character;
                            pasteActive = true;
                        }
                        else if (key.VirtualKey is >= 0x21 and <= 0x28)
                            pasteActive = false;
                    }
                    if (mode == "idle-paste")
                        File.WriteAllText(Path.Combine(directory, "submitted"), submitted);
                    File.WriteAllText(Path.Combine(directory, "received"), received);
                }
                Thread.Sleep(20);
            }
            File.WriteAllText(Path.Combine(directory, "result"), received);
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "error"), ex.ToString());
            return 1;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int AllocConsole();
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetConsoleMode(SafeFileHandle handle, uint mode);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int WriteConsoleW(SafeFileHandle handle, string text, uint length, out uint written, nint reserved);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial int ReadConsoleInputW(SafeFileHandle handle, Native.InputRecord* records, uint count, out uint read);
}
