using System.ComponentModel;
using System.Diagnostics;

namespace CodexAutoYes;

internal sealed class MainForm : Form
{
    /// <summary>次要文字颜色。</summary>
    private static readonly Color Hint = Color.FromArgb(95, 106, 125);

    /// <summary>会话列表，勾选表示监听，选中行决定预览显示哪个会话。</summary>
    private readonly ListView _sessions = new()
    {
        Name = "sessions", View = View.Details, CheckBoxes = true, FullRowSelect = true,
        HideSelection = false, MultiSelect = false, Dock = DockStyle.Fill
    };
    private readonly Button _refresh = new() { Text = "刷新窗口", Dock = DockStyle.Fill, Margin = new Padding(6, 0, 0, 0) };
    private readonly Button _toggleAll = new() { Text = "全选", Dock = DockStyle.Fill };

    /// <summary>
    /// 以管理员身份重启。只有扫描到"因权限不足而连不上"的会话时才出现：
    /// AttachConsole 不允许普通权限进程连接管理员终端里的 Codex。
    /// </summary>
    private readonly Button _elevate = new()
    {
        Name = "elevate", Text = "以管理员身份重启", Dock = DockStyle.Fill, AutoSize = true,
        Visible = false, Margin = new Padding(0, 0, 8, 0)
    };
    private readonly Button _start = new() { Text = "开始监听", Width = 128, Height = 38, Enabled = false };
    private readonly Button _stop = new() { Text = "停止", Width = 92, Height = 38, Enabled = false };
    private readonly Label _status = new() { Name = "status", Text = "未启动", AutoSize = true, ForeColor = Hint };
    private readonly Label _count = new() { Name = "count", Text = "已确认 0 次", AutoSize = true, Margin = new Padding(20, 10, 0, 0) };
    private readonly Label _resumedCount = new() { Name = "resumed", Text = "已续跑 0 次", AutoSize = true, Margin = new Padding(16, 10, 0, 0) };
    private readonly Label _returnedCount = new() { Name = "returned", Text = "已返回 0 次", AutoSize = true, Margin = new Padding(16, 10, 0, 0) };
    private readonly CheckBox _notify = new()
    {
        Name = "notifyOnReturn", Text = "回合返回时通知（仅后台时弹窗）",
        Checked = true, AutoSize = true, Margin = new Padding(0, 5, 0, 0)
    };
    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _trayStart = new("开始监听");
    private readonly ToolStripMenuItem _trayStop = new("停止监听");
    private readonly Action<string, string>? _notifyReturn;
    private FormWindowState _trayRestoreState = FormWindowState.Normal;
    private Icon? _ownedTrayIcon;

    /// <summary>普通空闲与其他响应异常的续跑开关。</summary>
    private readonly CheckBox _keepAlive = new()
    {
        Name = "keepAlive", Text = "Codex 空闲续跑", Checked = true, AutoSize = true,
        Margin = new Padding(0, 6, 0, 0)
    };
    private readonly CheckBox _highDemandRetry = new()
    {
        Name = "highDemandRetry", Text = "Codex 高负载失败后续发：内置重试结束后等待 1 秒；上方设为 0 秒时立即发送",
        Checked = true, AutoSize = true, Margin = new Padding(0, 4, 0, 4)
    };
    private readonly CheckBox _claudeRetry = new()
    {
        Name = "claudeRetry", Text = "Claude Code 错误重试：仅临时故障，等待 1 秒；上方设为 0 秒时立即发送",
        Checked = true, AutoSize = true, Margin = new Padding(0, 0, 0, 4)
    };
    /// <summary>空闲阈值，单位秒；0 表示不等待。</summary>
    private readonly NumericUpDown _idleSeconds = new()
    {
        Name = "idleSeconds", Minimum = 0, Maximum = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond,
        Value = 180, Increment = 1, DecimalPlaces = 3,
        Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 0)
    };
    /// <summary>续跑文本池，一行一条，每次随机取一条发送。</summary>
    private readonly TextBox _keepAliveText = new()
    {
        Name = "keepAliveText", Multiline = true, ScrollBars = ScrollBars.Vertical,
        WordWrap = false, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 0),
        Text = string.Join(Environment.NewLine, KeepAliveOptions.DefaultPrompts)
    };
    /// <summary>把文本池恢复成内置默认值，防止误清空后无从恢复。</summary>
    private readonly Button _resetPrompts = new()
    {
        Name = "resetPrompts", Text = "恢复默认", Dock = DockStyle.Fill, Margin = new Padding(8, 2, 0, 0)
    };
    /// <summary>单会话最大续跑次数，0 表示不限。</summary>
    private readonly NumericUpDown _maxSends = new()
    {
        Name = "maxSends", Minimum = 0, Maximum = 999, Value = 10,
        Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 0)
    };

    private readonly TextBox _preview = ReadOnlyBox("preview");
    private readonly TextBox _log = ReadOnlyBox("log");
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 400 };

    /// <summary>各会话最近一次读到的画面，按 PID 索引。</summary>
    private readonly Dictionary<int, string> _screens = [];
    /// <summary>各会话已确认次数，按 PID 索引。</summary>
    private readonly Dictionary<int, int> _confirms = [];
    /// <summary>各会话已续跑次数，按 PID 索引。</summary>
    private readonly Dictionary<int, int> _resumes = [];
    private MonitorSet? _monitors;
    private bool _polling;
    private bool _closing;
    /// <summary>true 表示正在由代码改动勾选状态，此时不拦截 ItemCheck。</summary>
    private bool _syncingChecks;
    private int _confirmed;
    private int _resumed;
    private int _returned;
    private int _lastReturnPid;
    private bool _shown;

    internal MainForm(Action<string, string>? notify = null)
    {
        _notifyReturn = notify;
        Text = "Codex / Claude Code 监听";
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(940, 948);
        MinimumSize = new Size(860, 880);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        Padding = new Padding(24);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;

        _sessions.Columns.Add("会话", 330);
        _sessions.Columns.Add("PID", 68, HorizontalAlignment.Left);
        _sessions.Columns.Add("状态", 260);
        _sessions.Columns.Add("已确认", 72, HorizontalAlignment.Left);
        _sessions.Columns.Add("已续跑", 72, HorizontalAlignment.Left);

        BuildLayout();
        BuildTray();
        WireEvents();
    }

    internal NotifyIcon Tray => _tray;

    /// <summary>创建常驻托盘图标，启停操作复用主窗口的处理方法。</summary>
    private void BuildTray()
    {
        var show = new ToolStripMenuItem("显示主窗口", null, (_, _) => RestoreFromTray());
        var exit = new ToolStripMenuItem("退出", null, (_, _) => Close());
        _trayStart.Click += (_, _) => StartMonitoring();
        _trayStop.Click += (_, _) => StopMonitoring("已停止");
        _tray.ContextMenuStrip = new ContextMenuStrip();
        _tray.ContextMenuStrip.Items.AddRange([show, _trayStart, _trayStop, new ToolStripSeparator(), exit]);
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _tray.BalloonTipClicked += (_, _) =>
        {
            RestoreFromTray();
            SelectSession(_lastReturnPid);
        };
        UpdateTrayMenu();
        try
        {
            _ownedTrayIcon = TryExtractIcon();
            _tray.Icon = _ownedTrayIcon ?? SystemIcons.Application;
            _tray.Visible = true;
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception)
        {
            _tray.Visible = false;
            Log($"托盘不可用，将保留任务栏入口：{ex.Message}");
        }
    }

    /// <summary>提取本程序图标；取不到时由调用方回退到系统图标。</summary>
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

    /// <summary>隐藏到托盘，不停止监听；托盘不可用时保留任务栏入口。</summary>
    internal void HideToTray()
    {
        if (!_tray.Visible)
            return;
        if (WindowState != FormWindowState.Minimized)
            _trayRestoreState = WindowState;
        var restoreState = _trayRestoreState;
        var syncingChecks = _syncingChecks;
        _syncingChecks = true;
        try
        {
            // 切换任务栏入口会重建列表句柄，允许 WinForms 还原原有勾选状态。
            ShowInTaskbar = false;
            Hide();
        }
        finally
        {
            _syncingChecks = syncingChecks;
            _trayRestoreState = restoreState;
        }
    }

    /// <summary>恢复隐藏前的窗口状态，不重新扫描会话。</summary>
    internal void RestoreFromTray()
    {
        var restoreState = _trayRestoreState;
        var syncingChecks = _syncingChecks;
        _syncingChecks = true;
        try
        {
            // 先离开最小化状态，避免重建任务栏句柄时又触发隐藏。
            WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            Show();
            // 句柄重建中的 Resize 可能暂时报为 Normal，使用隐藏前的快照。
            WindowState = restoreState;
            Activate();
            BringToFront();
        }
        finally
        {
            _syncingChecks = syncingChecks;
            _trayRestoreState = restoreState;
        }
    }

    /// <summary>通知对应的会话仍存在时，选中它并显示最近画面。</summary>
    private void SelectSession(int processId)
    {
        var row = _sessions.Items.Cast<ListViewItem>().FirstOrDefault(item => TargetOf(item).ProcessId == processId);
        if (row is null)
            return;
        _sessions.SelectedItems.Clear();
        row.Selected = true;
        row.Focused = true;
        row.EnsureVisible();
        _sessions.Focus();
        UpdatePreview();
    }

    /// <summary>托盘菜单与主窗口按钮保持一致，包括扫描期间的禁用状态。</summary>
    private void UpdateTrayMenu()
    {
        _trayStart.Enabled = _start.Enabled;
        _trayStop.Enabled = _stop.Enabled;
        _tray.Text = _monitors is null ? Text : $"{Text} · 监听 {_monitors.Count} 个会话";
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 48, 36, 38 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        // 会话列表占比最大：多会话监听时需要一眼看全所有行的状态。
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        foreach (var height in new[] { 242, 50, 32 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        layout.Controls.Add(new Label
        {
            Text = "Codex / Claude Code 监听", Font = new Font(Font.FontFamily, 21, FontStyle.Bold), AutoSize = true
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "Codex 自动确认与续跑；Claude Code 仅在临时错误最终失败后重试，不自动审批。",
            AutoSize = true, ForeColor = Hint
        }, 0, 1);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // 提权按钮隐藏时这一列自动收成 0 宽，不占位。
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        header.Controls.Add(new Label { Text = "Codex / Claude Code 窗口", AutoSize = true, Margin = new Padding(0, 7, 0, 0) }, 0, 0);
        header.Controls.Add(_elevate, 1, 0);
        header.Controls.Add(_toggleAll, 2, 0);
        header.Controls.Add(_refresh, 3, 0);
        layout.Controls.Add(header, 0, 2);
        layout.Controls.Add(_sessions, 0, 3);

        _start.BackColor = Color.FromArgb(38, 87, 214);
        _start.ForeColor = Color.White;
        _start.FlatStyle = FlatStyle.Flat;
        _start.FlatAppearance.BorderSize = 0;
        layout.Controls.Add(Section("自动发送（Codex 续跑 / Claude Code 错误重试）", BuildKeepAlivePanel()), 0, 4);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0), WrapContents = false };
        actions.Controls.AddRange([_start, _stop, _count, _resumedCount, _returnedCount]);
        layout.Controls.Add(actions, 0, 5);
        var statusRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status.AutoSize = false;
        _status.AutoEllipsis = true;
        _status.Dock = DockStyle.Fill;
        _status.Margin = new Padding(0, 8, 0, 0);
        statusRow.Controls.Add(_status, 0, 0);
        statusRow.Controls.Add(_notify, 1, 0);
        layout.Controls.Add(statusRow, 0, 6);
        layout.Controls.Add(Section("终端当前画面（列表中选中的会话）", _preview), 0, 7);
        layout.Controls.Add(Section("操作记录", _log), 0, 8);
        layout.Controls.Add(new Label
        {
            Text = "仅 Codex 审批会自动通过。Claude Code 内置重试、权限菜单或输入框有草稿时不发送。共用文本池和次数上限。关闭本工具即停止。",
            Dock = DockStyle.Fill, ForeColor = Hint, Padding = new Padding(0, 10, 0, 0)
        }, 0, 9);
        Controls.Add(layout);

        _sessions.AccessibleName = "需要监听的 Codex 和 Claude Code 会话列表，勾选以监听";
        _preview.AccessibleName = "终端当前画面，只读";
        _log.AccessibleName = "自动确认操作记录，只读";
        _elevate.AccessibleName = "以管理员身份重启，用于连接权限更高的 CLI 会话";
        _keepAlive.AccessibleName = "Codex 普通空闲或其他响应异常时自动续跑开关";
        _highDemandRetry.AccessibleName = "高负载回合失败后等待 1 秒，自动发送下方配置内容；空闲阈值为 0 时立即发送";
        _claudeRetry.AccessibleName = "Claude Code 内置重试结束，临时错误最终失败后续发一次；不自动审批或普通空闲续跑";
        _idleSeconds.AccessibleName = "空闲阈值，单位秒，支持小数；0 表示空闲或异常时立即发送";
        _keepAliveText.AccessibleName = "续跑文本池，一行一条，每次随机取一条发送";
        _resetPrompts.AccessibleName = "把续跑文本池恢复为默认内容";
        _maxSends.AccessibleName = "单会话最大续跑次数，0 表示不限";
        _notify.AccessibleName = "回合成功返回时是否弹系统通知，仅在窗口不在前台时弹出";
        _preview.Text = "开始监听后，这里显示列表中选中会话的画面。";
    }

    /// <summary>各发送规则独立开关，共用下方文本池和次数上限。</summary>
    /// <returns>已排布好的设置面板。</returns>
    private Control BuildKeepAlivePanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        // 说明行按文字实际高度留足，别让描述被裁掉半行。
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(BuildKeepAliveTiming(), 0, 0);
        panel.Controls.Add(_highDemandRetry, 0, 1);
        panel.Controls.Add(_claudeRetry, 0, 2);
        panel.Controls.Add(new Label
        {
            Text = "发送内容（一行一条，轮换发送；只填一行时固定发送。空闲或异常时均使用下方内容）",
            AutoSize = true, ForeColor = Hint, Margin = new Padding(4, 2, 4, 2)
        }, 0, 3);
        panel.Controls.Add(BuildKeepAlivePrompts(), 0, 4);
        return panel;
    }

    /// <summary>上排：开关、空闲阈值、次数上限。</summary>
    /// <returns>已排布好的一行控件。</returns>
    private Control BuildKeepAliveTiming()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, Margin = Padding.Empty };
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        // 文字列交给 AutoSize：换字体或改 DPI 时不会把标签截断；只有数字框固定宽度。
        foreach (var column in new[]
                 {
                     SizeType.AutoSize, SizeType.AutoSize, SizeType.Absolute,
                     SizeType.AutoSize, SizeType.Absolute, SizeType.AutoSize
                 })
            row.ColumnStyles.Add(new ColumnStyle(column, column == SizeType.Absolute ? 72 : 0));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles[2].Width = 180;

        row.Controls.Add(_keepAlive, 0, 0);
        row.Controls.Add(Caption("空闲超过"), 1, 0);
        row.Controls.Add(_idleSeconds, 2, 0);
        row.Controls.Add(Caption("秒（0 立即），上限"), 3, 0);
        row.Controls.Add(_maxSends, 4, 0);
        row.Controls.Add(Caption("次（0 不限）"), 5, 0);
        return row;
    }

    /// <summary>下排：文本池编辑框与"恢复默认"按钮。</summary>
    /// <returns>已排布好的一行控件。</returns>
    private Control BuildKeepAlivePrompts()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        row.Controls.Add(_keepAliveText, 0, 0);
        // 按钮顶部对齐输入框首行，不随输入框拉高。
        var holder = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _resetPrompts.Dock = DockStyle.Top;
        _resetPrompts.Height = 30;
        holder.Controls.Add(_resetPrompts);
        row.Controls.Add(holder, 1, 0);
        return row;
    }

    /// <summary>保活面板里的说明文字。</summary>
    /// <param name="text">说明内容。</param>
    /// <returns>统一样式的说明标签。</returns>
    private static Label Caption(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Hint, Margin = new Padding(4, 7, 4, 0)
    };

    private void WireEvents()
    {
        _refresh.Click += async (_, _) => await RefreshWindows();
        _elevate.Click += (_, _) => RestartElevated();
        _resetPrompts.Click += (_, _) =>
            _keepAliveText.Text = string.Join(Environment.NewLine, KeepAliveOptions.DefaultPrompts);
        _toggleAll.Click += (_, _) => ToggleAll();
        _start.Click += (_, _) => StartMonitoring();
        _stop.Click += (_, _) => StopMonitoring("已停止");
        // 监听期间不允许改动监听集合，否则会与在途轮询产生竞态。
        _sessions.ItemCheck += (_, e) =>
        {
            if (_monitors is not null && !_syncingChecks)
                e.NewValue = e.CurrentValue;
        };
        _sessions.ItemChecked += (_, _) =>
        {
            if (!_syncingChecks)
                UpdateStartState();
        };
        _sessions.SelectedIndexChanged += (_, _) => UpdatePreview();
        _timer.Tick += async (_, _) => await Poll();
        Shown += async (_, _) =>
        {
            if (_shown)
                return;
            _shown = true;
            await RefreshWindows();
        };
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
                HideToTray();
            else
                _trayRestoreState = WindowState;
        };
        FormClosing += (_, _) => { _closing = true; StopMonitoring("已停止"); };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing = true;
            _monitors?.StopAll();
            _monitors = null;
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            _ownedTrayIcon?.Dispose();
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>创建一个只读文本框。</summary>
    /// <param name="name">控件名，供测试与诊断按名查找。</param>
    /// <returns>只读文本框。</returns>
    private static TextBox ReadOnlyBox(string name) => new()
    {
        Name = name,
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Dock = DockStyle.Fill, BackColor = Color.FromArgb(246, 248, 251), BorderStyle = BorderStyle.None,
        Font = new Font("Consolas", 10)
    };

    private static Control Section(string title, Control content)
    {
        var section = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 12), Margin = new Padding(0, 0, 0, 12) };
        section.Controls.Add(content);
        return section;
    }

    /// <summary>列表中某行对应的会话。</summary>
    /// <param name="item">列表行。</param>
    /// <returns>该行绑定的终端会话。</returns>
    private static TerminalTarget TargetOf(ListViewItem item) => (TerminalTarget)item.Tag!;

    /// <summary>当前勾选的会话行。</summary>
    private IEnumerable<ListViewItem> CheckedRows => _sessions.Items.Cast<ListViewItem>().Where(item => item.Checked);

    /// <summary>重新扫描 CLI 会话，保留原有勾选状态。</summary>
    private async Task RefreshWindows()
    {
        var checkedIds = CheckedRows.Select(item => TargetOf(item).ProcessId).ToHashSet();
        var selectedId = _sessions.SelectedItems.Count > 0 ? TargetOf(_sessions.SelectedItems[0]).ProcessId : (int?)null;
        _refresh.Enabled = _toggleAll.Enabled = _start.Enabled = false;
        UpdateTrayMenu();
        _status.Text = "正在查找 CLI 窗口…";
        try
        {
            var scan = await Task.Run(TerminalConsole.FindCliSessions);
            if (_closing)
                return;
            _syncingChecks = true;
            _sessions.BeginUpdate();
            try
            {
                _sessions.Items.Clear();
                foreach (var target in scan.Targets)
                {
                    var row = new ListViewItem($"{target.CliName} · {target.Title}") { Tag = target };
                    row.SubItems.Add(target.ProcessId.ToString());
                    row.SubItems.Add("未监听");
                    row.SubItems.Add(_confirms.TryGetValue(target.ProcessId, out var done) ? done.ToString() : "0");
                    row.SubItems.Add(_resumes.TryGetValue(target.ProcessId, out var kept) ? kept.ToString() : "0");
                    // 刷新前已勾选的会话保持勾选；新出现的会话默认不勾选。
                    row.Checked = checkedIds.Contains(target.ProcessId);
                    _sessions.Items.Add(row);
                    if (target.ProcessId == selectedId)
                        row.Selected = true;
                }
            }
            finally
            {
                _sessions.EndUpdate();
                _syncingChecks = false;
            }
            var elevated = TerminalConsole.IsElevated();
            _status.Text = DescribeScan(scan, elevated);
            // 权限不足要能一键解决；已经是管理员还连不上就不该再劝提权。
            _elevate.Visible = scan.NeedsElevation > 0 && !elevated;
            // 先写逐进程的技术细节，便于排查；建议写在最后——日志框自动滚到底，
            // 顺序反了用户看到的就只有那条看不懂的"拒绝访问"。
            foreach (var reason in scan.Skipped)
                Log(reason);
            foreach (var advice in Advise(scan, elevated))
                Log(advice);
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                _status.Text = "读取窗口失败";
                Log(ex.Message);
            }
        }
        finally
        {
            if (!_closing)
            {
                _refresh.Enabled = _toggleAll.Enabled = true;
                UpdateStartState();
                UpdatePreview();
            }
        }
    }

    /// <summary>把扫描结果汇总成一行状态文本。</summary>
    /// <param name="scan">本次扫描结果。</param>
    /// <param name="elevated">本进程是否已以管理员身份运行。</param>
    /// <returns>状态栏文本。</returns>
    internal static string DescribeScan(SessionScan scan, bool elevated)
    {
        // 已提权仍连不上时不提"权限"，避免把用户引向没用的操作。
        var blocked = elevated ? 0 : scan.NeedsElevation;
        var suffix = blocked > 0 ? $"，另有 {blocked} 个因权限不足无法连接" : "";
        return scan.Targets.Count > 0
            ? $"找到 {scan.Targets.Count} 个 CLI 会话{suffix} · 未启动"
            : blocked > 0
                ? $"有 {blocked} 个 CLI 会话因权限不足无法连接"
                : "未找到可读取的 Codex / Claude Code CLI 窗口";
    }

    /// <summary>根据扫描结果给出可执行的建议，逐条写入操作记录。</summary>
    /// <param name="scan">本次扫描结果。</param>
    /// <param name="elevated">本进程是否已以管理员身份运行。</param>
    /// <returns>建议文本，按顺序记录。</returns>
    internal static IEnumerable<string> Advise(SessionScan scan, bool elevated)
    {
        if (scan.NeedsElevation > 0)
            yield return elevated
                ? $"有 {scan.NeedsElevation} 个会话连接被拒绝。本工具已是管理员权限，" +
                  "请确认这些会话是否属于其它用户账户。"
                : $"有 {scan.NeedsElevation} 个 CLI 会话运行在权限更高的终端里（通常是以管理员身份打开的 " +
                  "Windows Terminal），普通权限无法连接。点右上角「以管理员身份重启」即可看到它们。";
        if (scan.Targets.Count == 0 && scan.NeedsElevation == 0)
            yield return "请在 Windows Terminal 中运行 codex 或 claude（原生版本），然后刷新窗口。";
    }

    /// <summary>以管理员身份重启本工具。UAC 被取消时保持当前会话不变。</summary>
    private void RestartElevated()
    {
        var path = Environment.ProcessPath;
        if (path is null)
        {
            Log("无法定位本程序路径，请手动右键以管理员身份运行。");
            return;
        }
        try
        {
            // runas 必须配合 UseShellExecute，否则不会弹 UAC。
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // 用户在 UAC 对话框点了"否"会走到这里；当前会话不受影响。
            Log($"提权启动未完成：{ex.Message}");
            return;
        }
        _closing = true;
        StopMonitoring("已停止");
        Close();
    }

    /// <summary>全选或全不选。监听期间不可用。</summary>
    private void ToggleAll()
    {
        if (_monitors is not null)
            return;
        var check = _sessions.CheckedItems.Count < _sessions.Items.Count;
        _syncingChecks = true;
        try
        {
            foreach (ListViewItem item in _sessions.Items)
                item.Checked = check;
        }
        finally
        {
            _syncingChecks = false;
        }
        _toggleAll.Text = check ? "全不选" : "全选";
        UpdateStartState();
    }

    /// <summary>按勾选项是否存在，更新开始按钮可用状态。</summary>
    private void UpdateStartState()
    {
        _start.Enabled = !_closing && _refresh.Enabled && _monitors is null && _sessions.CheckedItems.Count > 0;
        UpdateTrayMenu();
    }

    /// <summary>
    /// 按界面当前输入组装保活配置。设置在监听期间锁定，所以这份配置在一次
    /// 监听过程中保持不变；要改参数就先停止再开始。
    /// </summary>
    /// <returns>所有开关均关闭或文本池为空时返回 null。</returns>
    private KeepAliveOptions? CurrentKeepAlive()
    {
        var prompts = ParsePrompts(_keepAliveText.Text);
        return (!_keepAlive.Checked && !_highDemandRetry.Checked && !_claudeRetry.Checked) || prompts.Count == 0
            ? null
            : new KeepAliveOptions(TimeSpan.FromTicks((long)(_idleSeconds.Value * TimeSpan.TicksPerSecond)), prompts, (int)_maxSends.Value,
                _keepAlive.Checked, _highDemandRetry.Checked, _claudeRetry.Checked);
    }

    /// <summary>把文本池编辑框的内容解析成文本列表：一行一条，忽略空行。</summary>
    /// <param name="text">编辑框原文。</param>
    /// <returns>去掉空行后的文本列表。</returns>
    internal static List<string> ParsePrompts(string text) =>
        text.Replace("\r", "").Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length != 0).ToList();

    /// <summary>监听期间锁定保活设置，避免同一轮监听里参数来回变化。</summary>
    /// <param name="enabled">true 表示允许编辑。</param>
    private void SetKeepAliveEditable(bool enabled) =>
        _keepAlive.Enabled = _highDemandRetry.Enabled = _claudeRetry.Enabled = _idleSeconds.Enabled = _keepAliveText.Enabled =
            _maxSends.Enabled = _resetPrompts.Enabled = enabled;

    private void StartMonitoring()
    {
        if (_closing || !_refresh.Enabled || _monitors is not null)
            return;
        var targets = CheckedRows.Select(TargetOf).ToList();
        if (targets.Count == 0)
            return;
        var keepAlive = CurrentKeepAlive();
        _monitors = new MonitorSet(targets, keepAlive);
        _toggleAll.Enabled = _refresh.Enabled = _start.Enabled = _elevate.Enabled = false;
        SetKeepAliveEditable(false);
        _stop.Enabled = true;
        _status.Text = $"监听中 · {targets.Count} 个会话";
        _status.ForeColor = Color.FromArgb(30, 125, 76);
        foreach (var row in CheckedRows)
            row.SubItems[2].Text = TargetOf(row).Kind == TerminalKind.ClaudeCode ? "等待响应异常" : "等待审批提示";
        Log($"开始监听 {targets.Count} 个会话：{string.Join("、", targets.Select(t => $"PID {t.ProcessId}"))}");
        Log(keepAlive is null
            ? "自动发送未启用。"
            : (keepAlive.IdleEnabled ? $"空闲续跑：{_idleSeconds.Value} 秒（0 表示立即发送）；" : "空闲续跑关闭；") +
              (keepAlive.HighDemandEnabled
                  ? $"高负载续发：内置重试结束、回合失败后{(keepAlive.IdleThreshold == TimeSpan.Zero ? "立即" : "等待 1 秒")}发送；"
                  : "高负载续发关闭；") +
              (keepAlive.ClaudeRetryEnabled ? "Claude Code 临时错误重试开启（内置重试结束后每次失败仅续发一次）；" : "Claude Code 错误重试关闭；") +
              $"发送内容共 {keepAlive.Prompts.Count} 条；" +
              (keepAlive.MaxSends > 0 ? $"每个会话最多 {keepAlive.MaxSends} 次。" : "不限次数。"));
        _timer.Start();
        UpdateTrayMenu();
    }

    private void StopMonitoring(string status)
    {
        _timer.Stop();
        var wasRunning = _monitors is not null;
        _monitors?.StopAll();
        _monitors = null;
        _stop.Enabled = false;
        _toggleAll.Enabled = _refresh.Enabled = _elevate.Enabled = true;
        _status.Text = status;
        _status.ForeColor = Hint;
        if (!_closing)
        {
            SetKeepAliveEditable(true);
            foreach (ListViewItem row in _sessions.Items)
                row.SubItems[2].Text = "未监听";
            UpdateStartState();
        }
        if (wasRunning)
            Log(status);
        UpdateTrayMenu();
    }

    private async Task Poll()
    {
        if (_polling || _monitors is null)
            return;
        _polling = true;
        var monitors = _monitors;
        try
        {
            // 终端访问由 TerminalConsole 的进程级锁串行化，这里顺序轮询所有会话。
            var polls = await Task.Run(monitors.PollAll);
            if (_closing || _monitors != monitors)
                return;
            foreach (var poll in polls)
                Apply(monitors, poll);
            if (monitors.Count == 0 && _monitors == monitors)
                StopMonitoring("所有会话均已停止");
            else
                UpdatePreview();
        }
        catch (Exception ex)
        {
            if (!_closing && _monitors == monitors)
            {
                StopMonitoring("读取或确认失败，监听已停止");
                Log(ex.Message);
            }
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>把单个会话的轮询结果反映到列表行、计数和日志。</summary>
    /// <param name="monitors">产生该结果的监听集合。</param>
    /// <param name="poll">单个会话的轮询结果。</param>
    internal void Apply(MonitorSet monitors, SessionPoll poll)
    {
        var target = poll.Monitor.Target;
        var row = _sessions.Items.Cast<ListViewItem>().FirstOrDefault(item => TargetOf(item).ProcessId == target.ProcessId);
        if (poll.Error is not null)
        {
            // 单个会话失败只摘掉它自己，其余会话继续监听。
            monitors.Remove(poll.Monitor);
            if (row is not null)
            {
                row.SubItems[2].Text = "已停止（出错）";
                _syncingChecks = true;
                row.Checked = false;
                _syncingChecks = false;
            }
            Log($"PID {target.ProcessId} 监听已停止：{poll.Error.Message}");
            UpdateTrayMenu();
            return;
        }

        var result = poll.Result!;
        _screens[target.ProcessId] = result.Screen;
        if (result.Notice is not null)
            Log($"PID {target.ProcessId}：{ShortStatus(result.Notice)}");
        if (row is not null)
            row.SubItems[2].Text = ShortStatus(result.Status);
        if (result.Sent)
        {
            _confirms[target.ProcessId] = _confirms.GetValueOrDefault(target.ProcessId) + 1;
            if (row is not null)
                row.SubItems[3].Text = _confirms[target.ProcessId].ToString();
            _count.Text = $"已确认 {++_confirmed} 次";
            Log($"PID {target.ProcessId}（{target.Title}）识别到审批菜单，已发送 y（本次允许）。");
        }
        else if (result.KeptAlive)
        {
            _resumes[target.ProcessId] = result.Resumed;
            if (row is not null)
                row.SubItems[4].Text = result.Resumed.ToString();
            _resumedCount.Text = $"已续跑 {++_resumed} 次";
            Log($"PID {target.ProcessId}（{target.Title}）{ShortStatus(result.Status)}，已发送「{result.Prompt}」。");
        }
        if (result.Returned)
        {
            _returnedCount.Text = $"已返回 {++_returned} 次";
            var summary = result.Summary ?? "已返回回答";
            Log($"PID {target.ProcessId}（{target.Title}）已返回回答：{summary}");
            if (_notify.Checked && IsInBackground)
            {
                // 点击仍在显示的通知时，不能跳到后来未发通知的会话。
                _lastReturnPid = target.ProcessId;
                ShowReturnNotice($"{target.CliName} 已返回：{target.Title}",
                    $"{summary}{Environment.NewLine}PID {target.ProcessId} · {DateTime.Now:HH:mm:ss}");
            }
        }
    }

    private bool IsInBackground =>
        !Visible || WindowState == FormWindowState.Minimized || Form.ActiveForm != this;

    /// <summary>测试使用注入的收集器，实际运行使用系统托盘通知。</summary>
    private void ShowReturnNotice(string title, string body)
    {
        if (_notifyReturn is not null)
            _notifyReturn(Fit(title, 63), Fit(body, 255));
        else if (_tray.Visible)
            _tray.ShowBalloonTip(5000, Fit(title, 63), Fit(body, 255), ToolTipIcon.Info);
    }

    private static string Fit(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>去掉状态文本里的"监听中 · "前缀，适配列宽。</summary>
    /// <param name="status">监听器返回的完整状态。</param>
    /// <returns>适合在列表中显示的短状态。</returns>
    private static string ShortStatus(string status) =>
        status.StartsWith("监听中 · ") ? status["监听中 · ".Length..] : status;

    /// <summary>把当前选中会话的画面显示到预览框。</summary>
    private void UpdatePreview()
    {
        if (_sessions.SelectedItems.Count == 0)
        {
            if (_sessions.Items.Count > 0 && _preview.Text.Length == 0)
                _preview.Text = "在上方列表中选择一个会话即可查看它的画面。";
            return;
        }
        var screen = _screens.TryGetValue(TargetOf(_sessions.SelectedItems[0]).ProcessId, out var text)
            ? text.TrimEnd()
            : "该会话尚未读取到画面。勾选并开始监听后显示。";
        if (_preview.Text == screen)
            return;
        _preview.Text = screen;
        _preview.SelectionStart = _preview.TextLength;
        _preview.ScrollToCaret();
    }

    private void Log(string message)
    {
        if (_log.Lines.Length >= 200)
            _log.Lines = _log.Lines.TakeLast(150).ToArray();
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}
