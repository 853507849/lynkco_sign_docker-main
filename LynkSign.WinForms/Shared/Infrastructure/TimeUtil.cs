using System.Globalization;

namespace LynkSign.Infrastructure;

/// <summary>
/// 时区与时间工具。对应 Python 版的 <c>_LOCAL_TZ</c> / <c>now()</c> / <c>parse_expiry()</c>。
///
/// 解析顺序：环境变量 <c>TZ</c>（默认 Asia/Shanghai）→ 失败则退化成固定偏移
/// （<c>LYNK_TZ_OFFSET</c>，默认 +8 小时）。这样即使镜像里没有 tzdata 也能正确运行。
/// </summary>
public static class TimeUtil
{
    /// <summary>当前生效的时区。</summary>
    public static TimeZoneInfo Tz { get; } = ResolveTimeZone();

    private static TimeZoneInfo ResolveTimeZone()
    {
        var tzName = Environment.GetEnvironmentVariable("TZ");
        if (string.IsNullOrWhiteSpace(tzName)) tzName = "Asia/Shanghai";

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(tzName.Trim());
        }
        catch (Exception)
        {
            var offsetHours = 8d;
            var raw = Environment.GetEnvironmentVariable("LYNK_TZ_OFFSET");
            if (!string.IsNullOrWhiteSpace(raw)
                && double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && parsed is >= -12 and <= 14)
            {
                offsetHours = parsed;
            }

            var offset = TimeSpan.FromHours(offsetHours);
            var id = $"LYNK_UTC{(offsetHours >= 0 ? "+" : "-")}{Math.Abs(offsetHours).ToString("0.##", CultureInfo.InvariantCulture)}";
            var display = $"UTC{(offsetHours >= 0 ? "+" : "-")}{Math.Abs(offsetHours).ToString("0.##", CultureInfo.InvariantCulture)}";
            return TimeZoneInfo.CreateCustomTimeZone(id, offset, display, display);
        }
    }

    /// <summary>当前本地时间（Kind 为 Unspecified，便于与解析出来的裸时间比较）。</summary>
    public static DateTime Now() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Tz);

    /// <summary>格式化为 <c>yyyy-MM-dd HH:mm:ss</c>。</summary>
    public static string Stamp() => Now().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Unix 秒（或毫秒）时间戳 → 本地时间。</summary>
    public static DateTime FromUnixSeconds(double seconds)
    {
        var whole = (long)Math.Floor(seconds);
        var utc = DateTimeOffset.FromUnixTimeSeconds(whole).UtcDateTime;
        return TimeZoneInfo.ConvertTimeFromUtc(utc, Tz);
    }

    /// <summary>
    /// 解析接口返回的过期时间，等价于 Python 的 <c>parse_expiry()</c>：
    /// 数字（&gt;1e12 视为毫秒）、ISO 字符串（带 Z / 带偏移 / 裸时间按 +8 处理）。
    /// 返回本地裸时间；解析不出来返回 null。
    /// </summary>
    public static DateTime? ParseExpiry(object? value)
    {
        switch (value)
        {
            case null:
                return null;

            case double d:
                return FromUnixSeconds(d > 1e12 ? d / 1000d : d);

            case float f:
                return FromUnixSeconds(f > 1e12 ? f / 1000d : f);

            case long l:
                return FromUnixSeconds(l > 1e12 ? l / 1000d : l);

            case int i:
                return FromUnixSeconds(i);

            case string s when !string.IsNullOrWhiteSpace(s):
            {
                var text = s.Trim().Replace("Z", "+00:00", StringComparison.Ordinal);

                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
                {
                    // 带时区偏移 → 换算成本地时间后去掉时区标记
                    return TimeZoneInfo.ConvertTimeFromUtc(dto.UtcDateTime, Tz);
                }

                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var naive))
                {
                    // 裸时间按 +8 解释（与原脚本一致），再折算到目标时区
                    var assumed = new DateTimeOffset(DateTime.SpecifyKind(naive, DateTimeKind.Unspecified),
                                                     TimeSpan.FromHours(8));
                    return TimeZoneInfo.ConvertTimeFromUtc(assumed.UtcDateTime, Tz);
                }

                return null;
            }

            default:
                return null;
        }
    }
}
