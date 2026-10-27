# 托盘最小化与回合返回通知 · 设计

- 日期：2026-10-27
- 状态：已确认
- 范围：`CodexAutoYes` WinForms 工具（.NET 10 / Windows）

## 目标

1. 主窗口可以最小化到系统托盘，监听在后台继续运行；从托盘恢复窗口、启停监听、退出。
2. 某个被监听的会话成功返回一轮回答时，弹出系统消息通知；通知只在窗口处于后台时出现，点击可回到该会话。

## 非目标

- 不做通知/托盘相关设置的持久化（与当前程序一致，每次启动回到默认值）。
- 不监听或解析 Codex 的结构化事件，判定只基于终端画面。
- 不对报错、高负载、重连单独发通知；它们仍然只写操作记录。
- 不引入第三方托盘/通知库，不改动自动确认与续跑的核心逻辑。

## 架构

```
Codex 终端
   │  TerminalConsole.Read（既有，进程级锁）
   ▼
ConsoleMonitor.Poll（后台线程）
   ├─ ApprovalGate / KeepAliveWatch（既有）
   └─ TurnWatcher.Observe（新增，纯逻辑）
   ▼
MonitorResult { …, Returned, Summary }
   ▼
MainForm.Apply（UI 线程）
   ├─ 列表 / 计数 / 操作记录（既有）
   └─ 后台且开关开启 → NotifyIcon.ShowBalloonTip
                              │ 点击
                              ▼
                        RestoreFromTray + 选中会话行
```

托盘与通知都属于表现层，不反向依赖监听逻辑；`TurnWatcher` 不依赖 WinForms，可脱离真实终端单测。

## 组件

### 1. 托盘（`MainForm` 内）

- `NotifyIcon _tray`，构造时创建，`Visible = true`，程序整个生命周期常驻通知区。
  - 图标：`Icon.ExtractAssociatedIcon(Environment.ProcessPath)`；返回 `null` 时回退 `SystemIcons.Application`。
  - 提示文本：未监听为“Codex 自动确认”，监听中为“Codex 自动确认 · 监听 N 个会话”。
- 最小化：`Resize` 事件中检测 `WindowState == FormWindowState.Minimized`，调用 `HideToTray()`：
  - 记录隐藏前的窗口状态（Normal / Maximized）到 `_trayRestoreState`；
  - `ShowInTaskbar = false`；`Hide()`；
  - 不停止 `_timer`，监听与轮询照常。
- 恢复：`RestoreFromTray()`：
  - `ShowInTaskbar = true`；`Show()`；`WindowState = _trayRestoreState`；`Activate()`；`BringToFront()`。
  - 恢复过程不得重新扫描窗口列表。`Shown` 事件里的首次发现用一次性标志保护，避免把监听中的会话行清空。
- 托盘菜单（`ContextMenuStrip`）：显示主窗口、开始监听、停止监听、退出。
  - 开始/停止项的 `Enabled` 与主界面按钮同步（新增 `UpdateTrayMenu()`，在 `UpdateStartState`、`StartMonitoring`、`StopMonitoring` 中调用）。
  - “退出”调用 `Close()`，沿用现有的“先停止监听再关闭”路径。
- 关闭按钮 X 保持现状：停止监听并退出。
- 释放：`Dispose(bool disposing)` 中 `_tray.Visible = false; _tray.Dispose();`，保证测试和异常退出后通知区不残留图标。

### 2. 回合返回检测（新文件 `TurnWatcher.cs`）

```csharp
/// <summary>一次成功返回：摘要用于通知正文。</summary>
internal sealed record TurnReturn(string Summary);

internal sealed class TurnWatcher
{
    /// <summary>喂入一帧画面；本帧构成一次成功返回时返回摘要，否则 null。</summary>
    internal TurnReturn? Observe(string screen);
}
```

状态：

| 字段 | 含义 |
| --- | --- |
| `_sawBusy` | 当前这一回合是否见过执行态；没见过的空闲不算返回 |
| `_idleSignature` | 退出执行态后用于判断画面稳定的签名 |
| `_idlePolls` | 该签名连续出现的次数，达到 3 判定返回 |

判定规则：

1. 画面为空串（正在调整终端尺寸、读取失败）时直接返回 `null`，不改变任何状态。
2. 执行态 = `KeepAliveWatch.LooksBusy(screen)` 为真，或 `ApprovalPrompt.Find(screen) is not null`。命中则 `_sawBusy = true`，清空稳定计数。
3. 非执行态且 `_sawBusy` 为假：静默返回 `null`（启动时停在输入框的会话不会误报）。
4. 非执行态且 `_sawBusy` 为真：比较 `KeepAliveWatch.Normalize(screen)`。
   - 与上一帧不同：记为新的稳定基线，计数归 1，返回 `null`。
   - 相同：计数 +1；不足 3 时返回 `null`。
   - 达到 3：检查 `KeepAliveWatch.FooterHasProblem(signature)`。有报错 / 高负载 / 重连 → 不算成功，重置 `_sawBusy`，返回 `null`；否则产出 `TurnReturn(摘要)` 并重置，等下一次执行态。
5. 判定后无论成功与否都清空 `_sawBusy`，保证一个回合最多通知一次；续跑发出新文本、执行提示重新出现时开始下一回合。

摘要提取：

- 在归一化画面上取尾部 12 条非空行；
- 丢弃界面装饰：以 `›` / `>` 开头的输入提示、`? for shortcuts`、`send with enter`、`Press enter to …`、纯边框/分隔线、`Token usage` 之类统计行；
- 取剩余内容合并成一行（连续空白折叠），超过 120 字截断并补 `…`；
- 没有可用内容时用 `"已返回回答"` 作为兜底摘要。

为复用规则，对 `KeepAlive.cs` 做两处最小改动：

- `KeepAliveWatch.LooksBusy` 由 `private` 改为 `internal`；
- 新增 `internal static bool FooterHasProblem(string signature)`，内部沿用 `ResponseError` / `HighDemand` / `Reconnect` 及“尾部 12 条非空行”的既有逻辑。

### 3. 监听结果扩展

`MonitorResult` 增加两个带默认值的字段：

```csharp
internal sealed record MonitorResult(
    string Status, string Screen, bool Sent, bool KeptAlive = false, int Resumed = 0,
    string? Prompt = null, string? Notice = null,
    bool Returned = false, string? Summary = null);
```

`ConsoleMonitor.Poll` 在读完画面后立即调用 `_turn.Observe(screen)`，各条返回路径（已发送 y、空闲、续跑、续跑被拒）都带上 `Returned/Summary`。`MonitorSet`、`SessionPoll` 不需要改动。

### 4. MainForm 集成

- 新增复选框 `_notify`（`Name = "notifyOnReturn"`，文本“回合返回时通知（仅后台时弹窗）”，默认勾选）。放在状态栏同一行，监听期间保持可改，不参与 `SetKeepAliveEditable`。
- 新增计数标签 `_returnedCount`（`Name = "returned"`，文本“已返回 0 次”），排在“已续跑”之后。
- `Apply` 中处理 `result.Returned`：
  - `_returnedCount.Text = $"已返回 {++_returned} 次"`；
  - 操作记录追加 `PID x（标题）已返回回答：<摘要>`；
  - 满足通知条件时调用通知：`_notify.Checked && IsInBackground`。
- 通知条件 `IsInBackground`：`!Visible || WindowState == FormWindowState.Minimized || Form.ActiveForm != this`。
- 通知内容：
  - 标题：`Codex 已返回：<会话标题>`（截断到 63 字符）；
  - 正文：`<摘要>` + 换行 + `PID <pid> · <HH:mm:ss>`（截断到 255 字符）；
  - 图标：`ToolTipIcon.Info`，显示 5 秒。
- 通知出口做成构造参数，默认走托盘气泡：

```csharp
internal MainForm(Action<string, string>? notify = null)
```

  `notify` 为 `null` 时执行 `_tray.ShowBalloonTip(5000, title, body, ToolTipIcon.Info)`；测试注入收集器，避免真的弹系统通知。
- 点击通知：记录最后一次返回的 PID（`_lastReturnPid`），`BalloonTipClicked` 中 `RestoreFromTray()` 并选中对应会话行（`EnsureVisible`），选中后既有的 `UpdatePreview` 会显示该会话最后的画面。行已不存在时只恢复窗口。
- 暴露给测试的只读入口：`internal NotifyIcon Tray => _tray;`、`internal void RestoreFromTray()`（恢复逻辑本身，双击托盘与菜单共用）。

## 错误处理

- 托盘图标：`ExtractAssociatedIcon` 可能返回 `null` 或抛 `ArgumentException`，统一回退 `SystemIcons.Application`；仍失败时不创建托盘，但最小化照常收进任务栏，通知降级为只写日志。
- `TurnWatcher` 只做字符串比较，不抛异常；即使抛也由 `MonitorSet.PollAll` 现有的单会话异常隔离兜住，不会影响其它会话。
- 通知被系统关闭时 `ShowBalloonTip` 静默无效；操作记录与计数仍然完整，信息不丢。
- 恢复窗口不触发重新扫描；扫描失败/关闭流程沿用既有逻辑。

## 测试计划

沿用 `tests/Program.cs` 的手写用例，新增：

1. `turn: successful return is reported once with a summary`（纯逻辑）
   - 空闲启动不返回；`esc to interrupt` 后连续 3 帧稳定空闲返回一次；继续空闲不重复；摘要不含 `? for shortcuts` 等装饰。
2. `turn: errors, reconnects and approval menus are not returns`（纯逻辑）
   - 尾部为 `rate limit` / 高负载 / reconnecting 时不返回；事件重连结束后仍不返回。
   - 审批菜单出现 → 菜单消失 → 执行提示 → 空闲：只返回一次。
3. `turn: summary is trimmed and capped`（纯逻辑）
   - 长回答截断到 120 字；纯装饰画面回退为“已返回回答”。
4. `real console: turn fixture reports one successful return`（真实控制台）
   - 夹具新增 `turn` 模式：写一行 `▌ Working (esc to interrupt)`，约 1.5 秒后追加一段带回答的空闲画面；用 `ConsoleMonitor` 轮询断言 `Returned` 恰好一次、`Summary` 含回答关键句。
   - 夹具的空闲画面必须在执行提示之后至少写 5 条非空行，把旧的执行提示挤出 `LooksBusy` 只检查的尾部 5 行；否则测试会因为一直判定为“执行中”永远等不到返回。
5. `tray: minimize hides, menu restores and actions stay in sync`（GUI）
   - 托盘图标 `Visible`；菜单 4 项；`WindowState = Minimized` 后 `!form.Visible && !form.ShowInTaskbar`；`RestoreFromTray()` 回到任务栏；未勾选会话时“开始监听”菜单项禁用。
6. `tray: notification fires only in background`（GUI）
   - 注入收集器，夹具会话返回后收集器恰好收到一次，标题含会话标题、正文含摘要。
   - 另起一次会话并事先取消勾选 `notifyOnReturn`：收集器不再收到，但操作记录仍写入、`已返回` 计数仍增加。

既有 26 项用例全部保留，必须继续通过。

## 风险与取舍

- 开续跑时每轮成功回答都会通知，频率等于续跑频率；用户可随时取消勾选，且窗口在前台时不打扰。
- 同一轮多个会话同时返回，Windows 只显示最后一个气泡；日志与计数不丢，点击只定位最后返回的会话。
- 判定依赖 Codex CLI 的底部提示文案（`esc to interrupt`、审批菜单、报错行）。CLI 改版时需要同步更新 `Busy` / `ResponseError` / `HighDemand` / `Reconnect` 规则，这是现有保活逻辑已经承担的同一类风险。
- 摘要取的是画面尾部文本，可能包含命令行回显或工具输出片段；只用于提示，不参与任何判定。
