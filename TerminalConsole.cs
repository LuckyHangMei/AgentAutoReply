using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexAutoYes;

internal enum TerminalKind { Codex, ClaudeCode }

internal sealed record TerminalTarget(
    int ProcessId, long StartTime, string Title, nint ConsoleWindow, TerminalKind Kind = TerminalKind.Codex)
{
    internal string CliName => Kind == TerminalKind.ClaudeCode ? "Claude Code" : "Codex";
    public override string ToString() => $"{Title}  ·  PID {ProcessId}";
}

/// <summary>会话扫描结果：可监听的会话，以及被跳过进程的原因。</summary>
/// <param name="Targets">可读取、可监听的 CLI 会话。</param>
/// <param name="Skipped">被跳过的进程及原因，用于在界面上解释为什么列表为空。</param>
/// <param name="NeedsElevation">
/// 因权限不足（ERROR_ACCESS_DENIED）而无法连接的会话数。这类会话运行在
/// 完整性级别更高的终端里（例如以管理员身份启动的 Windows Terminal），
/// 单独计数以便界面给出"以管理员身份重启"这种可执行的提示，
/// 而不是刷一堆看不懂的"拒绝访问"。
/// </param>
internal sealed record SessionScan(List<TerminalTarget> Targets, List<string> Skipped, int NeedsElevation);

internal static class TerminalConsole
{
    /// <summary>
    /// ERROR_ACCESS_DENIED。AttachConsole 连接完整性级别更高的进程时固定返回这个错误，
    /// 例如工具是普通权限、Codex 跑在以管理员身份启动的终端里。
    /// </summary>
    private const int ErrorAccessDenied = 5;

    // AttachConsole changes process-wide state, including when scanning other sessions.
    private static readonly object Sync = new();

    /// <summary>扫描本机可监听的 Codex 和 Claude Code CLI 会话。</summary>
    /// <returns>可监听会话列表，以及被跳过进程的原因说明。</returns>
    internal static SessionScan FindCliSessions()
    {
        var targets = new List<TerminalTarget>();
        var skipped = new List<string>();
        var consoles = new HashSet<nint>();
        var needsElevation = 0;
        // shortcut: 扫描原生可执行程序；支持旧版 Node/WSL 启动方式时再增加进程识别。
        var processes = new[] { "codex", "claude" }.SelectMany(Process.GetProcessesByName).OrderBy(p => p.Id).ToArray();
        if (processes.Length == 0)
            skipped.Add("没有正在运行的 codex 或 claude 进程。");
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var kind = process.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase)
                        ? TerminalKind.ClaudeCode : TerminalKind.Codex;
                    var target = new TerminalTarget(process.Id, process.StartTime.ToUniversalTime().Ticks, "", 0, kind);
                    WithConsole(target, () =>
                    {
                        using var output = Open("CONOUT$", 0x80000000);
                        ReadScreen(output);
                        var window = Native.GetConsoleWindow();
                        // ConPTY 下部分会话没有控制台窗口句柄，但依然可读可写，
                        // 这类会话按 PID 去重，不能直接丢弃。
                        if (window == 0 || consoles.Add(window))
                            targets.Add(target with { Title = GetTitle(target.CliName), ConsoleWindow = window });
                        else
                            skipped.Add($"PID {process.Id}：与另一会话共用同一终端窗口。");
                        return 0;
                    });
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
                {
                    // 权限不足单独计数：这类会话在更高完整性级别的终端里，
                    // 只能靠提权解决，和"进程已退出""没有控制台"不是一类问题。
                    if (ex is Win32Exception { NativeErrorCode: ErrorAccessDenied })
                        needsElevation++;
                    // App-server 和已退出的 Codex 进程没有可附加的控制台；记录原因便于排查。
                    skipped.Add($"PID {process.Id}：{ex.Message}");
                }
            }
        }
        return new SessionScan(targets, skipped, needsElevation);
    }

    /// <summary>本进程是否以管理员身份运行。</summary>
    /// <returns>true 表示当前已提权。</returns>
    internal static bool IsElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    internal static string Read(TerminalTarget target) => WithConsole(target, () =>
    {
        using var output = Open("CONOUT$", 0x80000000);
        return ReadScreen(output);
    });

    internal static bool ConfirmIfUnchanged(TerminalTarget target, string signature) => WithConsole(target, () =>
    {
        if (target.Kind != TerminalKind.Codex)
            return false;
        using var output = Open("CONOUT$", 0x80000000);
        using var input = Open("CONIN$", 0xC0000000);
        Check(Native.GetConsoleMode(input, out var mode), "无法读取终端输入模式");
        // A shell/read-line prompt must never receive an approval key.
        if ((mode & 0x0006) != 0 || ApprovalPrompt.Find(ReadScreen(output))?.Signature != signature)
            return false;
        Check(Native.GetNumberOfConsoleInputEvents(input, out var pending), "无法读取终端输入状态");
        if (pending != 0)
            return false;
        SendY(input);
        return true;
    });

    /// <summary>
    /// 在画面未变的前提下，向会话写入一段文本并回车，用于空闲续跑。
    /// 安全前提与 <see cref="ConfirmIfUnchanged"/> 完全一致：必须是原始输入模式、
    /// 没有待处理输入、画面与判定时一致；另外多一条 —— 画面上不能有审批菜单，
    /// 审批永远由 y 处理，不能被续跑文本抢走。
    /// </summary>
    /// <param name="target">目标会话。</param>
    /// <param name="signature">判定空闲时的画面签名，见 <see cref="KeepAliveWatch.Normalize"/>。</param>
    /// <param name="text">要发送的续跑文本，末尾自动补回车。</param>
    /// <returns>true 表示已写入；false 表示前提不满足，本次未写入任何按键。</returns>
    internal static bool SendTextIfUnchanged(TerminalTarget target, string signature, string text) => WithConsole(target, () =>
    {
        using var output = Open("CONOUT$", 0x80000000);
        using var input = Open("CONIN$", 0xC0000000);
        Check(Native.GetConsoleMode(input, out var mode), "无法读取终端输入模式");
        // A shell/read-line prompt must never receive keep-alive text.
        if ((mode & 0x0006) != 0)
            return false;
        var screen = ReadScreen(output);
        if (KeepAliveWatch.Normalize(screen) != signature || ApprovalPrompt.Find(screen) is not null)
            return false;
        if (target.Kind == TerminalKind.ClaudeCode &&
            (KeepAliveWatch.LooksBusy(screen) || !KeepAliveWatch.HasEmptyClaudePrompt(screen)))
            return false;
        Check(Native.GetNumberOfConsoleInputEvents(input, out var pending), "无法读取终端输入状态");
        if (pending != 0)
            return false;
        SendText(input, text);
        return true;
    });

    private static unsafe T WithConsole<T>(TerminalTarget target, Func<T> action)
    {
        lock (Sync)
        {
            using var process = Process.GetProcessById(target.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != target.StartTime || process.HasExited)
                throw new InvalidOperationException("原 CLI 进程已退出，请重新选择窗口。");
            // AttachConsole 在本进程已附加控制台时直接返回 ERROR_ACCESS_DENIED，
            // 而 GUI 从 cmd / 批处理启动时会继承父进程的控制台。先脱离，未附加时失败可忽略。
            Native.FreeConsole();
            Check(Native.AttachConsole((uint)target.ProcessId), "无法连接 CLI 终端");
            try
            {
                Check(Native.SetConsoleCtrlHandler(&IgnoreConsoleControl, 1), "无法隔离终端控制事件");
                if (process.HasExited || (target.ConsoleWindow != 0 && Native.GetConsoleWindow() != target.ConsoleWindow))
                    throw new InvalidOperationException("CLI 终端已关闭或更换，请重新选择窗口。");
                return action();
            }
            finally
            {
                Check(Native.FreeConsole(), "无法断开终端连接");
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int IgnoreConsoleControl(uint _) => 1;

    private static SafeFileHandle Open(string name, uint access)
    {
        var handle = Native.CreateFileW(name, access, 3, 0, 3, 0, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Error(error, "无法打开终端缓冲区");
        }
        return handle;
    }

    /// <summary>读取当前终端标题，并整理成适合下拉框显示的短文本。</summary>
    /// <returns>会话标题；读取失败或内容无意义时回退为 CLI 名称。</returns>
    private static unsafe string GetTitle(string cliName)
    {
        var buffer = stackalloc char[1024];
        var length = Native.GetConsoleTitleW(buffer, 1024);
        var title = length == 0 ? "" : new string(buffer, 0, (int)length);
        // Codex 在标题里放盲文点阵动画字符（U+2800-U+28FF），整条剔除后才能判断标题是否真的为空。
        title = new string(title.Where(c => c is < '⠀' or > '⣿').ToArray()).Trim();
        // 任务名为空时会剩下 "| 目录" 这样的残留分隔符。
        title = title.TrimStart('|').Trim();
        if (title.Length == 0)
            return $"{cliName} CLI";
        // 没有设置标题的会话会显示完整可执行文件路径，只保留文件名。
        return title.Contains('\\') && Path.IsPathFullyQualified(title)
            ? Path.GetFileName(title)
            : title;
    }

    private static unsafe string ReadScreen(SafeFileHandle output)
    {
        Check(Native.GetConsoleScreenBufferInfo(output, out var info), "无法读取终端尺寸");
        var area = info.Window;
        var width = area.Right - area.Left + 1;
        var height = area.Bottom - area.Top + 1;
        if (width <= 0 || height <= 0 || (long)width * height > 1_000_000)
            throw new InvalidOperationException("终端缓冲区尺寸无效。");
        var cells = new Native.CharInfo[width * height];
        fixed (Native.CharInfo* buffer = cells)
            Check(Native.ReadConsoleOutputW(output, buffer, new((short)width, (short)height), new(0, 0), ref area), "无法读取终端画面");
        if (area.Left != info.Window.Left || area.Top != info.Window.Top ||
            area.Right != info.Window.Right || area.Bottom != info.Window.Bottom)
            return ""; // Ignore a partial frame while the user resizes the terminal.
        var text = new StringBuilder(cells.Length + height);
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var cell = cells[row * width + col];
                if ((cell.Attributes & 0x0200) == 0)
                    text.Append(cell.Character == 0 ? ' ' : (char)cell.Character);
            }
            text.AppendLine();
        }
        return text.ToString();
    }

    private static unsafe void SendY(SafeFileHandle input)
    {
        var records = stackalloc Native.InputRecord[2];
        records[0] = new() { EventType = 1, KeyDown = 1, RepeatCount = 1, VirtualKey = 0x59,
            VirtualScanCode = (ushort)Native.MapVirtualKeyW(0x59, 0), Character = 'y' };
        records[1] = records[0];
        records[1].KeyDown = 0;
        Check(Native.WriteConsoleInputW(input, records, 2, out var written), "无法发送确认按键");
        if (written != 2)
            throw new InvalidOperationException("确认按键未完整写入，监听已停止。请检查终端。");
    }

    /// <summary>把文本逐字符写入终端输入缓冲，末尾补一个回车提交。</summary>
    /// <param name="input">已打开的 CONIN$ 句柄。</param>
    /// <param name="text">续跑文本，不含回车。</param>
    private static unsafe void SendText(SafeFileHandle input, string text)
    {
        var keys = new List<Native.InputRecord>((text.Length + 2) * 2);
        foreach (var character in text)
            AddKey(0, character);
        // End 刷新 Codex 的粘贴缓冲并结束粘贴状态，避免紧随文本的 Enter 被当成换行。
        AddKey(0x23, '\0');
        AddKey(0x0D, '\r');
        var records = keys.ToArray();
        uint written;
        fixed (Native.InputRecord* buffer = records)
            Check(Native.WriteConsoleInputW(input, buffer, (uint)records.Length, out written), "无法发送续跑文本");
        if (written != records.Length)
            throw new InvalidOperationException("续跑文本未完整写入，监听已停止。请检查终端。");

        void AddKey(ushort virtualKey, char character)
        {
            var key = new Native.InputRecord
            {
                EventType = 1, KeyDown = 1, RepeatCount = 1,
                VirtualKey = virtualKey,
                VirtualScanCode = virtualKey == 0 ? (ushort)0 : (ushort)Native.MapVirtualKeyW(virtualKey, 0),
                Character = character,
                ControlKeyState = virtualKey == 0x23 ? 0x0100u : 0u // End 的 ENHANCED_KEY 标记。
            };
            keys.Add(key);
            key.KeyDown = 0;
            keys.Add(key);
        }
    }

    private static void Check(int result, string message)
    {
        if (result == 0)
            throw Error(Marshal.GetLastPInvokeError(), message);
    }

    private static Win32Exception Error(int error, string message) =>
        new(error, $"{message}（{error}: {new Win32Exception(error).Message}）");
}

internal static unsafe partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Coord(short x, short y) { internal short X = x; internal short Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SmallRect { internal short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BufferInfo
    {
        internal Coord Size, Cursor;
        internal ushort Attributes;
        internal SmallRect Window;
        internal Coord MaximumWindowSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct CharInfo { internal ushort Character, Attributes; }
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    internal struct InputRecord
    {
        [FieldOffset(0)] internal ushort EventType;
        [FieldOffset(4)] internal int KeyDown;
        [FieldOffset(8)] internal ushort RepeatCount;
        [FieldOffset(10)] internal ushort VirtualKey;
        [FieldOffset(12)] internal ushort VirtualScanCode;
        [FieldOffset(14)] internal ushort Character;
        [FieldOffset(16)] internal uint ControlKeyState;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int AttachConsole(uint processId);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int FreeConsole();
    [LibraryImport("kernel32.dll")]
    internal static partial nint GetConsoleWindow();
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int SetConsoleCtrlHandler(delegate* unmanaged[Stdcall]<uint, int> handler, int add);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetConsoleTitleW(char* title, uint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GetConsoleScreenBufferInfo(SafeFileHandle output, out BufferInfo info);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int ReadConsoleOutputW(SafeFileHandle output, CharInfo* buffer, Coord size, Coord origin, ref SmallRect region);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GetConsoleMode(SafeFileHandle handle, out uint mode);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GetNumberOfConsoleInputEvents(SafeFileHandle input, out uint count);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int WriteConsoleInputW(SafeFileHandle input, InputRecord* records, uint count, out uint written);
    [LibraryImport("user32.dll")]
    internal static partial uint MapVirtualKeyW(uint code, uint mapType);
}
