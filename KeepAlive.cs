using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodexAutoYes;

/// <summary>
/// 空闲或响应异常时自动续跑（保活）的配置。整体为 null 时表示该功能关闭。
/// </summary>
/// <param name="IdleThreshold">终端画面需要连续静止多久才续跑；0 表示空闲或异常时立即发送。</param>
/// <param name="Prompts">
/// 空闲或响应异常时使用的续跑文本池，每次随机取一条，末尾自动补回车提交。必须非空。
/// 用池而不是固定一句：反复发同一句话容易让模型复述上一轮结论或空转，
/// 换着问更容易把它推到下一步。
/// </param>
/// <param name="MaxSends">同一会话最多续跑多少次；0 表示不限次数。</param>
/// <param name="IdleEnabled">启用普通空闲及其他响应异常的续跑。</param>
/// <param name="HighDemandEnabled">高负载回合失败后发送文本池内容；空闲阈值为 0 时立即，否则等待 1 秒。</param>
/// <param name="ClaudeRetryEnabled">Claude Code 临时错误最终失败后续发一次，不做普通空闲续跑。</param>
internal sealed record KeepAliveOptions(
    TimeSpan IdleThreshold, IReadOnlyList<string> Prompts, int MaxSends,
    bool IdleEnabled = true, bool HighDemandEnabled = true, bool ClaudeRetryEnabled = true)
{
    /// <summary>
    /// 默认续跑文本池。刻意只放"接着干"这一种语义 —— 像"还有什么能优化的"
    /// 这类开放式提问会让模型自己发明新任务，在自动确认开着的情况下容易跑偏。
    /// 需要更强的推动力请自行在界面上增删。
    /// </summary>
    internal static readonly string[] DefaultPrompts =
    [
        "继续",
        "继续推进，不要停",
        "接着做下一步",
        "继续完成剩下的工作",
        "继续，把当前任务做完",
        "continue",
        "keep going"
    ];
}

/// <summary>
/// 从文本池里取续跑文本。用洗牌袋而不是每次独立随机：
/// 独立随机会出现连着发同一句的情况，而连发重复正是要避免的。
/// 洗牌袋保证一轮内每条都用一次，且跨轮也不会紧挨着重复。
/// </summary>
internal sealed class KeepAlivePrompts
{
    private readonly IReadOnlyList<string> _pool;
    /// <summary>本轮剩余待发的文本，从末尾取。</summary>
    private readonly List<string> _bag = [];
    /// <summary>上一次发出的文本，用于避免跨轮紧邻重复。</summary>
    private string? _last;

    /// <summary>按给定文本池建立取样器。</summary>
    /// <param name="pool">续跑文本池，必须非空。</param>
    /// <exception cref="ArgumentException">文本池为空。</exception>
    internal KeepAlivePrompts(IReadOnlyList<string> pool)
    {
        if (pool.Count == 0)
            throw new ArgumentException("续跑文本池不能为空。", nameof(pool));
        _pool = pool;
    }

    /// <summary>取下一条续跑文本。</summary>
    /// <returns>本次要发送的文本。</returns>
    internal string Next()
    {
        if (_pool.Count == 1)
            return _pool[0];
        if (_bag.Count == 0)
            Refill();
        var prompt = _bag[^1];
        _bag.RemoveAt(_bag.Count - 1);
        _last = prompt;
        return prompt;
    }

    /// <summary>重新装袋并洗牌，顺带避开与上一条紧邻重复。</summary>
    private void Refill()
    {
        _bag.AddRange(_pool);
        for (var i = _bag.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
        }
        // 新一轮第一条（袋尾）与上一轮最后一条相同时，和袋里任意不同的一条换位。
        if (_bag.Count > 1 && _bag[^1] == _last)
        {
            var other = _bag.FindIndex(prompt => prompt != _last);
            if (other >= 0)
                (_bag[^1], _bag[other]) = (_bag[other], _bag[^1]);
        }
    }
}

/// <summary>单个会话一轮保活判定的结果。</summary>
internal enum KeepAliveState
{
    /// <summary>未启用保活。</summary>
    Disabled,
    /// <summary>画面仍在变化，或空闲时间尚未达到阈值。</summary>
    Waiting,
    /// <summary>已空闲足够久，但画面底部表明会话正在执行或停在其它按键选择界面。</summary>
    Blocked,
    /// <summary>该会话已达到续跑次数上限。</summary>
    Exhausted,
    /// <summary>可以发送续跑文本。</summary>
    Ready,
    /// <summary>其他响应异常已达到空闲阈值，可以发送文本池内容。</summary>
    RetryReady,
    /// <summary>高负载回合失败已达到续发条件，可以发送文本池内容。</summary>
    HighDemandReady,
    /// <summary>CLI 正在内置重试，本工具不发送。</summary>
    Reconnecting,
    /// <summary>Claude Code 临时错误已最终失败，等待 1 秒。</summary>
    RetryWaiting,
    /// <summary>Claude Code 本次失败已重试，等待下一回合。</summary>
    RetrySent,
    /// <summary>高负载回合已失败，等待 1 秒。</summary>
    HighDemandWaiting,
    /// <summary>本次失败已续发，等待下一回合，不重复发送。</summary>
    HighDemandSent
}

/// <summary>
/// 普通空闲按画面静止时间续跑；高负载则等待内置重连结束，失败后续发一次。
/// 两条通道都给执行中状态、按键菜单和用户输入让路。
/// </summary>
internal sealed class KeepAliveWatch(TerminalKind kind = TerminalKind.Codex)
{
    /// <summary>
    /// 画面底部出现这些提示，说明会话正在执行，或停在审批以外的按键选择界面，
    /// 此时注入文本可能被当成菜单操作，必须让开。
    /// </summary>
    private static readonly Regex Busy = new(
        @"to\s+interrupt|to\s+confirm|to\s+cancel|to\s+select|\(\s*y\s*/\s*n\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HighDemand = new(
        @"^(?:\[\d{2}:\d{2}:\d{2}\]\s*)?(?<failed>[■✗×!]\s*|(?:error|错误)\s*[:：]\s*)?" +
        @"(?:error\s*:\s*)?we['’]re\s+currently\s+experiencing\s+high\s+demand," +
        @"\s+which\s+may\s+cause\s+temporary\s+errors\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex Reconnect = new(
        @"\breconnecting(?:\.{3}|…)?\s*\d+\s*/\s*\d+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ResponseError = new(
        @"^(?:[■✗×!]\s*)?(?:error\s*:\s*)?(?:" +
        @"rate[\s_-]+limit[\s_-]+(?:exceeded|reached)|exceeded\s+(?:token|request)s?\s+rate\s+limit|too\s+many\s+requests|" +
        @"(?:unexpected\s+status(?:\s+code)?\s*:?\s*|http\s+)(?:429|500|502|503|504)\b|" +
        @"stream\s+disconnected\s+before\s+completion|error\s+sending\s+request|(?:request|connection)\s+timed\s+out|" +
        @"internal\s+server\s+error|bad\s+gateway|service\s+unavailable|gateway\s+timeout)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    // Claude Code error/retry text: https://code.claude.com/docs/en/errors
    private const string ClaudeNetworkError =
        @"(?:request\s+timed\s+out|connection\s+(?:error|refused|dropped|lost|closed)|unable\s+to\s+connect\s+to\s+API|" +
        @"no\s+response\s+from\s+API|stream\s+idle\s+timeout|server\s+error\s+mid-response|response\s+stalled)\b";

    private static readonly Regex ClaudeError = new(
        @"^(?:[⎿■✗×!●⏺]\s*)?(?:(?:(?:API\s+)?Error\s*:\s*)+|(?=" + ClaudeNetworkError +
        @"|Invalid API key\b|Not logged in\b|Credit balance too low\b|You['’]ve hit your limit\b))(?<message>[^\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex ClaudeTransientError = new(
        @"\A(?:(?:408|429|500|502|503|504|529)\b|Repeated\s+529\s+Overloaded\s+errors\b|" +
        @"(?:overloaded(?:_error)?|rate[\s_-]+limit(?:[\s_-]+(?:exceeded|reached|error))?|too\s+many\s+requests|" +
        @"internal\s+server\s+error|bad\s+gateway|service\s+unavailable|gateway\s+timeout|ECONNRESET|ETIMEDOUT)\b|" +
        ClaudeNetworkError + @")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ClaudeRetry = new(
        @"\bretrying\s+in\s+\d+(?:\.\d+)?\s*(?:ms|s|seconds?)\b[^\n]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ClaudeMessage = new(@"^[❯>●⏺][ \t]+\S", RegexOptions.Multiline);

    /// <summary>只检查画面末尾若干行；回滚历史里的旧提示不代表当前状态。</summary>
    private const int FooterLines = 5;

    /// <summary>当前静止画面的签名；为 null 表示还没建立基线。</summary>
    private string? _signature;
    /// <summary>签名最近一次变化的时间戳，用于计算空闲时长。</summary>
    private long _changedAt;
    /// <summary>该会话已成功续跑的次数。</summary>
    private int _sent;
    private long? _failedAt;
    private bool _failureSent;

    /// <summary>当前的内置重连进度，用于显示和去重日志。</summary>
    internal string? RetryStatus { get; private set; }

    /// <summary>当前静止画面的签名；发送前用它核对画面有没有变过。</summary>
    internal string? Signature => _signature;

    /// <summary>该会话已成功续跑的次数。</summary>
    internal int Sent => _sent;

    /// <summary>当前画面已经静止多久；还没有基线时为零。</summary>
    internal TimeSpan Idle => _signature is null ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_changedAt);

    /// <summary>吃进一帧画面，给出本轮的保活判定。</summary>
    /// <param name="screen">刚读到的终端画面原文。</param>
    /// <param name="options">保活配置；null 表示未启用。</param>
    /// <returns>Ready、RetryReady 或 HighDemandReady 表示可以发送文本池内容。</returns>
    internal KeepAliveState Observe(string screen, KeepAliveOptions? options)
    {
        if (options is null)
        {
            _signature = null;
            ResetFailure();
            RetryStatus = null;
            return KeepAliveState.Disabled;
        }

        var signature = Normalize(screen);
        // 画面读空（例如用户正在拖动终端尺寸）时不能当作空闲依据。
        if (signature.Length == 0)
        {
            _signature = null;
            return KeepAliveState.Waiting;
        }
        var changed = signature != _signature;
        if (changed)
        {
            _signature = signature;
            _changedAt = Stopwatch.GetTimestamp();
        }
        if (kind == TerminalKind.ClaudeCode)
            return ObserveClaude(signature, options);
        // shortcut: 仅从末尾 12 条非空行及错误前缀判断回合结束；CLI 输出格式变化时更新规则。
        var footer = Footer(signature);
        var demand = HighDemand.Matches(footer).LastOrDefault();
        var reconnect = Reconnect.Matches(footer).LastOrDefault();
        // 5/5 仍是重连中；只有后面出现最终错误，才能越过历史中的重连提示。
        if (reconnect is not null &&
            (demand is null || !demand.Groups["failed"].Success || reconnect.Index > demand.Index))
        {
            ResetFailure();
            RetryStatus = reconnect.Value;
            return KeepAliveState.Reconnecting;
        }
        RetryStatus = null;
        if (demand is null)
            ResetFailure();
        if (LooksBusy(screen))
        {
            _failedAt = null;
            return KeepAliveState.Blocked;
        }
        if (demand is not null)
            return ObserveFailure(options);
        if (!options.IdleEnabled)
            return KeepAliveState.Disabled;
        if (options.IdleThreshold > TimeSpan.Zero &&
            (changed || Stopwatch.GetElapsedTime(_changedAt) < options.IdleThreshold))
            return KeepAliveState.Waiting;
        if (options.MaxSends > 0 && _sent >= options.MaxSends)
            return KeepAliveState.Exhausted;
        return ResponseError.IsMatch(footer) ? KeepAliveState.RetryReady : KeepAliveState.Ready;
    }

    private KeepAliveState ObserveClaude(string signature, KeepAliveOptions options)
    {
        RetryStatus = null;
        if (!options.ClaudeRetryEnabled)
        {
            ResetFailure();
            return KeepAliveState.Disabled;
        }
        var footer = Footer(signature);
        var error = ClaudeError.Matches(footer).LastOrDefault();
        var retry = ClaudeRetry.Matches(footer).LastOrDefault();
        if (retry is not null && (error is null || retry.Index > error.Index))
        {
            ResetFailure();
            RetryStatus = retry.Value;
            return KeepAliveState.Reconnecting;
        }
        if (LooksBusy(signature))
        {
            _failedAt = null;
            // 菜单不代表新回合；执行态出现时重新放行同样的错误。
            if (Regex.IsMatch(footer, @"to\s+interrupt", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                ResetFailure();
            return KeepAliveState.Blocked;
        }
        if (!HasEmptyClaudePrompt(signature))
        {
            _failedAt = null;
            return KeepAliveState.Blocked;
        }
        var message = ClaudeMessage.Matches(footer).LastOrDefault();
        if (error is null || (message is not null && message.Index > error.Index))
        {
            _failedAt = null;
            if (message is not null)
                _failureSent = false;
            return KeepAliveState.Waiting;
        }
        if (!ClaudeTransientError.IsMatch(footer[error.Groups["message"].Index..]))
        {
            _failedAt = null;
            return KeepAliveState.Blocked;
        }
        return ObserveFailure(options);
    }

    private KeepAliveState ObserveFailure(KeepAliveOptions options)
    {
        var claude = kind == TerminalKind.ClaudeCode;
        if (!(claude ? options.ClaudeRetryEnabled : options.HighDemandEnabled))
        {
            _failedAt = null;
            return KeepAliveState.Disabled;
        }
        if (options.MaxSends > 0 && _sent >= options.MaxSends)
            return KeepAliveState.Exhausted;
        if (_failureSent)
            return claude ? KeepAliveState.RetrySent : KeepAliveState.HighDemandSent;
        _failedAt ??= Stopwatch.GetTimestamp();
        return options.IdleThreshold == TimeSpan.Zero ||
               Stopwatch.GetElapsedTime(_failedAt.Value) >= TimeSpan.FromSeconds(1)
            ? (claude ? KeepAliveState.RetryReady : KeepAliveState.HighDemandReady)
            : (claude ? KeepAliveState.RetryWaiting : KeepAliveState.HighDemandWaiting);
    }

    /// <summary>记录一次成功的续跑，并清空基线等待画面重新稳定。</summary>
    internal void MarkSent()
    {
        _sent++;
        if (_failedAt is not null)
            _failureSent = true;
        // 普通空闲重新计时；失败重试要等新回合，不能因旧错误留屏而连发。
        _signature = null;
    }

    /// <summary>
    /// 本次发送被终端拒绝（行输入模式、有待处理输入、画面已变）时退让：
    /// 保留基线但重新计时，按本通道的间隔重试；0 秒时下一轮即可重试。
    /// </summary>
    internal void Defer()
    {
        _changedAt = Stopwatch.GetTimestamp();
        if (_failedAt is not null)
            _failedAt = _changedAt;
    }

    private void ResetFailure()
    {
        _failedAt = null;
        _failureSent = false;
    }

    /// <summary>
    /// 把画面归一化成可比较的签名：统一换行、去掉行尾空白与尾部空行。
    /// 不剔除盲文动画字符 —— 动画在转就说明会话在干活，让它重置计时更安全。
    /// </summary>
    /// <param name="screen">终端画面原文。</param>
    /// <returns>画面签名；画面为空白时返回空串。</returns>
    internal static string Normalize(string screen) =>
        string.Join('\n', screen.Replace('\0', ' ').Replace("\r", "").Split('\n')
            .Select(line => line.TrimEnd())).TrimEnd('\n');

    /// <summary>判断画面底部是否处于"正在执行"或其它需要按键选择的界面。</summary>
    /// <param name="screen">终端画面原文。</param>
    /// <returns>true 表示此刻不适合注入续跑文本。</returns>
    internal static bool LooksBusy(string screen) =>
        screen.Replace("\r", "").Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length != 0)
            .TakeLast(FooterLines).Any(Busy.IsMatch);

    /// <summary>归一化画面尾部有响应异常、高负载或重连时，不算成功返回。</summary>
    internal static bool FooterHasProblem(string signature, TerminalKind kind = TerminalKind.Codex)
    {
        var footer = Footer(signature);
        if (kind == TerminalKind.ClaudeCode)
            return ClaudeError.IsMatch(footer) || ClaudeRetry.IsMatch(footer);
        return ResponseError.IsMatch(footer) || HighDemand.IsMatch(footer) || Reconnect.IsMatch(footer);
    }

    internal static bool HasEmptyClaudePrompt(string screen)
    {
        var lines = Footer(Normalize(screen)).Split('\n');
        var prompt = Array.FindLastIndex(lines, line => line.StartsWith('❯') || line.StartsWith('>'));
        var border = Array.FindLastIndex(lines, IsBorder);
        // shortcut: 仅接受上下边框之间的空输入框；Claude 改版或显示占位提示时需更新识别规则。
        return prompt > 0 && lines[prompt].Length == 1 && border == prompt + 1 && IsBorder(lines[prompt - 1]);

        static bool IsBorder(string line) => line.Length >= 3 && line.All(c => c is '─' or '━' or '-');
    }

    /// <summary>取尾部 12 条非空行，忽略更早的历史提示。</summary>
    private static string Footer(string signature) => string.Join('\n', signature.Split('\n')
        .Select(line => line.Trim()).Where(line => line.Length != 0).TakeLast(12));
}
