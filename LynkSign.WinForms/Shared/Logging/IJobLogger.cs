using LynkSign.Infrastructure;

namespace LynkSign.Logging;

/// <summary>
/// 运行日志出口，对应 Python 版 <c>ql_lynk.py</c> 里的 <c>log(level, msg)</c>。
/// 输出格式完全一致：<c>[yyyy-MM-dd HH:mm:ss] [OK] 消息</c>。
/// </summary>
public interface IJobLogger
{
    void Info(string message);
    void Ok(string message);
    void Warn(string message);
    void Err(string message);
}

/// <summary>统一的时间戳 + 图标格式化。</summary>
public abstract class JobLoggerBase : IJobLogger
{
    private static string Icon(string level) => level switch
    {
        "OK" => "[OK]",
        "WARN" => "[!]",
        "ERR" => "[X]",
        _ => "[INFO]",
    };

    public void Info(string message) => Emit($"[{TimeUtil.Stamp()}] {Icon("INFO")} {message}");
    public void Ok(string message) => Emit($"[{TimeUtil.Stamp()}] {Icon("OK")} {message}");
    public void Warn(string message) => Emit($"[{TimeUtil.Stamp()}] {Icon("WARN")} {message}");
    public void Err(string message) => Emit($"[{TimeUtil.Stamp()}] {Icon("ERR")} {message}");

    /// <summary>落一行格式化后的日志。</summary>
    protected abstract void Emit(string line);
}

/// <summary>只输出到控制台（对应 <c>python ql_lynk.py</c> 单独运行时的行为）。</summary>
public sealed class ConsoleJobLogger : JobLoggerBase, IDisposable
{
    protected override void Emit(string line) => Console.WriteLine(line);

    public void Dispose()
    {
        // 无需释放资源, 仅为统一 using 写法
    }
}
