namespace LynkSign.Lynk;

/// <summary>
/// 一条「签名通道」的完整描述：密钥 + 参与签名的请求头集合 + 头部取值约定。
///
/// <para>
/// 领克 App 同时存在两条签名通道，网关会按 <c>X-Ca-Key</c> 做<b>授权隔离</b> ——
/// 同一条路径在一条通道下可用、在另一条通道下恒为 403，
/// 所以「接口路径」和「用哪条通道签」必须成对修改，只改路径一定会踩坑。
/// </para>
/// <list type="table">
///   <listheader><term>通道</term><description>特征</description></listheader>
///   <item>
///     <term><see cref="H5"/></term>
///     <description>
///       领克 H5 前端（cordova webview）用的通道，key <c>204644386</c>，secret 属公开常量。
///       签名头只有 4 个，<c>Accept: */*</c>，<c>Content-Type: application/json</c>，不发 Date。
///       所有<b>查询类</b>接口（签到状态、连续天数、能量体、任务、分享）走这条。
///     </description>
///   </item>
///   <item>
///     <term><see cref="Native"/></term>
///     <description>
///       原生 iOS SDK 通道，key <c>203760416</c>。签名头 6 个（多 <c>X-Ca-Version</c> 与 <c>token</c>），
///       <c>Accept: application/json</c>，<c>Content-Type: application/json; charset=UTF-8</c>，
///       并发一个 <c>Date</c> 头。<b>签到写入</b>只被授权给这条通道。
///       它的 secret 不在抓包里，必须由用户通过 <c>ca_native_secret</c> 提供；
///       未提供时 <see cref="IsUsable"/> 为 <c>false</c>，调用方应据此快速失败而不是发一个必然被拒的请求。
///     </description>
///   </item>
/// </list>
/// </summary>
/// <param name="Name">通道中文名，仅用于日志与诊断展示。</param>
/// <param name="Key">app key，即 <c>X-Ca-Key</c> 的值。</param>
/// <param name="Secret">app secret，HMAC-SHA256 的密钥。</param>
/// <param name="SignatureHeaders">
/// <c>X-Ca-Signature-Headers</c> 的原样值（声明顺序）。
/// 拼接待签名字符串时会对它按头名 ASCII 序重排，切勿改成直接按声明顺序遍历。
/// </param>
/// <param name="Accept">请求头 <c>Accept</c> 的值。它整串参与签名，属该通道的固有取值。</param>
/// <param name="ContentType">请求头 <c>Content-Type</c> 的值。同样整串参与签名。</param>
/// <param name="UserAgent">该通道固定的 <c>User-Agent</c>；为 null 表示不设置。</param>
/// <param name="CaVersion"><c>X-Ca-Version</c> 的值；为 null 表示不发这个头。</param>
/// <param name="SignToken"><c>token</c> 是否参与签名（原生通道为 true）。</param>
/// <param name="WithDate">是否发送 <c>Date</c> 头（它的值占据签名串第 5 行）。</param>
public sealed record SignProfile(
    string Name,
    string Key,
    string Secret,
    string SignatureHeaders,
    string Accept,
    string ContentType,
    string? UserAgent = null,
    string? CaVersion = null,
    bool SignToken = false,
    bool WithDate = false)
{
    /// <summary>密钥是否齐备。不齐备时不应发起该通道的请求。</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Key) && !string.IsNullOrWhiteSpace(Secret);

    /// <summary>给日志用的一行描述（不含 secret）。</summary>
    public string Describe() => $"{Name} 通道 (X-Ca-Key={Key}, 签名头 {SignatureHeaders.Split(',').Length} 个)";

    /// <summary>H5 通道（公开密钥，所有查询类接口都用它）。</summary>
    public static SignProfile H5 { get; } = new(
        Name: "H5",
        Key: LynkConstants.CaKey,
        Secret: LynkConstants.CaSecret,
        SignatureHeaders: LynkConstants.H5SignatureHeaders,
        Accept: "*/*",
        ContentType: "application/json");

    /// <summary>
    /// 原生 iOS SDK 通道。key 缺省即抓包实测的 <see cref="LynkConstants.NativeCaKey"/>；
    /// secret 必须由调用方从配置里传入，为空时该 profile 不可用。
    /// </summary>
    public static SignProfile Native(string? key, string? secret) => new(
        Name: "原生 iOS SDK",
        Key: string.IsNullOrWhiteSpace(key) ? LynkConstants.NativeCaKey : key.Trim(),
        Secret: (secret ?? "").Trim(),
        SignatureHeaders: LynkConstants.NativeSignatureHeaders,
        Accept: "application/json",
        ContentType: LynkConstants.NativeContentType,
        UserAgent: LynkConstants.NativeUserAgent,
        CaVersion: LynkConstants.NativeCaVersion,
        SignToken: true,
        WithDate: true);
}
