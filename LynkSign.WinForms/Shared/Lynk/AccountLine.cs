using System.Globalization;
using System.Text.RegularExpressions;

namespace LynkSign.Lynk;

/// <summary>
/// 账户四项当前值的快照（保留接口返回的原始字符串形态）。
///
/// <para>
/// 一律用字符串而不是 int：接口没返回时是 <c>"-"</c>，与「真的是 0」是两回事。
/// 需要参与比较的地方（积分变化）再经 <see cref="AccountLine.ParseNumber"/> 转成可空数字。
/// </para>
/// </summary>
public sealed class AccountSnapshot
{
    /// <summary>积分余额（<c>myEnergy.point</c>，也就是界面上说的「积分 / 能量体」）。</summary>
    public string Point { get; init; } = "-";

    /// <summary>累计获得（<c>myEnergy.incomePoint</c>）。</summary>
    public string Income { get; init; } = "-";

    /// <summary>成长等级名（<c>my/growth.accountLevelVo.name</c>）。</summary>
    public string Level { get; init; } = "-";

    /// <summary>成长值（<c>my/growth.accountLevelVo.growth</c>）。</summary>
    public string Growth { get; init; } = "-";

    /// <summary>这批数字的来源（写日志用），如 <c>签到后复核</c>。</summary>
    public string Origin { get; init; } = "-";

    /// <summary>四项里至少有一项是真实数字（有值才值得记进积分历史）。</summary>
    public bool HasNumbers =>
        AccountLine.ParseNumber(Point) is not null
        || AccountLine.ParseNumber(Income) is not null
        || AccountLine.ParseNumber(Growth) is not null;

    /// <summary>新快照 + 新来源。</summary>
    public AccountSnapshot WithOrigin(string origin) => new()
    {
        Point = Point,
        Income = Income,
        Level = Level,
        Growth = Growth,
        Origin = origin,
    };
}

/// <summary>
/// 「账户积分」那一行日志的生成与解析，与 <see cref="SignStatusLine"/> 是同一套路数。
///
/// <para>
/// 为什么值得单独抽一层：这行日志有两个完全不同的消费方 ——
/// <b>写</b>它的是签到流程与每日状态刷新，<b>读</b>它的是界面上的「账户积分」卡片。
/// 两边分别手拼字符串/手写正则的话，改一边忘一边<b>不会报任何错</b>，
/// 只会表现为「跑完了但卡片上的积分还是上一次的」。所以格式与正则放在同一处，并配离线回归。
/// </para>
///
/// <para>
/// 顺带修掉了一处真实的不一致：<c>SignJob</c> 第 2 步原本打的是
/// <c>账户: Co积分=… 等级=… 能量体=…</c>，而复核与状态刷新打的是
/// <c>积分余额=… 成长等级=… 成长值=…</c> —— 同一个接口的同一组数，两份用词，
/// 事后 grep 日志都对不上号。
/// </para>
/// </summary>
public static class AccountLine
{
    /// <summary>行首标记，同时也是界面判断「这是账户行」的依据。</summary>
    public const string Prefix = "账户积分:";

    /// <summary>
    /// 从日志文本里摘出账户行。
    ///
    /// <para>
    /// 字段之间用 <c>\s{2,}</c> 定界（<see cref="Build"/> 打的就是两个空格）：
    /// 成长等级名可能自带空格（如 <c>LV.3 黄金</c>），用一个空格定界会把它切断。
    /// </para>
    /// </summary>
    public static readonly Regex Pattern = new(
        @"账户积分:\s*积分余额=(?<point>.+?)\s{2,}累计获得=(?<income>.+?)\s{2,}"
        + @"成长等级=(?<level>.+?)\s{2,}成长值=(?<growth>.+?)\s{2,}\(来源\s+(?<origin>[^)]*)\)",
        RegexOptions.Compiled);

    /// <summary>
    /// 生成账户行，例如
    /// <c>账户积分: 积分余额=118  累计获得=306  成长等级=LV.3  成长值=455  (来源 签到后复核)</c>。
    /// </summary>
    public static string Build(string? point, string? income, string? level, string? growth, string origin)
        => $"{Prefix} 积分余额={Value(point)}  累计获得={Value(income)}  "
           + $"成长等级={Value(level)}  成长值={Value(growth)}  (来源 {origin})";

    /// <inheritdoc cref="Build(string?, string?, string?, string?, string)"/>
    public static string Build(AccountSnapshot snapshot)
        => Build(snapshot.Point, snapshot.Income, snapshot.Level, snapshot.Growth, snapshot.Origin);

    /// <summary>把日志行解析回快照；行里缺字段时对应项为 <c>"-"</c>。</summary>
    public static AccountSnapshot? Parse(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;

        var match = Pattern.Match(line);
        if (!match.Success) return null;

        return new AccountSnapshot
        {
            Point = match.Groups["point"].Value.Trim(),
            Income = match.Groups["income"].Value.Trim(),
            Level = match.Groups["level"].Value.Trim(),
            Growth = match.Groups["growth"].Value.Trim(),
            Origin = match.Groups["origin"].Value.Trim(),
        };
    }

    /// <summary>
    /// 展示字符串 → 可空整数：<c>"1,234"</c> → 1234，<c>"-"</c> / 空 / 非数字 → null。
    ///
    /// <para>
    /// 千位分隔符要认（后端偶尔带），<c>"-"</c> 必须回 null 而不是 0 —— 否则一次接口抖动
    /// 就会被记成「积分掉到 0 了」，在变化历史里留下一条触目惊心的假记录。
    /// </para>
    /// </summary>
    public static int? ParseNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.Trim().Replace(",", "").Replace("，", "");
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var big))
        {
            // 超出 int 的极端值按 int 边界收拢，别让比较时溢出
            return big > int.MaxValue ? int.MaxValue : big < int.MinValue ? int.MinValue : (int)big;
        }
        return null;
    }

    /// <summary>缺值统一显示成 <c>"-"</c>（空串与 null 一视同仁）。</summary>
    private static string Value(string? v) => string.IsNullOrWhiteSpace(v) ? "-" : v.Trim();

    /// <summary>
    /// 离线回归：<see cref="Build(string?, string?, string?, string?, string)"/> 打出来的行必须能被
    /// <see cref="Pattern"/> 原样解回来，且数值转换不能把「没拿到」当成 0。
    /// 接进 <c>--selftest</c>。
    /// </summary>
    public static (bool Ok, List<string> Lines) SelfCheck()
    {
        var lines = new List<string>();
        var ok = true;

        (string Point, string Income, string Level, string Growth, string Note)[] cases =
        [
            ("118", "306", "LV.3", "455", "正常值"),
            ("-", "-", "-", "-", "接口没返回（四个都是 -）"),
            ("1,234", "0", "LV.1 青铜", "0", "带千分位 + 等级名含空格 + 真 0"),
        ];

        foreach (var (point, income, level, growth, note) in cases)
        {
            var text = Build(point, income, level, growth, "selftest");
            var parsed = Parse(text);

            if (parsed is null)
            {
                ok = false;
                lines.Add($"[X] 账户行无法被解析回来 ({note}): {text}");
                continue;
            }

            if (parsed.Point != point || parsed.Income != income
                || parsed.Level != level || parsed.Growth != growth)
            {
                ok = false;
                lines.Add($"[X] 账户行解析结果不符 ({note}): {text} → "
                    + $"{parsed.Point}/{parsed.Income}/{parsed.Level}/{parsed.Growth}");
            }
        }

        // 「没拿到」必须回 null，不能回 0 —— 否则一次接口抖动会被记成「积分掉到 0」
        var nullCases = new (string Raw, int? Expected, string Note)[]
        {
            ("-", null, "占位符 -"),
            ("", null, "空串"),
            ("abc", null, "非数字"),
            ("1,234", 1234, "千分位"),
            ("0", 0, "真的是 0（与没拿到区分开）"),
            ("-5", -5, "负数"),
        };

        var parseOk = true;
        foreach (var (raw, expected, note) in nullCases)
        {
            var actual = ParseNumber(raw);
            if (actual != expected)
            {
                parseOk = false;
                lines.Add($"[X] 数值转换不符: \"{raw}\" → {actual?.ToString() ?? "null"} 期望 {expected?.ToString() ?? "null"} ({note})");
            }
        }

        ok &= parseOk;
        lines.Add($"账户行: 生成 ↔ 解析 {(ok ? "一致" : "不一致")} (账户积分卡靠它刷新，"
            + $"\"-\"→null 也一并钉住)");

        return (ok, lines);
    }
}
