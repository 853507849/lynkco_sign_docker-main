using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LynkSign.Infrastructure;

namespace LynkSign.Lynk;

/// <summary>
/// 从抓包文件（HAR）里认出的一次登录所对应的凭据。
/// </summary>
/// <param name="RefreshToken">响应体 <c>data.centerTokenDto.refreshToken</c> 的原样值（自带 <c>bearer</c> 前缀）。</param>
/// <param name="DeviceId">登录请求 query 里的 <c>deviceId</c>（32 位十六进制）。</param>
/// <param name="Mobile">账号手机号，仅用于让人确认「导的是不是自己的号」，可能为空。</param>
/// <param name="Nickname">账号昵称，同上，可能为空。</param>
public sealed record HarLoginCredential(string RefreshToken, string DeviceId, string Mobile, string Nickname)
{
    /// <summary>日志里只露尾巴，避免把完整 token 写进日志文件。</summary>
    public string TokenTail => RefreshToken.Length <= 12 ? RefreshToken : "..." + RefreshToken[^12..];

    /// <summary>手机号脱敏（176****2914），用于界面上一眼确认账号对不对。</summary>
    public string MaskedMobile
        => Mobile.Length >= 7 ? $"{Mobile[..3]}****{Mobile[^4..]}" : Mobile;

    /// <summary>界面 / 日志用的一句话说明。</summary>
    public string Describe()
    {
        var who = MaskedMobile.Length > 0 ? $"账号 {MaskedMobile}" : "账号（手机号未解析到）";
        if (Nickname.Length > 0) who += $" ({Nickname})";
        return $"{who}, refreshToken={TokenTail}";
    }
}

/// <summary>
/// <see cref="HarCredentialExtractor.Scan(JsonElement)"/> 的结果。
/// </summary>
/// <param name="Credential">解析出的凭据；失败时为 <c>null</c>。</param>
/// <param name="MatchedEntries">抓包里命中登录接口的请求条数（0 表示抓错了 / 没抓到登录）。</param>
/// <param name="Error">失败原因（面向用户的一段中文说明）。</param>
public sealed record HarCredentialScan(HarLoginCredential? Credential, int MatchedEntries, string? Error)
{
    public bool Ok => Credential is not null;
}

/// <summary>
/// 从浏览器 / 手机抓包导出的 <b>HAR</b> 文件里，把登录凭据（refreshToken + deviceId）抠出来。
///
/// <para>
/// 定位的接口是领克 APP 的短信验证码登录：
/// <c>POST https://app-services.lynkco.com.cn/auth/login/mobileCodeLogin?deviceId=…</c>，
/// 两个值分别来自<b>请求的 query</b>（<c>deviceId</c>）与<b>响应体</b>
/// （<c>data.centerTokenDto.refreshToken</c>）。
/// </para>
///
/// <para>
/// 为什么值得做成一个功能：这两个值必须<b>成对且同源</b> —— deviceId 参与换 token 的请求，
/// 与 refreshToken 配错就是 401；而手工从几十条请求里翻 JSON、再整串复制 42 个字符的 token，
/// 是最容易出错、也最劝退的一步。抓包本身已经是用户绕不开的操作，那就让它一步到位。
/// </para>
///
/// <para>
/// 几个刻意的取舍：
/// <list type="bullet">
///   <item><b>只认请求方法 + 路径</b>，不校验域名 —— 测试环境换个域名也照样能用；</item>
///   <item><b>deviceId 严格等于 <c>deviceId</c></b>，绝不退化成前缀匹配 ——
///         query 里还有个 <c>hardwareDeviceId</c>（iOS 的设备标识，不是这个值），
///         取错了登录能过、换 token 必失败，且报错完全指不到原因；</item>
///   <item>响应体可能是 <c>base64</c> 编码、甚至还是 gzip/br 压缩的（部分抓包工具不解压），
///         所以解码后还要试一遍常见压缩格式；</item>
///   <item>抓包里若有多条登录（换号重登、反复登录），<b>取最后一条</b> ——
///         时间上最新的那条才是当前有效的。</item>
/// </list>
/// </para>
/// </summary>
public static class HarCredentialExtractor
{
    /// <summary>登录接口路径（不含域名与 query）。</summary>
    public const string LoginPath = "/auth/login/mobileCodeLogin";

    /// <summary>从抓包文件读取。</summary>
    public static HarCredentialScan ScanFile(string harPath)
    {
        try
        {
            if (!File.Exists(harPath))
                return new HarCredentialScan(null, 0, $"文件不存在: {harPath}");

            using var stream = File.OpenRead(harPath);
            using var doc = JsonDocument.Parse(stream);
            return Scan(doc.RootElement);
        }
        catch (JsonException ex)
        {
            return new HarCredentialScan(null, 0, $"不是合法的 HAR / JSON 文件: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new HarCredentialScan(null, 0, $"读取失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>从已解析的 HAR 根节点扫描（<c>log.entries[]</c>）。</summary>
    public static HarCredentialScan Scan(JsonElement harRoot)
    {
        var entries = harRoot.Prop("log").Prop("entries");
        if (entries is not { ValueKind: JsonValueKind.Array })
            return new HarCredentialScan(null, 0, "不是有效的 HAR：缺少 log.entries 数组");

        HarLoginCredential? found = null;
        string? lastError = null;
        var matched = 0;

        foreach (var entry in entries.Value.EnumerateArray())
        {
            var request = entry.Prop("request");
            if (!IsLoginRequest(request)) continue;

            matched++;

            var deviceId = ReadDeviceId(request);
            var (token, mobile, nickname, error) = ReadCredential(entry.Prop("response"));

            if (token.Length == 0)
            {
                lastError = error ?? "响应里没有 refreshToken";
                continue;
            }

            if (deviceId.Length == 0)
            {
                lastError = "这条登录请求里没有 deviceId（可能抓包工具裁剪了 query）";
                continue;
            }

            // 不 break：多条登录时保留最后一条
            found = new HarLoginCredential(token, deviceId, mobile, nickname);
        }

        if (matched == 0)
        {
            return new HarCredentialScan(null, 0,
                $"抓包里没有 POST {LoginPath} 请求 —— 请确认导出的是登录那一段，"
                + "并且抓包时确实完成了一次「短信验证码登录」");
        }

        if (found is null)
            return new HarCredentialScan(null, matched, lastError ?? "未能从登录响应里解析出 refreshToken");

        return new HarCredentialScan(found, matched, null);
    }

    // ==================================================================
    //  定位与取值
    // ==================================================================

    private static bool IsLoginRequest(JsonElement? request)
    {
        if (request is not { ValueKind: JsonValueKind.Object }) return false;

        if (!string.Equals(request.Value.Str("method"), "POST", StringComparison.OrdinalIgnoreCase))
            return false;

        var url = request.Value.Str("url");
        if (url.Length == 0) return false;

        // 去掉 query 与 fragment 后只比路径；域名不参与判断（测试环境域名不同也应能用）
        var path = url;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0) path = path[..cut];
        path = path.TrimEnd('/');

        return path.EndsWith(LoginPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 取 deviceId：先看 <c>request.queryString</c>，没有则回头从 URL 上解析。
    ///
    /// <para>
    /// 严格全等比较是这里的关键 —— query 里同时存在 <c>deviceId</c> 与
    /// <c>hardwareDeviceId</c>，一旦写成「包含 / 前缀匹配」就会取到后者，
    /// 而那个值只在登录时用，拿去换 token 会被拒。
    /// </para>
    /// </summary>
    private static string ReadDeviceId(JsonElement? request)
    {
        foreach (var q in request.Prop("queryString").Items())
        {
            if (!string.Equals(q.Str("name"), "deviceId", StringComparison.OrdinalIgnoreCase)) continue;
            var value = q.Str("value").Trim();
            if (value.Length > 0) return value;
        }

        return QueryValue(request.Str("url"), "deviceId");
    }

    /// <summary>从 URL 的 query 里取一个参数（手写解析：抓包里的 URL 未必能被 <see cref="Uri"/> 接受）。</summary>
    private static string QueryValue(string url, string name)
    {
        var cut = url.IndexOf('?');
        if (cut < 0) return "";

        var query = url[(cut + 1)..];
        var hash = query.IndexOf('#');
        if (hash >= 0) query = query[..hash];

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;

            if (!string.Equals(Unescape(pair[..eq]), name, StringComparison.OrdinalIgnoreCase)) continue;

            var value = Unescape(pair[(eq + 1)..]).Trim();
            if (value.Length > 0) return value;
        }

        return "";
    }

    private static string Unescape(string raw)
    {
        try { return Uri.UnescapeDataString(raw); }
        catch { return raw; }
    }

    /// <summary>从响应体里读出 refreshToken（附带手机号 / 昵称，供界面确认账号）。</summary>
    private static (string Token, string Mobile, string Nickname, string? Error) ReadCredential(JsonElement? response)
    {
        var text = ReadBodyText(response.Prop("content"));
        if (text.Length == 0)
            return ("", "", "", "抓包里这条请求没有保存响应体（导出时未勾选 response body）");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return ("", "", "", "响应体不是合法 JSON（可能仍是压缩状态，或导出时被截断）");
        }

        using (doc)
        {
            var root = doc.RootElement;

            var code = root.Str("code");
            if (code.Length > 0 && !string.Equals(code, "success", StringComparison.OrdinalIgnoreCase))
            {
                var message = root.Str("message");
                return ("", "", "", $"这次登录没有成功：code={code}{(message.Length > 0 ? $"（{message}）" : "")}");
            }

            var data = root.Prop("data");

            var token = data.Prop("centerTokenDto").Str("refreshToken").Trim();
            if (token.Length == 0)
            {
                // 结构变动兜底：整个响应里找第一个叫 refreshToken 的字符串字段。
                // 键名不会歧义（token / cepToken / svcToken 都是另外的名字），所以这样找是安全的。
                token = FindStringByKey(root, "refreshToken");
            }

            if (token.Length == 0)
                return ("", "", "", "响应里找不到 refreshToken（接口结构可能已变动）");

            var user = data.Prop("centerUserInfoDto");
            return (token, user.Str("mobile").Trim(), user.Str("displayName").Trim(), null);
        }
    }

    /// <summary>
    /// 读响应体文本：处理 <c>content.encoding = "base64"</c>（解码后还可能是 gzip/deflate/br）。
    /// </summary>
    private static string ReadBodyText(JsonElement? content)
    {
        var text = content.Str("text");
        if (text.Length == 0) return "";

        var encoding = content.Str("encoding");
        if (!string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase)) return text;

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(text.Trim());
        }
        catch (FormatException)
        {
            // 标了 base64 却不是合法 base64：按原文当 JSON 试，比直接失败更有用
            return text;
        }

        return TryDecompress(raw) ?? Encoding.UTF8.GetString(raw);
    }

    /// <summary>按魔数 / 逐个尝试解开常见压缩格式；解出来不是 JSON 就认为猜错了。</summary>
    private static string? TryDecompress(byte[] raw)
    {
        if (raw.Length > 2 && raw[0] == 0x1F && raw[1] == 0x8B)
            return TryInflate(raw, s => new GZipStream(s, CompressionMode.Decompress));

        // Brotli 没有魔数，只能试；顺序按 HTTP 响应里的常见度排
        return TryInflate(raw, s => new BrotliStream(s, CompressionMode.Decompress))
            ?? TryInflate(raw, s => new ZLibStream(s, CompressionMode.Decompress))
            ?? TryInflate(raw, s => new DeflateStream(s, CompressionMode.Decompress));
    }

    private static string? TryInflate(byte[] raw, Func<Stream, Stream> wrap)
    {
        try
        {
            using var input = new MemoryStream(raw, writable: false);
            using var inflate = wrap(input);
            using var output = new MemoryStream();
            inflate.CopyTo(output);

            var text = Encoding.UTF8.GetString(output.ToArray()).TrimStart();
            return text.Length > 0 && (text[0] == '{' || text[0] == '[') ? text : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>深度优先找第一个名为 <paramref name="key"/> 的非空字符串值（键名不区分大小写）。</summary>
    private static string FindStringByKey(JsonElement element, string key, int depth = 0)
    {
        if (depth > 12) return "";

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // 先扫本层的键，再下探 —— 避免深层同名字段抢在前面的正确值之前被取到
                foreach (var p in element.EnumerateObject())
                {
                    if (!string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.Value.ValueKind != JsonValueKind.String) continue;

                    var value = (p.Value.GetString() ?? "").Trim();
                    if (value.Length >= 8) return value;
                }

                foreach (var p in element.EnumerateObject())
                {
                    var hit = FindStringByKey(p.Value, key, depth + 1);
                    if (hit.Length > 0) return hit;
                }
                return "";

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var hit = FindStringByKey(item, key, depth + 1);
                    if (hit.Length > 0) return hit;
                }
                return "";

            default:
                return "";
        }
    }

    // ==================================================================
    //  离线回归（--selftest）
    // ==================================================================

    /// <summary>
    /// 用内嵌样本把解析规则钉住，<b>不需要网络、不需要真实抓包文件</b>。
    ///
    /// <para>
    /// 这段逻辑出错的表现都是「导入后签到失败」，而失败原因（deviceId 取成了
    /// hardwareDeviceId、响应体没解压、取到了上一次登录的旧 token）在表面上完全看不出来 ——
    /// 用户只会看到一句 401。所以把各种抓包形态做成样本，在这里一次性钉死。
    /// </para>
    /// </summary>
    public static (bool Ok, List<string> Lines) SelfCheck()
    {
        const string Dev1 = "8d73c6bd3b8d4f90b23e5d8fa86b50b1";
        const string Dev2 = "aaaabbbbccccdddd1111222233334444";
        const string UrlDev1 = LoginUrlBase + "?deviceId=" + Dev1 + "&appVersion=4.2.7&mobile=17682302914";
        const string UrlDev2 = LoginUrlBase + "?deviceId=" + Dev2 + "&deviceType=IOS&hardwareDeviceId=5C933CEF-1CE7-4E8F-A84A-2F0C6795CF08";
        const string UrlNoDev = LoginUrlBase + "?appVersion=4.2.7&hardwareDeviceId=5C933CEF-1CE7-4E8F-A84A-2F0C6795CF08";
        const string Token1 = "bearer7bf1101e-ef6d-4115-9e85-b87e6c26dcb9";

        var lines = new List<string>();
        var ok = true;

        void Check(string label, bool pass, string detail = "")
        {
            if (!pass) ok = false;
            lines.Add($"{(pass ? "  [OK] " : "  [X]  ")}{label}{(detail.Length > 0 ? $" → {detail}" : "")}");
        }

        // ---- 1) 标准样本：与真机抓包同形（含 deviceId 与 hardwareDeviceId 并存）----
        var s = Scan(Parse(BuildHar(new EntrySpec("POST", UrlDev1, Dev1, LoginBody(Token1)))));
        Check("标准登录样本", s.Ok && s.Credential!.RefreshToken == Token1 && s.Credential.DeviceId == Dev1,
            s.Ok ? s.Credential!.Describe() : s.Error ?? "");
        Check("  账号信息（手机号 / 昵称）", s.Credential?.Mobile == "17682302914" && s.Credential.Nickname == "登录账号");

        // ---- 2) deviceId 不得取成 hardwareDeviceId ----
        var s2 = Scan(Parse(BuildHar(new EntrySpec("POST", UrlDev2, Dev2, LoginBody(Token1)))));
        Check("deviceId 与 hardwareDeviceId 并存", s2.Credential?.DeviceId == Dev2, s2.Credential?.DeviceId ?? "-");

        // ---- 3) queryString 被裁剪，只能从 URL 解析 ----
        var s3 = Scan(Parse(BuildHar(new EntrySpec("POST", UrlDev1, null, LoginBody(Token1)))));
        Check("queryString 缺失 → 回退解析 URL", s3.Credential?.DeviceId == Dev1, s3.Credential?.DeviceId ?? s3.Error ?? "-");

        // ---- 4) 真的没有 deviceId（只有 hardwareDeviceId）→ 必须失败而不是用错值 ----
        var s4 = Scan(Parse(BuildHar(new EntrySpec("POST", UrlNoDev,
            "5C933CEF-1CE7-4E8F-A84A-2F0C6795CF08", LoginBody(Token1), QueryName: "hardwareDeviceId"))));
        Check("只有 hardwareDeviceId 时判定失败", !s4.Ok && (s4.Error?.Contains("deviceId") ?? false), s4.Error ?? "（不该成功）");

        // ---- 5) 多条登录 → 取最后一条（时间上最新）----
        var s5 = Scan(Parse(BuildHar(
            new EntrySpec("POST", UrlDev1, Dev1, LoginBody("bearerold0-old0-old0-old0")),
            new EntrySpec("POST", UrlDev2, Dev2, LoginBody(Token1)))));
        Check("多条登录取最后一条", s5.Credential?.RefreshToken == Token1 && s5.Credential.DeviceId == Dev2,
            $"匹配 {s5.MatchedEntries} 条, 取到 {s5.Credential?.TokenTail}");
        Check("  命中条数统计", s5.MatchedEntries == 2, s5.MatchedEntries.ToString());

        // ---- 6) 登录失败（code != success）→ 不算凭据 ----
        var s6 = Scan(Parse(BuildHar(new EntrySpec("POST", UrlDev1, Dev1, LoginBody(Token1, code: "fail", message: "验证码错误")))));
        Check("登录失败（code=fail）判定失败", !s6.Ok && (s6.Error?.Contains("验证码错误") ?? false), s6.Error ?? "（不该成功）");

        // ---- 7) content.encoding=base64 + gzip 压缩的响应体 ----
        var s7 = Scan(Parse(BuildHar(new EntrySpec("POST", UrlDev1, Dev1, Encode(LoginBody(Token1)), EncodingValue: "base64"))));
        Check("base64 + gzip 响应体", s7.Credential?.RefreshToken == Token1, s7.Error ?? s7.Credential?.TokenTail ?? "-");

        // ---- 8) content.encoding=base64 但只是明文（未压缩）----
        var s8 = Scan(Parse(BuildHar(new EntrySpec("POST", UrlDev1, Dev1, Encode(LoginBody(Token1), compress: false), EncodingValue: "base64"))));
        Check("base64 明文响应体", s8.Credential?.RefreshToken == Token1, s8.Error ?? s8.Credential?.TokenTail ?? "-");

        // ---- 9) 响应体缺失 ----
        var s9 = Scan(Parse(BuildHar(new EntrySpec("POST", UrlDev1, Dev1, Body: null))));
        Check("响应体缺失 → 明确报原因", !s9.Ok && (s9.Error?.Contains("响应体") ?? false), s9.Error ?? "（不该成功）");

        // ---- 10) 结构变动：refreshToken 直接挂在 data 下 ----
        var s10 = Scan(Parse(BuildHar(new EntrySpec("POST", UrlDev1, Dev1, FlatBody(Token1)))));
        Check("refreshToken 键名兜底（data 直挂）", s10.Credential?.RefreshToken == Token1, s10.Error ?? s10.Credential?.TokenTail ?? "-");

        // ---- 11) 路径大小写不敏感 ----
        var s11 = Scan(Parse(BuildHar(new EntrySpec("POST", LoginUrlBase.ToUpperInvariant() + "?deviceId=" + Dev1, Dev1, LoginBody(Token1)))));
        Check("路径大小写不敏感", s11.Ok, s11.Error ?? "OK");

        // ---- 12) GET 请求不算（登录是 POST）----
        var s12 = Scan(Parse(BuildHar(new EntrySpec("GET", UrlDev1, Dev1, LoginBody(Token1)))));
        Check("GET 同路径不误判", !s12.Ok && s12.MatchedEntries == 0, s12.Error ?? "（不该成功）");

        // ---- 13) 抓包里压根没有登录请求 ----
        var s13 = Scan(Parse(BuildHar(new EntrySpec("POST", "https://app-api-gw-toc.lynkco.com/app/sign/in", null, "{\"code\":\"success\"}"))));
        Check("没有登录请求 → 提示抓错段了", !s13.Ok && s13.MatchedEntries == 0 && (s13.Error?.Contains("没有 POST") ?? false), s13.Error ?? "（不该成功）");

        // ---- 14) 结构不对 / 不是 HAR ----
        var s14 = Scan(Parse("{\"entries\":[]}"));
        Check("非 HAR 结构", !s14.Ok && (s14.Error?.Contains("log.entries") ?? false), s14.Error ?? "（不该成功）");

        // ---- 15) 坏 JSON 不抛异常 ----
        var s15 = Scan(Parse("this is not json at all"));
        Check("坏 JSON 不抛异常", !s15.Ok, s15.Error ?? "（不该成功）");

        // ---- 16) 读取不存在的文件 ----
        var s16 = ScanFile(Path.Combine(Path.GetTempPath(), "lynk-har-does-not-exist-9f3a.har"));
        Check("文件不存在 → 不抛异常", !s16.Ok && (s16.Error?.Contains("不存在") ?? false), s16.Error ?? "（不该成功）");

        return (ok, lines);
    }

    private const string LoginUrlBase = "https://app-services.lynkco.com.cn" + LoginPath;

    private sealed record EntrySpec(string Method, string Url, string? QueryDeviceId, string? Body,
                                    string? EncodingValue = null, string QueryName = "deviceId");

    private static string LoginBody(string refreshToken, string mobile = "17682302914", string nickname = "登录账号",
                                    string code = "success", string message = "")
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message.Length == 0 ? null : message,
            ["data"] = new Dictionary<string, object?>
            {
                ["centerTokenDto"] = new Dictionary<string, object?>
                {
                    ["token"] = "bearer34049bc1-e8e2-42bf-b849-76778ceb62bf",
                    ["cepToken"] = "bearer34049bc1-e8e2-42bf-b849-76778ceb62bf",
                    ["svcToken"] = "bearer34049bc1-e8e2-42bf-b849-76778ceb62bf",
                    ["refreshToken"] = refreshToken,
                    ["expireAt"] = 1789888074150L,
                },
                ["centerUserInfoDto"] = new Dictionary<string, object?>
                {
                    ["id"] = "5608814990304805917",
                    ["mobile"] = mobile,
                    ["displayName"] = nickname,
                },
            },
        });

    /// <summary>结构变动样本：refreshToken 直接挂在 data 下（没有 centerTokenDto 这一层）。</summary>
    private static string FlatBody(string refreshToken)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["code"] = "success",
            ["data"] = new Dictionary<string, object?> { ["refreshToken"] = refreshToken },
        });

    /// <summary>base64（可选再 gzip）编码的响应体，模拟抓包工具不解压的情况。</summary>
    private static string Encode(string body, bool compress = true)
    {
        var raw = Encoding.UTF8.GetBytes(body);
        if (!compress) return Convert.ToBase64String(raw);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(raw);
        }
        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>按 HAR 1.2 的最小结构拼一份抓包，只保留被测代码会读到的字段。</summary>
    private static string BuildHar(params EntrySpec[] specs)
    {
        var entries = specs.Select(s => new Dictionary<string, object?>
        {
            ["request"] = new Dictionary<string, object?>
            {
                ["method"] = s.Method,
                ["url"] = s.Url,
                ["queryString"] = s.QueryDeviceId is null
                    ? null
                    : new[] { new Dictionary<string, object?> { ["name"] = s.QueryName, ["value"] = s.QueryDeviceId } },
            },
            ["response"] = new Dictionary<string, object?>
            {
                ["status"] = 200,
                ["content"] = s.Body is null
                    ? null
                    : new Dictionary<string, object?>
                    {
                        ["mimeType"] = "application/json;charset=UTF-8",
                        ["text"] = s.Body,
                        ["encoding"] = s.EncodingValue,
                    },
            },
        }).ToArray();

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?> { ["entries"] = entries },
        });
    }

    /// <summary>
    /// 解析样本 JSON。<c>Clone()</c> 是必须的：<see cref="JsonDocument"/> 一旦释放，
    /// 它的 <c>RootElement</c> 就指向已经归还的内存，后续读取是未定义行为。
    ///
    /// <para>
    /// 解析失败时返回 <c>default</c>（<see cref="JsonValueKind.Undefined"/>）而不是抛异常 ——
    /// 这样「文件损坏」会走 <see cref="Scan"/> 里那条「不是有效的 HAR」的报错路径，
    /// 与 <see cref="ScanFile"/> 的行为一致（它也是把解析异常转成一句人话）。
    /// </para>
    /// </summary>
    private static JsonElement Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
