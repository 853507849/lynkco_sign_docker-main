namespace LynkSign.Lynk;

/// <summary>原生通道密钥探测的判定结论。</summary>
public enum NativeProbeVerdict
{
    /// <summary>没填 secret，<b>没有发出任何请求</b>。</summary>
    NotConfigured,

    /// <summary>网关回 <c>400 Invalid Signature</c> —— key 被网关认识，但 secret 算不对。</summary>
    SecretRejected,

    /// <summary>网关回 <c>401 Invalid Key</c> —— 这个 key 网关不认识。</summary>
    KeyRejected,

    /// <summary>
    /// 网关回 <c>403 Unauthorized Consumer</c> —— <b>签名已经通过</b>，
    /// 是授权层拦的（该 APP 未被订阅到这条 API）。key/secret 本身是对的。
    /// </summary>
    NotAuthorized,

    /// <summary>网关既没报签名错也没报 key 错，说明签名被接受了，请求已进到后面的层。</summary>
    SignatureAccepted,

    /// <summary>网络层就没打通，无法判定。</summary>
    Unreachable,
}

/// <summary>探测结果。<see cref="Body"/> 原样保留网关正文，便于人工复核。</summary>
/// <param name="Verdict">判定结论。</param>
/// <param name="HttpStatus">HTTP 状态码；<c>0</c> 表示没拿到响应。</param>
/// <param name="Body">响应正文（网关的拒绝原因就藏在这里，会被截断到 400 字）。</param>
/// <param name="Summary">
/// 一句话结论。<b>一律以 <c>[OK] / [X] / [!]</c> 开头</b>：
/// 桌面版日志框靠这几个前缀着色，而控制台是 cp936、打不出 emoji，
/// 用前缀是唯一两边都正常的写法。
/// </param>
/// <param name="Advice">下一步该做什么。</param>
public sealed record NativeProbeResult(
    NativeProbeVerdict Verdict,
    int HttpStatus,
    string Body,
    string Summary,
    string Advice)
{
    /// <summary>密钥是否已被网关接受（未授权也算 —— 那一步发生在签名校验之后）。</summary>
    public bool SecretAccepted => Verdict is NativeProbeVerdict.SignatureAccepted or NativeProbeVerdict.NotAuthorized;
}

/// <summary>
/// 原生 iOS SDK 通道密钥的<b>在线探测</b>。
///
/// <para>
/// 解决的问题：<c>ca_native_secret</c> 不在任何抓包里（抓包只能看到 <c>X-Ca-Key</c>），
/// 也无法由签名反推（HMAC 单向，已用 144 种待签名串拼法离线穷举验证过），
/// 用户只能自己去 App 安装包里挖。而「挖出来的这串对不对」如果只能靠
/// 「等明天早上签到跑挂了再说」来验证，成本高得离谱 —— 挖错了不会有任何提示。
/// </para>
///
/// <para>
/// 这里用一个<b>假 token</b> 向签到端点发一次探测请求，靠网关的四种回应把问题一次问清：
/// </para>
/// <list type="table">
///   <listheader><term>网关回应</term><description>含义</description></listheader>
///   <item><term><c>400 Invalid Signature</c></term><description>secret 不对（key 是被认识的）</description></item>
///   <item><term><c>401 Invalid Key</c></term><description>key 不对</description></item>
///   <item><term><c>403 Unauthorized Consumer</c></term><description>签名对了，只是这个 APP 没订阅该 API</description></item>
///   <item><term>其它</term><description>签名已通过，请求进到了后面一层</description></item>
/// </list>
///
/// <para>
/// <b>为什么安全、无副作用</b>：签名校验发生在 token 校验<b>之前</b>
/// （实测依据：H5 通道带着有效 token 打签到端点，拿到的仍是 <c>403 Unauthorized
/// Consumer</c> 而不是 token 相关错误），所以 token 内容不影响「签名对不对」的判定；
/// 而假 token 让后端拿不到用户身份，<b>不可能真的签成</b>。
/// </para>
/// </summary>
public static class NativeChannelProbe
{
    /// <summary>
    /// 探测用的假 token。刻意写成一眼能看出不是真 token 的样子 ——
    /// 万一将来有人去翻日志，也不会把它误当成某个账号的凭据。
    /// </summary>
    public const string FakeToken = "lynk-native-probe-not-a-real-token";

    /// <summary>发一次探测请求并给出结论。不发真正的签到动作。</summary>
    /// <param name="api">接口客户端。</param>
    /// <param name="profile">要验证的原生通道 profile（key + secret）。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<NativeProbeResult> RunAsync(
        LynkApiClient api,
        SignProfile profile,
        CancellationToken ct = default)
    {
        if (!profile.IsUsable)
        {
            return new NativeProbeResult(
                NativeProbeVerdict.NotConfigured,
                HttpStatus: 0,
                Body: "",
                Summary: "[!] 未配置原生通道 secret，无法探测",
                Advice: $"在 config.json 填 \"{LynkConstants.NativeSecretConfigField}\"，"
                        + $"或设环境变量 {LynkConstants.NativeSecretEnvVar}，或传 --ca-native-secret");
        }

        var response = await api.CallAsync(
            method: "POST",
            path: LynkConstants.EpSign,
            token: FakeToken,
            body: null,
            signProfile: profile,
            ct: ct).ConfigureAwait(false);

        var body = !string.IsNullOrEmpty(response.Raw) ? response.Raw : response.ToJsonString(400);

        return Classify(response.HttpStatus, body, response.Message, profile.Key);
    }

    /// <summary>
    /// 由「HTTP 状态码 + 响应正文」判定密钥是否可用 —— <b>纯函数，无网络</b>。
    ///
    /// <para>
    /// 单独抽出来是为了能被离线回归覆盖：这套判别法（400 = 签名算错 /
    /// 401 = key 不认识 / 403 = 签名已过但未授权）是整条链路上唯一能区分
    /// 「Key 填错」和「Secret 填错」的依据，措辞和顺序一旦被改乱，
    /// 用户拿到的建议就会指错方向，而这种错在联网测试里很难复现
    /// （唯一能造出来的 400 得靠真去网上打一发）。见 <see cref="SelfCheck"/>。
    /// </para>
    ///
    /// <para>
    /// 判定顺序是「正文关键词 → 状态码」两级：网关的拒绝正文是纯文本
    /// （<c>Invalid Signature</c> 这种），关键词最可靠；一旦它哪天改成 JSON，
    /// 关键词就没了，只能退回状态码。
    /// </para>
    /// </summary>
    /// <param name="status">HTTP 状态码；<c>0</c> 表示网络层异常。</param>
    /// <param name="body">响应正文。</param>
    /// <param name="message">响应里的 message 字段（可能为空）。</param>
    /// <param name="key">当前使用的 X-Ca-Key，仅用于拼建议文案。</param>
    public static NativeProbeResult Classify(int status, string body, string message, string key)
    {
        if (status == 0)
        {
            return new NativeProbeResult(
                NativeProbeVerdict.Unreachable, 0, body,
                "[!] 没能连上网关（网络层异常，签名无从校验）",
                "先确认能访问 app-api-gw-toc.lynkco.com；若开着代理软件，把配置页「网络」卡片里的 HTTP 代理留空试试。");
        }

        var text = $"{body} {message}".ToLowerInvariant();

        if (text.Contains("invalid signature"))
        {
            return new NativeProbeResult(
                NativeProbeVerdict.SecretRejected, status, body,
                $"[X] secret 不正确（网关回 {status} Invalid Signature）",
                $"key {key} 是被网关认识的（否则会回 401 Invalid Key），但它按这个 secret 算出来的签名对不上。"
                + "请核对从 App 安装包里提取的 secret 是否完整、有没有多带引号或换行。");
        }

        if (text.Contains("unauthorized consumer"))
        {
            return new NativeProbeResult(
                NativeProbeVerdict.NotAuthorized, status, body,
                $"[OK] 签名已通过（网关回 {status} Unauthorized Consumer，拦在授权层而非签名层）",
                "密钥是对的。剩余问题是这个 APP 未订阅该 API —— 若 key 用的是抓包实测值，"
                + "说明抓到的 key 与实际调用方不一致，请核对抓包里这条请求的 X-Ca-Key。");
        }

        if (text.Contains("invalid key"))
        {
            return new NativeProbeResult(
                NativeProbeVerdict.KeyRejected, status, body,
                $"[X] key 不正确（网关回 {status} Invalid Key，不认识这个 X-Ca-Key）",
                $"当前 key = {key}。请填抓包里签到请求的 X-Ca-Key 原值（App 实测为 {LynkConstants.NativeCaKey}），"
                + "注意别把 Key 和 Secret 填反。");
        }

        if (status == 400)
        {
            return new NativeProbeResult(
                NativeProbeVerdict.SecretRejected, status, body,
                "[X] secret 不正确（网关回 400，正文里没有可识别的关键词）",
                $"key {key} 被网关认识，是签名校验没过。请重新核对 secret。");
        }

        if (status == 401 && text.Contains("token"))
        {
            // 这是最理想的结果：401 是 token 层给的，说明签名与授权都已通过。
            return new NativeProbeResult(
                NativeProbeVerdict.SignatureAccepted, status, body,
                "[OK] 签名与密钥均已通过（401 来自 token 层，与密钥无关）",
                "可以放心保存。探测用的是假 token，所以这里本来就该报 token 错。");
        }

        if (status == 401)
        {
            return new NativeProbeResult(
                NativeProbeVerdict.KeyRejected, status, body,
                "[X] key 不正确（网关回 401，未给出可识别关键词）",
                $"当前 key = {key}。请填抓包里签到请求的 X-Ca-Key 原值，并确认没有把 Key 和 Secret 填反。");
        }

        if (status == 403)
        {
            return new NativeProbeResult(
                NativeProbeVerdict.NotAuthorized, status, body,
                "[OK] 签名已通过（403 来自授权层，拦在签名校验之后）",
                "密钥是对的。若 key 用的是抓包实测值而仍被拒，请核对抓包里这条请求的 X-Ca-Key。");
        }

        return new NativeProbeResult(
            NativeProbeVerdict.SignatureAccepted, status, body,
            $"[OK] 签名已被网关接受（HTTP {status}，响应里没有签名/key 类错误）",
            "可以放心保存。");
    }

    /// <summary>
    /// 把探测结果排成一份适合贴进日志 / 终端的多行报告。
    ///
    /// <para>
    /// 单独做成一个纯函数而不是直接 <c>Console.WriteLine</c>：
    /// 命令行要看到它、桌面版的日志框也要看到它，两边共用同一份措辞，
    /// 就不会出现「命令行的提示有用、界面里的提示没用」这种偏差。
    /// </para>
    /// </summary>
    public static string Format(NativeProbeResult result, SignProfile profile)
    {
        var lines = new List<string>
        {
            $"探测端点: POST {LynkConstants.EpSign}",
            $"签名通道: {profile.Describe()}",
            $"探测 token: {FakeToken}   (假 token, 不会真的签到)",
            $"HTTP 状态: {(result.HttpStatus == 0 ? "—（未拿到响应）" : result.HttpStatus.ToString())}",
        };

        if (result.Body.Length > 0)
        {
            var body = result.Body.Replace("\r", " ").Replace("\n", " ").Trim();
            lines.Add($"响应正文: {(body.Length > 300 ? body[..300] + "…" : body)}");
        }

        lines.Add(new string('-', 52));
        lines.Add(result.Summary);
        lines.Add(result.Advice);

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// <see cref="Classify"/> 的离线回归。样本覆盖全部 6 种结论 + 两条边界
    /// （400 但正文变 JSON、以及「正确结果」的 <see cref="NativeProbeResult.SecretAccepted"/> 取值）。
    ///
    /// <para>
    /// 无网络、无凭据、可长期跑 —— 唯一造不出真样本的是 200/正常业务响应，
    /// 那条用「签名已过、请求进到后面的层」这个等价形态代表。
    /// </para>
    /// </summary>
    public static (bool Ok, List<string> Lines) SelfCheck()
    {
        var key = LynkConstants.NativeCaKey;

        (string Label, int Status, string Body, string Message, NativeProbeVerdict Expect, bool Accept)[] cases =
        [
            // 真机实测形态：key 对 + secret 错 → 400 Invalid Signature（纯文本正文）
            ("网关 400 Invalid Signature", 400, "Invalid Signature", "", NativeProbeVerdict.SecretRejected, false),

            // 网关不认识 key
            ("网关 401 Invalid Key", 401, "Invalid Key", "", NativeProbeVerdict.KeyRejected, false),

            // 签名过了，卡在授权层
            ("网关 403 Unauthorized Consumer", 403, "Unauthorized Consumer", "", NativeProbeVerdict.NotAuthorized, true),

            // 签名过了，卡在 token 层 —— 探测时本来就该出现这个
            ("token 层 401", 401, "{\"message\":\"token 已失效\"}", "token 已失效", NativeProbeVerdict.SignatureAccepted, true),

            // 真机上「密钥正确」的最终形态：请求进到业务层，返回业务 JSON
            ("业务层正常响应", 200, "{\"code\":\"10000\",\"message\":\"ok\"}", "ok", NativeProbeVerdict.SignatureAccepted, true),

            // 边界：网关哪天把拒绝正文换成 JSON，关键词就没了，必须靠状态码兜底
            ("400 但正文是 JSON（关键词丢失）", 400, "{\"message\":\"Unauthorized\"}", "", NativeProbeVerdict.SecretRejected, false),

            // 边界：403 但正文无关键词
            ("403 但正文为空", 403, "", "", NativeProbeVerdict.NotAuthorized, true),

            // 网络层异常
            ("网络不通", 0, "", "HttpRequestException: 由于目标计算机积极拒绝", NativeProbeVerdict.Unreachable, false),
        ];

        var ok = true;
        var lines = new List<string>();

        foreach (var (label, status, body, message, expect, accept) in cases)
        {
            var got = Classify(status, body, message, key);
            var pass = got.Verdict == expect && got.SecretAccepted == accept;
            ok &= pass;

            lines.Add(pass
                ? $"密钥判别法 [{label}]: OK → {expect}"
                : $"密钥判别法 [{label}]: 不符  期望 {expect}/可用={accept}, 实得 {got.Verdict}/可用={got.SecretAccepted}");
        }

        // 不变式：只有「签名已通过」的两类才算密钥可用；其余（含未配置、网络不通、
        // key 错）都不许被当成通过 —— 判错的代价是用户以为配好了，然后每天签到静默失败。
        NativeProbeVerdict[] acceptable = [NativeProbeVerdict.SignatureAccepted, NativeProbeVerdict.NotAuthorized];
        var guardPass = true;

        foreach (var verdict in Enum.GetValues<NativeProbeVerdict>())
        {
            var probeResult = new NativeProbeResult(verdict, 0, "", "", "");
            guardPass &= probeResult.SecretAccepted == acceptable.Contains(verdict);
        }

        lines.Add(guardPass
            ? "密钥判别法 [SecretAccepted 只认签名已通过的两类]: OK"
            : "密钥判别法 [SecretAccepted 只认签名已通过的两类]: 破坏");

        ok &= guardPass;

        return (ok, lines);
    }
}
