using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LynkSign.Infrastructure;
using LynkSign.Logging;

namespace LynkSign.Services;

/// <summary>日志列表项，字段名与前端的期望一致。</summary>
public sealed class LogItem
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("mtime")] public string Mtime { get; init; } = "";
    [JsonPropertyName("trigger")] public string Trigger { get; init; } = "";
    [JsonPropertyName("exit_code")] public int? ExitCode { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "";
}

/// <summary>
/// 运行日志仓库，对应 Python 版的 <c>prune_logs()</c> / <c>list_logs()</c> /
/// <c>log_exit_code()</c> 与 <c>run_job()</c> 里的日志文件写入。
/// </summary>
public sealed class RunLogStore
{
    /// <summary>日志保留天数。</summary>
    public const int RetentionDays = 35;

    /// <summary>合法日志文件名：run-yyyy-MM-dd_HHmmss[-触发方式].log。</summary>
    private static readonly Regex NamePattern =
        new(@"^run-\d{4}-\d{2}-\d{2}_\d{6}(?:-\w+)?\.log$", RegexOptions.Compiled);

    private static readonly Regex ExitCodePattern =
        new(@"EXIT CODE:\s*(-?\d+)", RegexOptions.Compiled);

    /// <summary>日志目录。</summary>
    public string Dir { get; } = AppPaths.LogsDir;

    /// <summary>生成新的日志文件路径（等价 <c>run-{ts}-{trigger}.log</c>）。</summary>
    public string NewLogPath(string trigger)
        => Path.Combine(Dir, $"run-{TimeUtil.Now():yyyy-MM-dd_HHmmss}-{trigger}.log");

    /// <summary>删除超过保留期的日志。</summary>
    public void Prune()
    {
        if (!Directory.Exists(Dir)) return;

        var cutoffUtc = DateTime.UtcNow.AddDays(-RetentionDays);

        foreach (var path in Directory.EnumerateFiles(Dir))
        {
            if (!NamePattern.IsMatch(Path.GetFileName(path))) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoffUtc) File.Delete(path);
            }
            catch
            {
                // 文件被占用等, 忽略
            }
        }
    }

    /// <summary>列出日志（按名称倒序，等价 Python 的 <c>out.sort(..., reverse=True)</c>）。</summary>
    public List<LogItem> List()
    {
        var items = new List<LogItem>();
        if (!Directory.Exists(Dir)) return items;

        foreach (var path in Directory.EnumerateFiles(Dir))
        {
            var name = Path.GetFileName(path);
            if (!NamePattern.IsMatch(name)) continue;

            try
            {
                var info = new FileInfo(path);
                var exitCode = ReadExitCode(path);

                items.Add(new LogItem
                {
                    Name = name,
                    Size = info.Length,
                    Mtime = TimeZoneInfo.ConvertTimeFromUtc(info.LastWriteTimeUtc, TimeUtil.Tz)
                        .ToString("yyyy-MM-dd HH:mm:ss"),
                    Trigger = ExtractTrigger(name),
                    ExitCode = exitCode,
                    Status = exitCode is null ? "running" : (exitCode == 0 ? "ok" : "fail"),
                });
            }
            catch
            {
                // 读不到就跳过这条
            }
        }

        items.Sort((a, b) => string.CompareOrdinal(b.Name, a.Name));
        return items;
    }

    /// <summary>从日志尾部解析 EXIT CODE；仍在运行中则返回 null。</summary>
    public int? ReadExitCode(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var length = stream.Length;
            var take = (int)Math.Min(400, length);
            if (take <= 0) return null;

            stream.Seek(-take, SeekOrigin.End);
            var buffer = new byte[take];
            var read = stream.Read(buffer, 0, take);

            var tail = Encoding.UTF8.GetString(buffer, 0, read);
            var match = ExitCodePattern.Match(tail);

            return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>校验文件名是否合法（防目录穿越）。</summary>
    public static bool IsValidLogName(string? name)
        => !string.IsNullOrEmpty(name) && NamePattern.IsMatch(name);

    /// <summary>
    /// 解析触发方式，等价 Python 的 <c>name.rsplit("-", 1)[-1].replace(".log", "")</c>。
    /// 用 LastIndexOf 而不是 Split，保证触发器名里带连字符时也正确。
    /// </summary>
    private static string ExtractTrigger(string name)
    {
        var dot = name.LastIndexOf('.');
        var withoutExt = dot >= 0 ? name[..dot] : name;
        var dash = withoutExt.LastIndexOf('-');
        return dash >= 0 ? withoutExt[(dash + 1)..] : "?";
    }
}

/// <summary>
/// 运行日志写入器：同时输出到控制台与日志文件，逐行 flush，
/// 因此前端在任务运行期间就能看到实时输出（对应 Python 版子进程 stdout 的流式转发）。
/// </summary>
public sealed class RunLogWriter : JobLoggerBase, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private bool _disposed;

    public string LogPath { get; }
    public string LogName { get; }

    public RunLogWriter(string logPath, string trigger)
    {
        LogPath = logPath;
        LogName = Path.GetFileName(logPath);

        var dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _writer = new StreamWriter(logPath, append: false, Encoding.UTF8)
        {
            AutoFlush = true,
            NewLine = "\n",   // 统一 LF, 与 Python 版日志格式一致
        };

        WriteLine("===== 领克签到运行日志 =====");
        WriteLine($"触发方式: {trigger}");
        WriteLine($"开始时间: {TimeUtil.Stamp()}");
        WriteLine(new string('=', 40));
        WriteLine("");
    }

    protected override void Emit(string line) => WriteLine(line);

    /// <summary>写一行原始文本（自动补时间戳之外的头部信息用）。</summary>
    public void WriteLine(string text)
    {
        lock (_lock)
        {
            if (_disposed) return;
            Console.WriteLine(text);
            _writer.WriteLine(text);
        }
    }

    /// <summary>写入收尾信息与退出码。</summary>
    public void Complete(int exitCode)
    {
        lock (_lock)
        {
            if (_disposed) return;

            _writer.WriteLine();
            _writer.WriteLine(new string('=', 40));
            _writer.WriteLine($"结束时间: {TimeUtil.Stamp()}");
            _writer.WriteLine($"===== EXIT CODE: {exitCode} =====");
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch
            {
                // 忽略关闭异常
            }
        }
    }
}
