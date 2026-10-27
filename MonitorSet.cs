namespace CodexAutoYes;

/// <summary>单个会话一次轮询的结果。某个会话失败不影响其余会话。</summary>
/// <param name="Monitor">产生该结果的会话监听器。</param>
/// <param name="Result">轮询结果；失败时为 null。</param>
/// <param name="Error">失败原因；成功时为 null。</param>
internal sealed record SessionPoll(ConsoleMonitor Monitor, MonitorResult? Result, Exception? Error);

/// <summary>
/// 同时监听多个 Codex 会话。
/// 终端访问要经过 <see cref="TerminalConsole"/> 的进程级锁串行化，
/// 因此这里按顺序轮询，不额外开并发。
/// </summary>
internal sealed class MonitorSet
{
    /// <summary>保护 <see cref="_monitors"/> 的结构性改动，轮询在后台线程、停止在界面线程。</summary>
    private readonly object _sync = new();
    private readonly List<ConsoleMonitor> _monitors;

    /// <summary>按给定会话建立监听集合。成员固定，只会减少不会增加。</summary>
    /// <param name="targets">要监听的终端会话。</param>
    /// <param name="keepAlive">空闲续跑配置，所有会话共用同一份；null 表示不启用保活。</param>
    internal MonitorSet(IEnumerable<TerminalTarget> targets, KeepAliveOptions? keepAlive = null) =>
        _monitors = targets.Select(target => new ConsoleMonitor(target, keepAlive)).ToList();

    /// <summary>仍在监听的会话数。</summary>
    internal int Count
    {
        get { lock (_sync) return _monitors.Count; }
    }

    /// <summary>依次轮询所有会话，单个会话抛出的异常被收集而不向外传播。</summary>
    /// <returns>每个会话各一条结果，顺序与当前成员顺序一致。</returns>
    internal List<SessionPoll> PollAll()
    {
        ConsoleMonitor[] snapshot;
        lock (_sync)
            snapshot = _monitors.ToArray();
        // 轮询不持锁：单轮可能耗时数十毫秒，持锁会阻塞界面线程的停止操作。
        var polls = new List<SessionPoll>(snapshot.Length);
        foreach (var monitor in snapshot)
        {
            try
            {
                polls.Add(new SessionPoll(monitor, monitor.Poll(), null));
            }
            catch (Exception ex)
            {
                polls.Add(new SessionPoll(monitor, null, ex));
            }
        }
        return polls;
    }

    /// <summary>移除并停止一个会话，通常因为它的终端已不可用。</summary>
    /// <param name="monitor">要移除的会话监听器。</param>
    internal void Remove(ConsoleMonitor monitor)
    {
        monitor.Stop();
        lock (_sync)
            _monitors.Remove(monitor);
    }

    /// <summary>
    /// 停止所有会话。返回后任何在途轮询都不会再发送按键：
    /// 每个 <see cref="ConsoleMonitor.Stop"/> 都会取得该会话自身的锁，
    /// 而 <see cref="ConsoleMonitor.Poll"/> 全程持有同一把锁。
    /// </summary>
    internal void StopAll()
    {
        ConsoleMonitor[] snapshot;
        lock (_sync)
        {
            snapshot = _monitors.ToArray();
            _monitors.Clear();
        }
        foreach (var monitor in snapshot)
            monitor.Stop();
    }
}
