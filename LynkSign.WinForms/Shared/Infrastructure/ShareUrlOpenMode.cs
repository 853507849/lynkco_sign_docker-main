namespace LynkSign.Infrastructure;

/// <summary>
/// 「拿到分享链接后要不要自动用浏览器打开」的策略。
///
/// <para>
/// 之所以不是一个简单的 <c>bool</c>：定时任务（cron）大多在早上无人值守时跑，
/// 这时候弹一个浏览器窗口出来是纯打扰；而点「立即签到」是人坐在电脑前主动触发的，
/// 打开浏览器正好是想要的结果。两件事共用同一个开关就只能二选一，
/// 所以这里把「什么时候打开」也变成可配的。
/// </para>
/// </summary>
public enum ShareUrlOpenMode
{
    /// <summary>不自动打开（默认）。链接只打印到日志 / 推送里，由用户自己复制。</summary>
    Off = 0,

    /// <summary>只在手动触发（桌面版「立即签到」、<c>--run-once</c>）后打开。</summary>
    Manual = 1,

    /// <summary>手动触发与 cron 定时触发都打开。</summary>
    Always = 2,
}

/// <summary>
/// <see cref="ShareUrlOpenMode"/> 与 config.json 字符串值之间的换算，以及取值宽容解析。
///
/// <para>
/// 配置文件里存的是字符串（<c>off</c> / <c>manual</c> / <c>always</c>）而不是数字或布尔，
/// 原因是这份 config.json 要给人看、也要被不算聪明的脚本改；
/// <c>"open_share_url": "manual"</c> 一眼就知道是什么意思，而 <c>1</c> 需要回去查代码。
/// </para>
/// </summary>
public static class ShareUrlOpenModes
{
    /// <summary>config.json 里的字段名。</summary>
    public const string ConfigField = "open_share_url";

    /// <summary>环境变量名（优先级高于 config.json，低于命令行）。</summary>
    public const string EnvVar = "LYNK_OPEN_SHARE_URL";

    /// <summary>规范值：不打开。</summary>
    public const string OffValue = "off";

    /// <summary>规范值：仅手动触发时打开。</summary>
    public const string ManualValue = "manual";

    /// <summary>规范值：手动 + 定时都打开。</summary>
    public const string AlwaysValue = "always";

    /// <summary>
    /// 宽容解析配置值。<b>无法识别 / 留空一律按 <see cref="ShareUrlOpenMode.Off"/> 处理</b> ——
    /// 这个开关的副作用是「弹一个窗口」，宁可少弹也不能因为写错一个词就变成随机弹。
    /// </summary>
    public static ShareUrlOpenMode Parse(string? raw)
    {
        switch ((raw ?? "").Trim().ToLowerInvariant())
        {
            case "off":
            case "none":
            case "never":
            case "0":
            case "false":
            case "no":
                return ShareUrlOpenMode.Off;

            // 布尔式的 on/true 归到 Manual：这类值多半是「想打开」的直觉写法，
            // 而「定时任务也弹窗」是更重的副作用，不该由一个含混的 true 触发。
            case "manual":
            case "once":
            case "true":
            case "yes":
            case "on":
            case "1":
                return ShareUrlOpenMode.Manual;

            case "always":
            case "all":
            case "auto":
                return ShareUrlOpenMode.Always;

            default:
                return ShareUrlOpenMode.Off;
        }
    }

    /// <summary>写回 config.json 时用的规范字符串。</summary>
    public static string ToConfigValue(this ShareUrlOpenMode mode) => mode switch
    {
        ShareUrlOpenMode.Manual => ManualValue,
        ShareUrlOpenMode.Always => AlwaysValue,
        _ => OffValue,
    };

    /// <summary>给界面 / 日志看的中文说明。</summary>
    public static string Describe(this ShareUrlOpenMode mode) => mode switch
    {
        ShareUrlOpenMode.Manual => "仅手动触发时打开",
        ShareUrlOpenMode.Always => "手动 + 定时都打开",
        _ => "不自动打开",
    };

    /// <summary>本次触发方式（<c>manual</c> / <c>cron</c>）下是否应该打开浏览器。</summary>
    public static bool ShouldOpen(this ShareUrlOpenMode mode, string trigger) => mode switch
    {
        ShareUrlOpenMode.Off => false,
        ShareUrlOpenMode.Manual => IsManualTrigger(trigger),
        _ => true,
    };

    /// <summary>触发方式是否属于「人在电脑前主动点的」。</summary>
    public static bool IsManualTrigger(string? trigger)
        => (trigger ?? "").StartsWith("manual", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 离线回归：把「哪些写法归到哪一档」「哪一档在哪种触发下打开」钉死。
    ///
    /// <para>
    /// 这套映射有两处最容易在改代码时被无意改坏，而它们都不是编译期能发现的：
    /// 一是 <see cref="Parse"/> 把不认识的词落到 Off（宽松兜底），
    /// 二是 <see cref="ShouldOpen"/> 里 Manual 必须挡住 cron —— 一旦写反，
    /// 表现就是「早上 9 点突然弹出浏览器」，而不是报错。
    /// </para>
    /// </summary>
    public static (bool Ok, List<string> Lines) SelfCheck()
    {
        var lines = new List<string>();
        var ok = true;

        void Expect(string label, bool actual, bool expected)
        {
            if (actual != expected) ok = false;
            lines.Add($"{(actual == expected ? "  [OK] " : "  [X]  ")}{label}: 实际={actual} 期望={expected}");
        }

        // 1) 取值归一化：任何写入 config.json 的值都必须是三个规范值之一，
        //    否则「界面显示这一档、实际执行另一档」这类问题会永远查不出来。
        (string Input, ShareUrlOpenMode Expected)[] cases =
        [
            ("", ShareUrlOpenMode.Off),
            ("   ", ShareUrlOpenMode.Off),
            ("off", ShareUrlOpenMode.Off),
            ("OFF", ShareUrlOpenMode.Off),
            ("false", ShareUrlOpenMode.Off),
            ("0", ShareUrlOpenMode.Off),
            ("manual", ShareUrlOpenMode.Manual),
            (" true ", ShareUrlOpenMode.Manual),
            ("on", ShareUrlOpenMode.Manual),
            ("always", ShareUrlOpenMode.Always),
            (" ALWAYS ", ShareUrlOpenMode.Always),
            ("auto", ShareUrlOpenMode.Always),
            ("随便写的", ShareUrlOpenMode.Off),   // 不认识的词必须落到 Off
        ];

        foreach (var (input, expected) in cases)
        {
            var parsed = Parse(input);
            var roundTrip = Parse(parsed.ToConfigValue());

            Expect($"open_share_url=\"{input}\"", parsed == expected, true);
            Expect($"  └ 规范值回读 \"{parsed.ToConfigValue()}\"", roundTrip == parsed, true);
        }

        // 2) 触发方式 × 档位矩阵。Manual 不挡 cron 是这段代码里最贵的错误。
        Expect("off    + manual", ShareUrlOpenMode.Off.ShouldOpen("manual"), false);
        Expect("off    + cron", ShareUrlOpenMode.Off.ShouldOpen("cron"), false);
        Expect("manual + manual", ShareUrlOpenMode.Manual.ShouldOpen("manual"), true);
        Expect("manual + cron", ShareUrlOpenMode.Manual.ShouldOpen("cron"), false);
        Expect("always + manual", ShareUrlOpenMode.Always.ShouldOpen("manual"), true);
        Expect("always + cron", ShareUrlOpenMode.Always.ShouldOpen("cron"), true);

        return (ok, lines);
    }
}
