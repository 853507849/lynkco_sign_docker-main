using System.Text;

namespace LynkSign.WinForms.App;

/// <summary>
/// 把 <see cref="Console"/> 的输出接管到一个回调上（通常接到窗体的日志框）。
///
/// <para>
/// 为什么用这种「劫持标准输出」的做法，而不是改造成注入式日志：
/// 原项目（src/LynkSign）的日志出口全部走 <c>Console.WriteLine</c> ——
/// <list type="bullet">
///   <item><c>RunLogWriter</c>：<c>Console.WriteLine</c> + 写日志文件（<c>writeLogFile: true</c>）；</item>
///   <item><c>ConsoleJobLogger</c>：只 <c>Console.WriteLine</c>（<c>writeLogFile: false</c>）。</item>
/// </list>
/// 只要在这里接一根管子，桌面版就能<b>零改动</b>地同时拿到「日志文件」与「实时输出」，
/// 原项目一行都不用动，两边行为也永远一致。
/// </para>
///
/// <para>
/// 注意：<c>SignJobRunner</c> 内部还会直接调用 <c>Console.WriteLine</c>（如 "配置已写入"），
/// 同样会经过这里。
/// </para>
/// </summary>
internal sealed class UiLogWriter : TextWriter
{
    private readonly Action<string> _onLine;
    private readonly StringBuilder _pending = new();
    private readonly object _lock = new();

    public UiLogWriter(Action<string> onLine) => _onLine = onLine;

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\r') return;

        string? ready = null;
        lock (_lock)
        {
            if (value == '\n')
            {
                ready = _pending.ToString();
                _pending.Clear();
            }
            else
            {
                _pending.Append(value);
            }
        }

        // 回调放在锁外：回调最终会 BeginInvoke 到 UI 线程，
        // 万一 UI 正在关闭时同步阻塞，也不会把写日志的线程锁死。
        if (ready is not null) SafeEmit(ready);
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        foreach (var ch in value) Write(ch);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        for (var i = index; i < index + count && i < buffer.Length; i++) Write(buffer[i]);
    }

    public override void WriteLine(string? value)
    {
        if (!string.IsNullOrEmpty(value)) Write(value);
        Write('\n');
    }

    public override void WriteLine() => Write('\n');

    /// <summary>把残留的半行刷出去（程序退出前调用，避免最后一行丢失）。</summary>
    public void FlushPending()
    {
        string? ready = null;
        lock (_lock)
        {
            if (_pending.Length > 0)
            {
                ready = _pending.ToString();
                _pending.Clear();
            }
        }
        if (ready is not null) SafeEmit(ready);
    }

    private void SafeEmit(string line)
    {
        try
        {
            _onLine(line);
        }
        catch
        {
            // 窗体已经销毁（关闭过程中还有日志在刷）时直接丢弃
        }
    }
}
