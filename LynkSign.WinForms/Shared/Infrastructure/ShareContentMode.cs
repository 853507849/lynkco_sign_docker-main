namespace LynkSign.Infrastructure;

/// <summary>
/// 「本次分享用哪篇文章的 ID」的策略。
///
/// <para>
/// 分享链接里带着被分享文章的 id（<c>?id=…</c>），B 账号点开加分时后端也会拿这个 id
/// 去校验前后两步一致，所以它必须是一个**当前有效**的文章。
/// 而文章会被下架、会过期 —— 硬编码一个 id 迟早会失效，且失效那天不会有任何报错，
/// 只会表现为「签到成功但分享不加分」。
/// </para>
/// </summary>
public enum ShareContentMode
{
    /// <summary>
    /// 每次运行去探索广场拉内容流，取最新一篇「文章」的 id（默认，推荐）。
    /// 拉不到时自动回退到 config.json 里配的 <c>share_content_id</c>。
    /// </summary>
    Auto = 0,

    /// <summary>固定用 <c>share_content_id</c> 里配的那个 id，不发额外请求。</summary>
    Fixed = 1,
}

/// <summary>
/// <see cref="ShareContentMode"/> 与 config.json 字符串值之间的换算，以及取值宽容解析。
///
/// <para>
/// 与 <see cref="ShareUrlOpenModes"/> 保持同一套写法：配置里存人话字符串而不是布尔，
/// 因为 <c>"share_content_mode": "auto"</c> 一眼能看懂，<c>true</c> 得回去查代码。
/// </para>
/// </summary>
public static class ShareContentModes
{
    /// <summary>config.json 里的字段名。</summary>
    public const string ConfigField = "share_content_mode";

    /// <summary>环境变量名（优先级高于 config.json，低于命令行）。</summary>
    public const string EnvVar = "LYNK_SHARE_CONTENT_MODE";

    /// <summary>规范值：自动拉取最新文章。</summary>
    public const string AutoValue = "auto";

    /// <summary>规范值：固定用配置里的文章 ID。</summary>
    public const string FixedValue = "fixed";

    /// <summary>
    /// 宽容解析配置值。<b>无法识别 / 留空一律按 <see cref="ShareContentMode.Auto"/> 处理</b>。
    ///
    /// <para>
    /// 与 <see cref="ShareUrlOpenModes.Parse"/> 的兜底方向刚好相反，这是刻意的：
    /// 那个开关的副作用是「弹一个窗口」，宁可少弹；而这一项兜底到 Auto 的后果只是
    /// 「多发一个取文章列表的请求」，却能让分享不会因为文章被下架而静默失效 ——
    /// 拿不准的时候，选那个不会悄悄坏掉的一档。
    /// </para>
    /// </summary>
    public static ShareContentMode Parse(string? raw)
    {
        switch ((raw ?? "").Trim().ToLowerInvariant())
        {
            case "auto":
            case "latest":
            case "new":
            case "dynamic":
            case "true":
            case "yes":
            case "on":
            case "1":
                return ShareContentMode.Auto;

            case "fixed":
            case "fix":
            case "static":
            case "manual":
            case "config":
            case "custom":
            case "false":
            case "no":
            case "off":
            case "0":
                return ShareContentMode.Fixed;

            default:
                return ShareContentMode.Auto;
        }
    }

    /// <summary>写回 config.json 时用的规范字符串。</summary>
    public static string ToConfigValue(this ShareContentMode mode)
        => mode == ShareContentMode.Fixed ? FixedValue : AutoValue;

    /// <summary>给界面 / 日志看的中文说明。</summary>
    public static string Describe(this ShareContentMode mode)
        => mode == ShareContentMode.Fixed ? "固定用配置里的文章 ID" : "自动拉取广场最新一篇";

    /// <summary>
    /// 离线回归：把取值归一化钉死。
    ///
    /// <para>
    /// 这套映射改坏的后果特别隐蔽 —— 某一档被解析成另一档时不会报错，
    /// 只是「配置写的自动、实际一直是固定 id」，要等文章下架后分享静默不加分才会发现。
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

        (string Input, ShareContentMode Expected)[] cases =
        [
            ("", ShareContentMode.Auto),            // 留空 → auto（老配置升级后的行为）
            ("   ", ShareContentMode.Auto),
            ("auto", ShareContentMode.Auto),
            (" AUTO ", ShareContentMode.Auto),
            ("latest", ShareContentMode.Auto),
            ("true", ShareContentMode.Auto),
            ("1", ShareContentMode.Auto),
            ("on", ShareContentMode.Auto),
            ("fixed", ShareContentMode.Fixed),
            (" FIXED ", ShareContentMode.Fixed),
            ("static", ShareContentMode.Fixed),
            ("false", ShareContentMode.Fixed),
            ("0", ShareContentMode.Fixed),
            ("随便写的", ShareContentMode.Auto),     // 不认识的词必须落到 auto
        ];

        foreach (var (input, expected) in cases)
        {
            var parsed = Parse(input);
            var roundTrip = Parse(parsed.ToConfigValue());

            Expect($"{ConfigField}=\"{input}\"", parsed == expected, true);
            Expect($"  └ 规范值回读 \"{parsed.ToConfigValue()}\"", roundTrip == parsed, true);
        }

        return (ok, lines);
    }
}
