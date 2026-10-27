namespace CodexAutoYes;

/// <summary>单个会话一轮轮询的结果。</summary>
/// <param name="Status">适合展示在界面上的状态文本。</param>
/// <param name="Screen">本轮读到的终端画面。</param>
/// <param name="Sent">本轮是否发送了审批确认按键 y。</param>
/// <param name="KeptAlive">本轮是否发送了保活续跑文本。</param>
/// <param name="Resumed">该会话累计续跑次数。</param>
/// <param name="Prompt">本轮实际发出的续跑文本；未续跑时为 null。</param>
/// <param name="Notice">重连或失败等待状态发生变化时的日志；相同状态只记录一次。</param>
/// <param name="Returned">本轮是否观察到一次成功返回。</param>
/// <param name="Summary">成功返回的回答摘要；未返回时为 null。</param>
internal sealed record MonitorResult(
    string Status, string Screen, bool Sent, bool KeptAlive = false, int Resumed = 0,
    string? Prompt = null, string? Notice = null, bool Returned = false, string? Summary = null);

/// <summary>
/// 监听单个会话：识别审批菜单并自动确认，空闲或响应异常时按配置间隔续跑。
/// </summary>
/// <param name="target">被监听的终端会话。</param>
/// <param name="keepAlive">保活配置；null 表示不启用保活。</param>
internal sealed class ConsoleMonitor(TerminalTarget target, KeepAliveOptions? keepAlive = null)
{
    private readonly object _sync = new();
    private readonly ApprovalGate _gate = new();
    private readonly KeepAliveWatch _watch = new(target.Kind);
    private readonly TurnWatcher _turn = new(target.Kind);
    /// <summary>每个会话独立取样，各自的轮换顺序互不影响。</summary>
    private readonly KeepAlivePrompts? _prompts =
        keepAlive is null ? null : new KeepAlivePrompts(keepAlive.Prompts);
    private bool _stopped;
    private string? _lastProgress;

    /// <summary>被监听的终端会话，用于在界面上标识对应行。</summary>
    internal TerminalTarget Target => target;

    internal MonitorResult Poll()
    {
        lock (_sync)
        {
            if (_stopped)
                return new("已停止", "", false);
            var screen = TerminalConsole.Read(target);
            var turn = _turn.Observe(screen);
            // 返回标记与本轮审批、续跑结果并存，所有分支都要保留。
            MonitorResult Attach(MonitorResult result) =>
                turn is null ? result : result with { Returned = true, Summary = turn.Summary };
            var prompt = target.Kind == TerminalKind.Codex ? ApprovalPrompt.Find(screen) : null;
            if (_gate.Observe(prompt?.Signature))
            {
                if (!TerminalConsole.ConfirmIfUnchanged(target, prompt!.Signature))
                    return Attach(Idle("监听中 · 等待终端状态稳定", screen));
                _gate.MarkSent(prompt.Signature);
                return Attach(new("监听中 · 已发送 y", screen, true, false, _watch.Sent));
            }
            // 画面上有审批菜单时一律不续跑：审批只能由 y 处理。
            if (prompt is not null)
                return Attach(Idle("监听中 · 确认菜单状态", screen));
            return Attach(KeepAlive(screen));
        }
    }

    /// <summary>审批通道没有动作时，走保活判定。</summary>
    /// <param name="screen">本轮读到的画面。</param>
    /// <returns>本轮结果。</returns>
    private MonitorResult KeepAlive(string screen)
    {
        var state = _watch.Observe(screen, keepAlive);
        var progress = state is KeepAliveState.Reconnecting or KeepAliveState.HighDemandWaiting or KeepAliveState.RetryWaiting
            ? Describe(state) : null;
        var notice = progress != _lastProgress ? progress : null;
        _lastProgress = progress;
        if (state is not (KeepAliveState.Ready or KeepAliveState.RetryReady or KeepAliveState.HighDemandReady))
            return new(Describe(state), screen, false, false, _watch.Sent, Notice: notice);
        var prompt = _prompts!.Next();
        if (!TerminalConsole.SendTextIfUnchanged(target, _watch.Signature!, prompt))
        {
            // 终端此刻不接受输入，按对应通道的间隔退让后重试。
            _watch.Defer();
            return new("监听中 · 续跑被终端拒绝，稍后重试", screen, false, false, _watch.Sent);
        }
        _watch.MarkSent();
        var status = state switch
        {
            KeepAliveState.RetryReady when target.Kind == TerminalKind.ClaudeCode => "Claude Code 响应异常，已重试",
            KeepAliveState.RetryReady => "响应异常，已续跑",
            KeepAliveState.HighDemandReady => "服务高负载，已续跑",
            _ => "已续跑"
        };
        return new($"监听中 · {status}（第 {_watch.Sent} 次）", screen, false, true, _watch.Sent, prompt);
    }

    /// <summary>保活未触发时，仍要把画面喂给保活计时器，否则空闲时长无从累积。</summary>
    /// <param name="status">本轮由审批通道决定的状态文本。</param>
    /// <param name="screen">本轮读到的画面。</param>
    /// <returns>本轮结果。</returns>
    private MonitorResult Idle(string status, string screen)
    {
        _lastProgress = null;
        _watch.Observe(screen, keepAlive);
        return new(status, screen, false, false, _watch.Sent);
    }

    /// <summary>把保活状态翻译成界面状态文本。</summary>
    /// <param name="state">保活判定结果。</param>
    /// <returns>状态文本。</returns>
    private string Describe(KeepAliveState state) => state switch
    {
        KeepAliveState.Disabled => "监听中 · 当前自动发送规则未启用",
        KeepAliveState.Reconnecting => $"监听中 · {target.CliName} 内置重试：{_watch.RetryStatus}",
        KeepAliveState.RetryWaiting => "监听中 · Claude Code 回合已失败，1 秒后自动重试。",
        KeepAliveState.RetrySent => "监听中 · 本次失败已重试，等待下一回合",
        KeepAliveState.HighDemandWaiting => "监听中 · 目标回合已失败，1 秒后自动发送。",
        KeepAliveState.HighDemandSent => "监听中 · 本次失败已续发，等待下一回合",
        KeepAliveState.Blocked when target.Kind == TerminalKind.ClaudeCode => "监听中 · 非重试错误或终端暂不接受输入",
        KeepAliveState.Blocked => "监听中 · 会话执行中，暂不续跑",
        KeepAliveState.Exhausted => $"监听中 · 续跑已达上限（{keepAlive!.MaxSends} 次）",
        _ when target.Kind == TerminalKind.ClaudeCode => "监听中 · 等待 Claude Code 响应异常",
        _ when keepAlive is not null && _watch.Idle >= TimeSpan.FromSeconds(1) =>
            $"监听中 · 空闲 {(int)_watch.Idle.TotalSeconds}s",
        _ => "监听中 · 等待审批提示"
    };

    internal void Stop()
    {
        // Returning from Stop guarantees no old poll can send another key.
        lock (_sync)
            _stopped = true;
    }
}
