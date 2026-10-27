using System.Text.RegularExpressions;

namespace CodexAutoYes;

/// <summary>一次成功返回，摘要用于通知正文。</summary>
internal sealed record TurnReturn(string Summary);

/// <summary>先观察执行态，再等待三帧稳定空闲；每回合只报告一次成功返回。</summary>
internal sealed class TurnWatcher(TerminalKind kind = TerminalKind.Codex)
{
    private const int StablePolls = 3;
    private const int SummaryLength = 120;

    private static readonly Regex Chrome = new(
        @"^(?:[›❯>].*|\?\s*for\s+shortcuts|send\s+with\s+enter|press\s+enter\s+to\b.*|" +
        @"esc\s+to\s+cancel|token\s+usage\b.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Decoration = new(
        @"^[\s─━│┃┄┅┆┇┈┉┊┋╭╮╯╰┌┐└┘├┤┬┴┼=_\-]+$",
        RegexOptions.CultureInvariant);

    private bool _sawBusy;
    private string? _idleSignature;
    private int _idlePolls;

    /// <summary>读取一帧画面；本帧构成成功返回时给出摘要，否则返回 null。</summary>
    internal TurnReturn? Observe(string screen)
    {
        var signature = KeepAliveWatch.Normalize(screen);
        // 调整窗口或读取失败时的空画面不改变上一帧的状态。
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
        return KeepAliveWatch.FooterHasProblem(signature, kind) ? null : new TurnReturn(Summarize(signature));
    }

    /// <summary>从尾部 12 条非空行提取摘要，去掉界面装饰并限制为 120 字。</summary>
    internal static string Summarize(string signature)
    {
        var lines = signature.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length != 0)
            .TakeLast(12)
            .Where(line => !Chrome.IsMatch(line) && !Decoration.IsMatch(line));
        var text = Regex.Replace(string.Join(' ', lines), @"\s+", " ").Trim();
        if (text.Length == 0)
            return "已返回回答";
        return text.Length <= SummaryLength ? text : text[..(SummaryLength - 1)] + "…";
    }
}
