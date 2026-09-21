namespace LynkSign.Lynk;

/// <summary>
/// 签名算法的离线回归检查。
///
/// <para>
/// 为什么值得单独做：X-Ca 签名一旦拼错，网关只会回一句
/// <c>400 Invalid Signature</c>，既不告诉你是哪一行错、也不告诉你是哪个头错。
/// 而签名算法本身是<b>纯函数</b> —— 只要手里有「历史请求的 nonce / 时间戳 / 最终签名」，
/// 就能离线、零网络、零副作用地验证算法是否一字不差。
/// </para>
///
/// <para>
/// 这里的黄金样本取自 2026-09-19 的 App 抓包（H5 通道）。H5 通道的签名头只有
/// Key/Nonce/Signature-Method/Timestamp 四个，<b>token 不参与签名</b>，
/// 所以样本里不含任何凭据 —— 可以安全地长期留在源码里当回归基线。
/// </para>
/// </summary>
public static class SignatureSelfCheck
{
    /// <summary>一条抓包黄金样本：固定的输入 + 当时真实算出的签名。</summary>
    /// <param name="Label">样本名（日志展示用）。</param>
    /// <param name="Method">HTTP 方法。</param>
    /// <param name="Path">接口路径（本样本均无 query）。</param>
    /// <param name="Accept">当时的 <c>Accept</c> 头值。</param>
    /// <param name="ContentType">当时的 <c>Content-Type</c> 头值。</param>
    /// <param name="Nonce">当时的 <c>X-Ca-Nonce</c> 值。</param>
    /// <param name="Timestamp">当时的 <c>X-Ca-Timestamp</c> 值（毫秒）。</param>
    /// <param name="Expected">抓包里那条请求的 <c>X-Ca-Signature</c>（Base64）。</param>
    public sealed record GoldenCase(
        string Label,
        string Method,
        string Path,
        string Accept,
        string ContentType,
        string Nonce,
        string Timestamp,
        string Expected);

    /// <summary>H5 通道的抓包黄金样本（一个 GET + 一个 POST，覆盖两种方法）。</summary>
    public static IReadOnlyList<GoldenCase> H5Cases { get; } =
    [
        new(
            Label: "GET 连续天数",
            Method: "GET",
            Path: "/up/api/v1/userReward/getContinueDaysAndSignCard",
            Accept: "*/*",
            ContentType: "application/json",
            Nonce: "0a5366aa-df1a-4cce-82d0-a5ffdc95084f",
            Timestamp: "1789787162756",
            Expected: "shP9tYiAa8rmftrxLR6lPWyx1hUwDrZDANM6S/3Y3ks="),

        new(
            Label: "POST 签到日历",
            Method: "POST",
            Path: "/up/api/v1/user/sign/sign/info",
            Accept: "*/*",
            ContentType: "application/json",
            Nonce: "6378df64-1244-4041-b778-0c6a9cc08c03",
            Timestamp: "1789787162758",
            Expected: "VOn3+1fMBXsmn8X39pBOAHXpU5eQRwJomiqNwFaQbrM="),
    ];

    /// <summary>
    /// 用黄金样本回归签名算法。任一样本对不上，说明签名实现被改坏了
    /// （改动顺序、大小写、分隔符、Content-Type 取值等），必须修回。
    /// </summary>
    public static (bool Ok, List<string> Lines) Run()
    {
        var ok = true;
        var lines = new List<string>();

        foreach (var sample in H5Cases)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = sample.Accept,
                ["Content-Type"] = sample.ContentType,
                ["X-Ca-Key"] = SignProfile.H5.Key,
                ["X-Ca-Nonce"] = sample.Nonce,
                ["X-Ca-Signature-Method"] = "HmacSHA256",
                ["X-Ca-Timestamp"] = sample.Timestamp,
            };

            var payload = LynkSignature.BuildStringToSign(SignProfile.H5, sample.Method, sample.Path, headers);
            var actual = LynkSignature.Sign(SignProfile.H5, payload);

            var pass = string.Equals(actual, sample.Expected, StringComparison.Ordinal);
            ok &= pass;

            lines.Add(pass
                ? $"签名黄金样本 [{sample.Label}]: OK"
                : $"签名黄金样本 [{sample.Label}]: 不一致  期望 {sample.Expected} / 实得 {actual}");
        }

        return (ok, lines);
    }

    /// <summary>
    /// 校验原生通道 profile 的<b>结构不变式</b>。
    ///
    /// <para>
    /// 原生通道的 secret 拿不到，所以无法做端到端验签；但抓包能告诉我们这个通道的
    /// 「签名头集合 / 各头取值 / 端点」长什么样，这些都属于结构信息，可以把它们钉成断言 ——
    /// 避免以后有人图省事把 <c>X-Ca-Version</c> 或 <c>token</c> 从签名头里删掉、
    /// 或者把 <c>Content-Type</c> 改成不带 charset 的写法，静默地把签到改坏。
    /// </para>
    /// </summary>
    public static (bool Ok, List<string> Lines) CheckNativeProfile()
    {
        var lines = new List<string>();
        var problems = new List<string>();

        void Check(bool condition, string description)
        {
            if (condition) lines.Add($"原生通道不变式 [{description}]: OK");
            else problems.Add(description);
        }

        // 用空 secret 构造：结构信息与 secret 无关，且顺便验证「缺 secret 时不可用」这条契约。
        var withoutSecret = SignProfile.Native(key: null, secret: null);
        var withSecret = SignProfile.Native(key: null, secret: "dummy-secret-for-structure-check");

        var declared = withoutSecret.SignatureHeaders
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Check(withoutSecret.SignatureHeaders == LynkConstants.NativeSignatureHeaders, "签名头声明与抓包一致");
        Check(declared.SequenceEqual(declared.OrderBy(n => n, StringComparer.Ordinal)), "签名头声明顺序本身即 ASCII 序");
        Check(declared.Contains("X-Ca-Version"), "X-Ca-Version 参与签名");
        Check(declared.Contains("token"), "token 参与签名");
        Check(withoutSecret.Key == LynkConstants.NativeCaKey, $"key 缺省即抓包实测的 {LynkConstants.NativeCaKey}");
        Check(withoutSecret.CaVersion == LynkConstants.NativeCaVersion, "X-Ca-Version 取值");
        Check(withoutSecret.SignToken, "SignToken 已置位");
        Check(withoutSecret.WithDate, "发送并签名 Date 头");
        Check(withoutSecret.Accept == "application/json", "Accept 取值");
        Check(withoutSecret.ContentType == "application/json; charset=UTF-8", "Content-Type 原样保留空格与大小写");
        Check(LynkConstants.EpSign == "/up/api/v1/user/sign/upgrade", "签到端点与抓包一致");
        Check(!withoutSecret.IsUsable, "缺 secret 时 profile 不可用 (上层据此快速失败)");
        Check(withSecret.IsUsable, "有 secret 时 profile 可用");

        if (problems.Count > 0)
        {
            foreach (var problem in problems) lines.Add($"原生通道不变式 [破坏]: {problem}");
        }

        return (problems.Count == 0, lines);
    }
}
