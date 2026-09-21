using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LynkSign.Lynk;

/// <summary>
/// 接口签名（阿里云 API 网关 X-Ca-* 规范）。逐行对齐 Python 版的 <c>build_sig()</c>，
/// 并按「签名通道」参数化 —— 领克 App 有两条通道，头部集合与取值都不同，详见 <see cref="SignProfile"/>。
///
/// <para>待签名字符串（用 <c>\n</c> 连接）：</para>
/// <code>
/// METHOD
/// {Accept}
/// {Content-MD5}          (恒为空)
/// {Content-Type}
/// {Date}                 (不发则为空)
/// X-Ca-Key:{key}
/// X-Ca-Nonce:{nonce}
/// X-Ca-Signature-Method:HmacSHA256
/// X-Ca-Timestamp:{ms}
/// [X-Ca-Version:{v}]     (仅原生通道)
/// [token:{token}]        (仅原生通道)
/// {path}[?{k=v&amp;...}]
/// </code>
///
/// ⚠️ 四个容易踩坑的细节：
/// <list type="number">
///   <item>
///     参与签名的头必须 <b>按头名 ASCII 序</b> 排列（大写字母在小写字母之前），
///     而不是按 <c>X-Ca-Signature-Headers</c> 里声明的顺序去遍历。
///     H5 通道两者恰好一致，所以这个坑平时看不出来；原生通道里 <c>token</c> 参与签名，
///     顺序一旦搞错就是恒 400 Invalid Signature。本文件已用抓包黄金样本回归验证（见 <see cref="SignatureSelfCheck"/>）。
///   </item>
///   <item>
///     <c>Content-Type</c> 的值 <b>整串</b>参与签名 —— 原生通道要保留分号后的空格与
///     <c>UTF-8</c> 的大小写，写成 <c>application/json</c> 或补上 <c>; charset=utf-8</c> 都会签名不匹配。
///   </item>
///   <item>签名串里没有独立的 query 行，GET 参数是拼进 path 的（按 key 字典序，k=v 形式）。</item>
///   <item><c>X-Ca-Signature-Headers</c> 自身不参与签名，它只是告诉网关「哪些头被签了」。</item>
/// </list>
/// </summary>
public static class LynkSignature
{
    /// <summary>生成大写 UUID 形式的 nonce（等价 Python 的 <c>str(uuid.uuid4()).upper()</c>）。</summary>
    public static string NewNonce() => Guid.NewGuid().ToString().ToUpperInvariant();

    /// <summary>当前毫秒时间戳字符串。</summary>
    public static string NewTimestamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

    /// <summary>
    /// 按 App 的写法生成 <c>Date</c> 头。抓包实测格式是 <c>Sat, 19 Sep 2026 11:06:01 GMT+8</c>
    /// —— 注意这不是标准 HTTP-date（标准写法应为 <c>... GMT</c>），但网关接受，且它整串参与签名，
    /// 所以必须照抄格式而不是用 <c>DateTimeOffset.ToString("R")</c>。
    ///
    /// <para>只观察过整小时偏移（GMT+8）；非整小时时按 <c>GMT+530</c> 这种紧凑写法拼接。</para>
    /// </summary>
    public static string NewDateHeader()
    {
        var now = DateTimeOffset.Now;
        var offset = now.Offset;

        var zone = offset == TimeSpan.Zero
            ? "GMT"
            : $"GMT{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours)}"
              + (offset.Minutes == 0 ? "" : Math.Abs(offset.Minutes).ToString("00", CultureInfo.InvariantCulture));

        return now.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + zone;
    }

    /// <summary>
    /// 拼装待签名字符串。
    ///
    /// <para>
    /// 拆出来单独暴露是为了能拿抓包里的历史 (nonce, timestamp) 离线回归算法 ——
    /// 生产路径只经由 <see cref="Build"/> 调用它，两边永远是同一套代码。
    /// </para>
    /// </summary>
    /// <param name="profile">通道（决定签名头集合与取值约定）。</param>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="pathAndQuery">路径，含 query（GET 参数拼进来的形式）。</param>
    /// <param name="headers">本次请求实际的头部取值；键名大小写不敏感。</param>
    public static string BuildStringToSign(
        SignProfile profile,
        string method,
        string pathAndQuery,
        IReadOnlyDictionary<string, string> headers)
    {
        var parts = new List<string>(16)
        {
            method.ToUpperInvariant(),
            Header(headers, "Accept"),
            Header(headers, "Content-MD5"),
            Header(headers, "Content-Type"),
            Header(headers, "Date"),
        };

        // 按头名 ASCII 序（Ordinal）排列 —— 不是按 profile.SignatureHeaders 的声明顺序
        var signedNames = profile.SignatureHeaders
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var name in signedNames)
        {
            parts.Add($"{name}:{Header(headers, name)}");
        }

        parts.Add(pathAndQuery);
        return string.Join("\n", parts);
    }

    /// <summary>HMAC-SHA256 后转 Base64，即 <c>X-Ca-Signature</c> 的值。</summary>
    public static string Sign(SignProfile profile, string payload)
        => Convert.ToBase64String(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(profile.Secret),
            Encoding.UTF8.GetBytes(payload)));

    /// <summary>
    /// 生成该通道本次请求要用到的全部头部（含 <c>X-Ca-Signature</c>）。
    ///
    /// <para>
    /// 返回值里 <b>同时包含</b> <c>Content-Type</c> 与 <c>token</c>：
    /// 前者由调用方转到 content headers（<c>HttpRequestMessage.Headers</c> 会静默丢弃它），
    /// 后者是统一发放、避免调用方重复添加导致多值。
    /// </para>
    /// </summary>
    /// <param name="profile">通道。</param>
    /// <param name="method">HTTP 方法（GET / POST）。</param>
    /// <param name="path">接口路径，如 <c>/up/api/v1/user/sign/upgrade</c>。</param>
    /// <param name="token">access token；部分通道要求它参与签名。</param>
    /// <param name="queryParams">GET 查询参数；为 null 或空则不拼接 query。</param>
    public static Dictionary<string, string> Build(
        SignProfile profile,
        string method,
        string path,
        string token,
        IReadOnlyDictionary<string, string>? queryParams = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Ca-Key"] = profile.Key,
            ["X-Ca-Nonce"] = NewNonce(),
            ["X-Ca-Signature-Method"] = "HmacSHA256",
            ["X-Ca-Timestamp"] = NewTimestamp(),
            ["Accept"] = profile.Accept,
            // ⚠️ Content-Type 只能通过 Content headers 落到请求里
            //    （塞进 HttpRequestMessage.Headers 会被 .NET 静默丢弃），调用方负责转交。
            ["Content-Type"] = profile.ContentType,
            ["token"] = token ?? string.Empty,
        };

        if (profile.CaVersion is { Length: > 0 } version)
        {
            headers["X-Ca-Version"] = version;
        }

        if (profile.WithDate)
        {
            headers["Date"] = NewDateHeader();
        }

        var url = path;
        if (queryParams is { Count: > 0 })
        {
            var query = string.Join("&", queryParams
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            if (query.Length > 0) url = $"{path}?{query}";
        }

        headers["X-Ca-Signature"] = Sign(profile, BuildStringToSign(profile, method, url, headers));
        headers["X-Ca-Signature-Headers"] = profile.SignatureHeaders;

        return headers;
    }

    private static string Header(IReadOnlyDictionary<string, string> headers, string name)
        => headers.TryGetValue(name, out var value) ? value : string.Empty;
}
