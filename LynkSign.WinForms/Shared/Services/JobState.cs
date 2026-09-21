namespace LynkSign.Services;

/// <summary>
/// 调度器与最近一次运行的共享状态。
///
/// <para>
/// 谁在写：<see cref="SignJobRunner"/> 在开跑/收尾时调 <see cref="MarkStarted"/> /
/// <see cref="MarkFinished"/>。谁在读：窗体据此把顶部状态胶囊和「上次运行」一行刷出来。
/// 所以它是<b>纯内存对象</b>，没有落盘、也没有网络出口 ——
/// 所有读写都加锁，因为调度线程与 UI 线程会同时碰它。
/// </para>
///
/// <para>
/// 历史：这个类原先还给 Web 版的 <c>/api/status</c> 提供 JSON，
/// 于是每个属性都挂着 <c>[JsonPropertyName]</c> 的 snake_case 名。
/// Web 版删除后没有任何序列化调用方，那些特性只是误导，已移除。
/// </para>
/// </summary>
public sealed class JobState
{
    private readonly object _lock = new();

    private bool _running;
    private string? _lastStart;
    private string? _lastFinish;
    private int? _lastRc;
    private string? _lastLog;

    public bool Running
    {
        get { lock (_lock) return _running; }
        set { lock (_lock) _running = value; }
    }

    public string? LastStart
    {
        get { lock (_lock) return _lastStart; }
        set { lock (_lock) _lastStart = value; }
    }

    public string? LastFinish
    {
        get { lock (_lock) return _lastFinish; }
        set { lock (_lock) _lastFinish = value; }
    }

    public int? LastRc
    {
        get { lock (_lock) return _lastRc; }
        set { lock (_lock) _lastRc = value; }
    }

    public string? LastLog
    {
        get { lock (_lock) return _lastLog; }
        set { lock (_lock) _lastLog = value; }
    }

    /// <summary>批量更新开始状态。</summary>
    public void MarkStarted(string logName)
    {
        lock (_lock)
        {
            _running = true;
            _lastStart = Infrastructure.TimeUtil.Now().ToString("yyyy-MM-ddTHH:mm:ss");
            _lastLog = logName;
            _lastRc = null;
            _lastFinish = null;
        }
    }

    /// <summary>批量更新结束状态。</summary>
    public void MarkFinished(int exitCode)
    {
        lock (_lock)
        {
            _lastRc = exitCode;
            _lastFinish = Infrastructure.TimeUtil.Now().ToString("yyyy-MM-ddTHH:mm:ss");
        }
    }
}
