using System.Text.RegularExpressions;

namespace CodexAutoYes;

internal sealed record ApprovalPrompt(string Signature, string Heading)
{
    private static readonly Regex Yes = new(
        @"^(?:[›❯>]\s*)?1[.)]\s+Yes,\s+proceed\s*\(y\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex No = new(
        @"^(?:[›❯>]\s*)?\d+[.)]\s+No,\s+(?:and tell Codex what to do differently\s*\(esc\)|continue without running it\s*\(d\))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Footer = new(
        @"^(?:Press\s+)?enter\s+to confirm\s+or\s+esc\s+to cancel(?:\s+or\s+\w\s+to open thread)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SelectionMarker = new(@"^[›❯>]\s*(?=\d+[.)]\s)");

    internal static ApprovalPrompt? Find(string screen)
    {
        var lines = screen.Replace("\r", "").Replace("\0", " ").Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length != 0).ToArray();
        if (lines.Length < 4 || !Footer.IsMatch(lines[^1]))
            return null;

        var yes = Array.FindLastIndex(lines, line => Yes.IsMatch(line));
        if (yes < 1 || !lines.Skip(yes + 1).Any(line => No.IsMatch(line)))
            return null;

        var heading = Array.FindLastIndex(lines, yes - 1, line => line is
            "Would you like to run the following command?" or
            "Would you like to make the following edits?");
        if (heading < 0)
            return null;

        var signature = string.Join('\n', lines.Skip(heading)
            .Select(line => SelectionMarker.Replace(line, "")));
        return new ApprovalPrompt(signature, lines[heading]);
    }
}

internal sealed class ApprovalGate
{
    private string? _candidate;
    private string? _sent;
    private int _stableReads;
    private int _missingReads;

    internal bool Observe(string? signature)
    {
        if (signature is null)
        {
            _candidate = null;
            _stableReads = 0;
            if (++_missingReads >= 2)
                _sent = null;
            return false;
        }

        _missingReads = 0;
        _stableReads = signature == _candidate ? _stableReads + 1 : 1;
        _candidate = signature;
        // shortcut: identical back-to-back dialogs without a clear frame cannot be distinguished; use a structured CLI event API if needed.
        return _stableReads >= 2 && signature != _sent;
    }

    internal void MarkSent(string signature) => _sent = signature;
}
