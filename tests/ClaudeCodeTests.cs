using System.Reflection;
using CodexAutoYes;

namespace CodexAutoYes.Tests;

internal static partial class Program
{
    private const string ClaudeIdleScreen = "----------------------------------------\r\n" +
                                            "\u276f \r\n----------------------------------------\r\n? for shortcuts\r\n";
    private const string ClaudeErrorScreen = "\u23bf API Error: 503 Service unavailable\r\n" + ClaudeIdleScreen;
    private const string ClaudeRetryScreen = "API error: 503\r\nRetrying in 2s \u00b7 attempt 10/10\r\n" + ClaudeIdleScreen;
    private const string ClaudeBusyScreen = "* Thinking... (esc to interrupt)\r\n";

    private static void TestClaudeErrors()
    {
        var options = new KeepAliveOptions(TimeSpan.Zero, ["continue"], 2);
        foreach (var error in new[]
                 {
                     "API Error: 408 Request timeout", "API Error: 429 Too many requests",
                     "API Error: 500 Internal server error", "API Error: 502 Bad gateway",
                     "API Error: 503 Service unavailable", "API Error: 504 Gateway timeout",
                     "API Error: 529 {\"error\":{\"type\":\"overloaded_error\"}}",
                     "API Error: Repeated 529 Overloaded errors", "API Error: Connection error.",
                     "API Error: Request timed out.", "API Error: No response from API",
                     "API Error: Stream idle timeout - no chunks received", "Request timed out",
                     "Unable to connect to API. Check your internet connection",
                     "Connection dropped (ECONNRESET)", "Server error mid-response. The response above may be incomplete.",
                     "\u23bf API Error:\r\n  529 Overloaded", "\u23bf API Error: Request timed\r\n  out."
                 })
        {
            var watch = new KeepAliveWatch(TerminalKind.ClaudeCode);
            var screen = error + "\r\n" + ClaudeIdleScreen;
            Assert(watch.Observe(screen, options) == KeepAliveState.RetryReady, $"Transient error must retry: {error}");
            Assert(watch.Signature == KeepAliveWatch.Normalize(screen), "Keep the exact signature for send validation.");
            Assert(KeepAliveWatch.FooterHasProblem(watch.Signature!, TerminalKind.ClaudeCode), "An error is not a successful answer.");
        }

        foreach (var screen in new[]
                 {
                     ClaudeIdleScreen,
                     "The documentation explains API Error: 503.\r\n" + ClaudeIdleScreen,
                     "\u276f Explain API Error: 503\r\n" + ClaudeIdleScreen,
                     "API Error: 400 Invalid request\r\n" + ClaudeIdleScreen,
                     "API Error: 401 Invalid API key\r\n" + ClaudeIdleScreen,
                     "API Error: 403 Forbidden\r\n" + ClaudeIdleScreen,
                     "API Error: 404 Model not found\r\n" + ClaudeIdleScreen,
                     "API Error: 5000 Not an HTTP status\r\n" + ClaudeIdleScreen,
                     "Invalid API key - Please run /login\r\n" + ClaudeIdleScreen,
                     "Credit balance too low\r\n" + ClaudeIdleScreen,
                     "You've hit your limit - resets tomorrow\r\n" + ClaudeIdleScreen,
                     "API Error: 503\r\nAPI Error: 401 Unauthorized\r\n" + ClaudeIdleScreen,
                     "API Error: 503\r\n\u23fa Finished successfully.\r\n" + ClaudeIdleScreen,
                     "API Error: 503\r\n\u276f A new user message\r\n" + ClaudeIdleScreen,
                     "API Error: 503\r\n" + string.Concat(Enumerable.Repeat("old output\r\n", 13)) + ClaudeIdleScreen,
                     ClaudeErrorScreen + ClaudeBusyScreen,
                     ClaudeErrorScreen + "Enter to confirm - Esc to cancel\r\n",
                     ClaudeErrorScreen.Replace("\u276f ", "\u276f draft in progress"),
                     ClaudeErrorScreen.Replace("\u276f \r\n", "\u276f \r\n  multiline draft\r\n"),
                     ClaudeErrorScreen.Replace("\u276f \r\n", "\u276f \r\n--------\r\n  multiline draft\r\n"),
                     "API Error: 503\r\n", ""
                 })
        {
            var watch = new KeepAliveWatch(TerminalKind.ClaudeCode);
            Assert(watch.Observe(screen, options) is not (KeepAliveState.Ready or KeepAliveState.RetryReady or KeepAliveState.HighDemandReady),
                $"Do not inject into idle, permanent errors, history, drafts or menus: {screen}");
        }
        var disabled = new KeepAliveWatch(TerminalKind.ClaudeCode);
        Assert(disabled.Observe(ClaudeErrorScreen, null) == KeepAliveState.Disabled, "Null options disable Claude retries.");
        Assert(disabled.Observe(ClaudeErrorScreen, options with { ClaudeRetryEnabled = false }) == KeepAliveState.Disabled,
            "Codex switches must not enable Claude retries.");
        Assert(KeepAliveWatch.HasEmptyClaudePrompt(ClaudeIdleScreen.Replace('-', '\u2500')), "Accept the native Unicode border.");
    }

    private static void TestClaudeRetryCycle()
    {
        var options = new KeepAliveOptions(TimeSpan.FromMinutes(3), ["continue"], 2, IdleEnabled: false, HighDemandEnabled: false);
        var watch = new KeepAliveWatch(TerminalKind.ClaudeCode);
        foreach (var countdown in new[] { "Retrying in 2s \u00b7 attempt 1/10", "Retrying in 0.5 seconds... (attempt 10/10)" })
        {
            var screen = "API Error: 529\r\n" + countdown + "\r\n" + ClaudeIdleScreen;
            Assert(watch.Observe(screen, options) == KeepAliveState.Reconnecting, "Built-in retries must finish first.");
            Assert(watch.RetryStatus == countdown, "Expose the current retry progress.");
            Assert(watch.Observe(screen, options with { IdleThreshold = TimeSpan.Zero }) == KeepAliveState.Reconnecting,
                "Zero delay must not bypass built-in retries, even at the final attempt.");
        }
        var failed = ClaudeRetryScreen + ClaudeErrorScreen;
        Assert(watch.Observe(failed, options) == KeepAliveState.RetryWaiting, "Only a final error starts the one-second wait.");
        Thread.Sleep(1100);
        Assert(watch.Observe(failed, options) == KeepAliveState.RetryReady, "Do not wait for the 180-second Codex idle threshold.");
        watch.Defer();
        Assert(watch.Observe(failed, options) == KeepAliveState.RetryWaiting, "A refused send restarts the delay.");
        Thread.Sleep(1100);
        Assert(watch.Observe(failed, options) == KeepAliveState.RetryReady, "Retry after deferring.");
        watch.MarkSent();
        Assert(watch.Sent == 1, "A Claude retry consumes the shared send allowance.");
        watch.Observe("", options);
        Assert(watch.Observe(failed + "footer repaint\r\n", options) == KeepAliveState.RetrySent, "Repainting must not repeat a send.");
        watch.Observe(ClaudeIdleScreen, options);
        Assert(watch.Observe(ClaudeErrorScreen, options) == KeepAliveState.RetrySent, "Temporarily hidden errors must not rearm a send.");
        watch.Observe(ClaudeErrorScreen.Replace("\u276f ", "\u276f user draft"), options);
        Assert(watch.Observe(ClaudeErrorScreen, options) == KeepAliveState.RetrySent, "Clearing a draft is not a new failed turn.");
        watch.Observe(ClaudeBusyScreen, options);
        Assert(watch.Observe(ClaudeErrorScreen, options with { IdleThreshold = TimeSpan.Zero }) == KeepAliveState.RetryReady,
            "The same error may retry again after observing a new running turn.");
        watch.MarkSent();
        Assert(watch.Observe(ClaudeErrorScreen, options) == KeepAliveState.Exhausted, "Enforce the per-session cap.");

        var unlimited = new KeepAliveWatch(TerminalKind.ClaudeCode);
        for (var i = 0; i < 3; i++)
        {
            var immediate = options with { IdleThreshold = TimeSpan.Zero, MaxSends = 0 };
            unlimited.Observe(ClaudeRetryScreen, immediate);
            Assert(unlimited.Observe(ClaudeErrorScreen, immediate) == KeepAliveState.RetryReady, "Zero cap means unlimited failed turns.");
            unlimited.MarkSent();
            Assert(unlimited.Observe(ClaudeErrorScreen, immediate) == KeepAliveState.RetrySent, "Unlimited still means once per failed turn.");
        }
    }

    private static void TestClaudeOptions()
    {
        using var form = new MainForm();
        var controls = Descendants(form).ToArray();
        var claude = controls.OfType<CheckBox>().Single(box => box.Name == "claudeRetry");
        Assert(claude.Checked && !string.IsNullOrWhiteSpace(claude.AccessibleName), "Expose an accessible, enabled-by-default Claude retry switch.");
        controls.OfType<CheckBox>().Single(box => box.Name == "keepAlive").Checked = false;
        controls.OfType<CheckBox>().Single(box => box.Name == "highDemandRetry").Checked = false;
        var read = typeof(MainForm).GetMethod("CurrentKeepAlive", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var options = (KeepAliveOptions)read.Invoke(form, null)!;
        Assert(options.ClaudeRetryEnabled && !options.IdleEnabled && !options.HighDemandEnabled, "Claude retries work independently.");
        claude.Checked = false;
        Assert(read.Invoke(form, null) is null, "Turning every switch off disables automatic text.");
        claude.Checked = true;
        var editable = typeof(MainForm).GetMethod("SetKeepAliveEditable", BindingFlags.Instance | BindingFlags.NonPublic)!;
        editable.Invoke(form, [false]);
        Assert(!claude.Enabled, "Lock the new switch during monitoring.");
        editable.Invoke(form, [true]);
        Assert(claude.Enabled, "Unlock the new switch after stopping.");
        controls.OfType<TextBox>().Single(box => box.Name == "keepAliveText").Text = " ";
        Assert(read.Invoke(form, null) is null, "An empty prompt pool disables Claude retries too.");
    }

    private static void TestClaudeTurnFailures()
    {
        foreach (var screen in new[]
                 {
                     ClaudeErrorScreen, ClaudeRetryScreen,
                     "API Error: 401 Unauthorized\r\n" + ClaudeIdleScreen,
                     "Invalid API key - Please run /login\r\n" + ClaudeIdleScreen,
                     "You've hit your limit - resets tomorrow\r\n" + ClaudeIdleScreen
                 })
        {
            var watch = new TurnWatcher(TerminalKind.ClaudeCode);
            watch.Observe(ClaudeBusyScreen);
            for (var i = 0; i < 5; i++)
                Assert(watch.Observe(screen) is null, $"A failed Claude turn is not a successful return: {screen}");
        }
    }

    private static string ClaudeFixtureScreen(string mode) => mode switch
    {
        "claude-error" or "claude-error-cooked" => ClaudeErrorScreen,
        "claude-retry" => ClaudeRetryScreen,
        "claude-idle" => ClaudeIdleScreen,
        "claude-permanent" => "API Error: 401 Unauthorized\r\n" + ClaudeIdleScreen,
        "claude-draft" => ClaudeErrorScreen.Replace("\u276f ", "\u276f draft in progress"),
        "claude-menu" => "Do you want to proceed?\r\n\u276f 1. Yes\r\n  2. No\r\nEnter to confirm - Esc to cancel\r\n",
        "claude-codex-menu" => Prompt,
        _ => throw new ArgumentException("Unknown Claude fixture mode.", nameof(mode))
    };

    private static void TestClaudeConsole()
    {
        foreach (var mode in new[]
                 {
                     "claude-error", "claude-retry", "claude-idle", "claude-permanent",
                     "claude-draft", "claude-menu", "claude-codex-menu", "claude-error-cooked"
                 })
        {
            using var fixture = StartFixture(mode);
            var target = fixture.Target with { Kind = TerminalKind.ClaudeCode };
            var options = new KeepAliveOptions(TimeSpan.Zero, ["resume this task"], 2, IdleEnabled: false, HighDemandEnabled: false);
            var monitor = new ConsoleMonitor(target, options);
            var sends = 0;
            for (var i = 0; i < 15; i++)
            {
                var poll = monitor.Poll();
                Assert(!poll.Sent, $"Claude must never receive a Codex approval key: {mode}");
                if (poll.KeptAlive)
                {
                    sends++;
                    Assert(poll.Status.Contains("Claude Code") && poll.Prompt == "resume this task", "Label the retry and use the configured pool.");
                }
                Thread.Sleep(80);
            }
            var expected = mode == "claude-error" ? "resume this task\r" : "";
            var received = ReadIfPresent(Path.Combine(fixture.Directory, "received")) ?? "";
            Assert(sends == (expected.Length > 0 ? 1 : 0) && received == expected, $"Unexpected terminal input for {mode}: {received}");
            if (mode is "claude-draft" or "claude-menu" or "claude-error-cooked")
            {
                var signature = KeepAliveWatch.Normalize(TerminalConsole.Read(target));
                Assert(!TerminalConsole.SendTextIfUnchanged(target, signature, "must not send"), "Recheck input guards at the send boundary.");
            }
            monitor.Stop();
            Assert(!monitor.Poll().KeptAlive, "A stopped Claude monitor cannot send again.");
            Assert(!File.Exists(Path.Combine(fixture.Directory, "error")), "The fixture must remain healthy.");
        }
    }

    private static void TestClaudeDiscovery()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "claude.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "CodexAutoYes.Tests.exe"), executable, overwrite: false);
        try
        {
            using var fixture = StartFixture("claude-error", executable);
            var scan = TerminalConsole.FindCliSessions();
            var target = scan.Targets.SingleOrDefault(candidate => candidate.ProcessId == fixture.Target.ProcessId);
            Assert(target is { Kind: TerminalKind.ClaudeCode, CliName: "Claude Code" }, "Discover native claude.exe with the correct CLI identity.");
            Assert(target!.StartTime == fixture.Target.StartTime, "Preserve PID reuse protection when scanning Claude.");
            Assert(TerminalConsole.Read(target).Contains("API Error: 503"), "Read the discovered Claude console without sending anything.");
        }
        finally
        {
            File.Delete(executable);
        }
    }

    private static void TestClaudeGui()
    {
        using var fixture = StartFixture("claude-error");
        TestGuiMonitor(fixture.Target with { Kind = TerminalKind.ClaudeCode }, claude: true);
        WaitUntil(() => ReadIfPresent(Path.Combine(fixture.Directory, "received")) == "\u8ba1\u5212\u4e66\u7f16\u5199\r",
            "The GUI must submit its configured prompt to Claude.");
    }
}
