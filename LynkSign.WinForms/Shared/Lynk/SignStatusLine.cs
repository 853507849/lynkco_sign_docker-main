using System.Text.RegularExpressions;

namespace LynkSign.Lynk;

/// <summary>
/// 「签到状态」那一行日志的生成与解析。
///
/// <para>
/// 界面顶部的三张状态卡（今日签到 / 连续签到 / 补签卡）是<b>靠解析这行日志</b>刷新的
/// （见 <c>MainForm.ApplySignStatus</c>）—— 这样不必让签到流程额外回传结构化结果，
/// 手动触发与 cron 触发两条路也自动都覆盖到。代价是格式与正则必须严丝合缝：
/// 写得出来却解析不回去（多一个空格、少一个单位）<b>不会报任何错</b>，
/// 只会表现为「签到成功了但卡片不动」。所以两者放在同一处，并配一个离线回归把它钉住。
/// </para>
/// </summary>
public static class SignStatusLine
{
    /// <summary>从日志文本里摘出签到状态行（界面用它刷新状态卡）。</summary>
    public static readonly Regex Pattern = new(
        @"签到状态:\s*今日(已|未)签到.*?连续签到=(\d+)天\s*补签卡=(\d+)张",
        RegexOptions.Compiled);

    /// <summary>
    /// 生成状态行，例如
    /// <c>签到状态: 今日已签到  (来源 签到后复核)  连续签到=13天  补签卡=3张</c>。
    /// </summary>
    /// <param name="signed">今天签没签。</param>
    /// <param name="origin">这个判断的来源（写日志时可读、排错有用）。</param>
    public static string Build(bool signed, string origin, int streak, int signCard)
        => $"签到状态: 今日{(signed ? "已" : "未")}签到  (来源 {origin})  连续签到={streak}天  补签卡={signCard}张";

    /// <summary>
    /// 离线回归：<see cref="Build"/> 写出来的行必须能被 <see cref="Pattern"/> 原样解回来。
    /// 只改其中一头（比如把「连续」改成「已连续」）就会在这里炸，而不是等到某天
    /// 用户发现「签到成功但卡片还是未签到」。
    /// </summary>
    public static (bool Ok, List<string> Lines) SelfCheck()
    {
        var lines = new List<string>();
        var ok = true;

        foreach (var signed in new[] { true, false })
        {
            var text = Build(signed, "selftest", 13, 3);
            var match = Pattern.Match(text);

            if (!match.Success)
            {
                ok = false;
                lines.Add($"[X] 状态行无法被解析回来: {text}");
                continue;
            }

            var expected = signed ? "已" : "未";
            if (match.Groups[1].Value != expected
                || match.Groups[2].Value != "13"
                || match.Groups[3].Value != "3")
            {
                ok = false;
                lines.Add($"[X] 状态行解析结果不符: {text} → "
                    + $"{match.Groups[1].Value}/{match.Groups[2].Value}/{match.Groups[3].Value}");
            }
        }

        lines.Add($"签到状态行: 生成 ↔ 解析 {(ok ? "一致" : "不一致")} (状态卡靠它刷新)");
        return (ok, lines);
    }
}
