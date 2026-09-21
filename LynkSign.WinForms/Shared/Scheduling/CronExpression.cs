namespace LynkSign.Scheduling;

/// <summary>
/// 标准 5 段 cron 解析器（分 时 日 月 周），语义与 Python 版 <c>config_server.py</c>
/// 中的 <c>_cron_field</c> / <c>cron_is_valid</c> / <c>cron_match</c> / <c>cron_next</c> 完全一致。
///
/// 特性：
/// <list type="bullet">
///   <item>支持 <c>*</c>、<c>a</c>、<c>a-b</c>、<c>*/n</c>、<c>a-b/n</c>、逗号组合；</item>
///   <item>周字段 <c>0</c> 与 <c>7</c> 都代表周日；</item>
///   <item>「日」与「周」同时被限定时取 <b>或</b>（Vixie cron 语义），否则取被限定的那个。</item>
/// </list>
///
/// 相比 Python 版：字段集合在构造时一次性展开缓存，<see cref="Matches"/> 与
/// <see cref="Next"/>（最坏要逐分钟扫 527040 次）不再重复解析表达式。
/// </summary>
public sealed class CronExpression
{
    private readonly HashSet<int> _minutes;
    private readonly HashSet<int> _hours;
    private readonly HashSet<int> _daysOfMonth;
    private readonly HashSet<int> _months;
    private readonly HashSet<int> _daysOfWeek;
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;

    /// <summary>原始表达式。</summary>
    public string Raw { get; }

    private CronExpression(
        string raw,
        HashSet<int> minutes,
        HashSet<int> hours,
        HashSet<int> daysOfMonth,
        HashSet<int> months,
        HashSet<int> daysOfWeek,
        bool domRestricted,
        bool dowRestricted)
    {
        Raw = raw;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _dayOfMonthRestricted = domRestricted;
        _dayOfWeekRestricted = dowRestricted;
    }

    /// <summary>解析表达式；非法时返回 false（不抛异常）。</summary>
    public static bool TryParse(string? expression, out CronExpression cron)
    {
        cron = null!;
        if (string.IsNullOrWhiteSpace(expression)) return false;

        var fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) return false;

        try
        {
            var minutes = Expand(fields[0], 0, 59);
            var hours = Expand(fields[1], 0, 23);
            var daysOfMonth = Expand(fields[2], 1, 31);
            var months = Expand(fields[3], 1, 12);
            var daysOfWeek = Expand(fields[4], 0, 7);

            // 7 与 0 都表示周日
            if (daysOfWeek.Contains(7)) daysOfWeek.Add(0);

            cron = new CronExpression(
                expression.Trim(),
                minutes,
                hours,
                daysOfMonth,
                months,
                daysOfWeek,
                domRestricted: fields[2] != "*",
                dowRestricted: fields[4] != "*");

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>表达式是否合法。</summary>
    public static bool IsValid(string? expression) => TryParse(expression, out _);

    /// <summary>某个时间点是否命中。</summary>
    public bool Matches(DateTime dt)
    {
        if (!_minutes.Contains(dt.Minute)) return false;
        if (!_hours.Contains(dt.Hour)) return false;
        if (!_months.Contains(dt.Month)) return false;

        var dayOfWeek = (int)dt.DayOfWeek;   // .NET: 周日=0 … 周六=6, 与 cron 一致
        var domOk = _daysOfMonth.Contains(dt.Day);
        var dowOk = _daysOfWeek.Contains(dayOfWeek);

        // Vixie cron: 日与周都限定时取「或」, 否则取被限定的那个
        if (_dayOfMonthRestricted && _dayOfWeekRestricted) return domOk || dowOk;
        if (_dayOfMonthRestricted) return domOk;
        if (_dayOfWeekRestricted) return dowOk;
        return true;
    }

    /// <summary>从 <paramref name="baseTime"/> 之后找下一个命中时间（逐分钟扫描，上限 366 天）。</summary>
    public DateTime? Next(DateTime baseTime)
    {
        var t = new DateTime(baseTime.Year, baseTime.Month, baseTime.Day, baseTime.Hour, baseTime.Minute, 0)
            .AddMinutes(1);

        const int limit = 366 * 24 * 60;
        for (var i = 0; i < limit; i++)
        {
            if (Matches(t)) return t;
            t = t.AddMinutes(1);
        }

        return null;
    }

    /// <summary>
    /// 把单个字段展开成允许值集合，等价 Python 的 <c>_cron_field()</c>。
    /// 越界或步长非法时抛 <see cref="FormatException"/>（外层 <see cref="TryParse"/> 会吞掉）。
    /// </summary>
    private static HashSet<int> Expand(string expression, int lo, int hi)
    {
        var values = new HashSet<int>();

        foreach (var rawPart in expression.Split(','))
        {
            var part = rawPart.Trim();
            var step = 1;
            var range = part;

            if (part.Contains('/'))
            {
                var segments = part.Split('/', 2);
                range = segments[0];
                if (!int.TryParse(segments[1], out step)) throw new FormatException($"步长非法: {part}");
            }

            int start, end;
            if (range is "*" or "")
            {
                start = lo;
                end = hi;
            }
            else if (range.Contains('-'))
            {
                var bounds = range.Split('-', 2);
                if (!int.TryParse(bounds[0], out start) || !int.TryParse(bounds[1], out end))
                {
                    throw new FormatException($"区间非法: {part}");
                }
            }
            else
            {
                if (!int.TryParse(range, out start)) throw new FormatException($"取值非法: {part}");
                end = start;
            }

            if (start < lo || end > hi || step < 1) throw new FormatException($"字段越界: {part}");

            for (var v = start; v <= end; v += step) values.Add(v);
        }

        return values;
    }
}

/// <summary>供 API 展示的「下次运行时间」计算，对应 Python 的 <c>next_run_time()</c>。</summary>
public static class CronSchedule
{
    /// <summary>计算下次运行时间；表达式非法时返回 null。</summary>
    public static DateTime? Next(string? expression, DateTime from)
    {
        try
        {
            if (!CronExpression.TryParse(expression, out var cron)) return null;
            return cron.Next(from);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>计算并格式化为 <c>yyyy-MM-dd HH:mm:ss</c>。</summary>
    public static string? NextText(string? expression)
    {
        var next = Next(expression, Infrastructure.TimeUtil.Now());
        return next?.ToString("yyyy-MM-dd HH:mm:ss");
    }
}
