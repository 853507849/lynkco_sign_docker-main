using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LynkSign.Infrastructure;

namespace LynkSign.Lynk;

/// <summary>
/// 领克接口客户端，对应 Python 版的 <c>lynk_call()</c> / <c>refresh_lynk_token()</c>
/// 以及一票 <c>lynk_*</c> 便捷方法。
///
/// 与 Python 版的差异：
/// <list type="bullet">
///   <item>用 <see cref="IHttpClientFactory"/> 取 HttpClient，连接池与 DNS 轮换交给框架管理，
///         不再像 <c>requests</c> 那样每次新建连接；</item>
///   <item>全部异步（<c>Async</c> 后缀），便于在 ASP.NET Core 里做并发调度；</item>
///   <item>响应解析依然保留「非 JSON 也不抛异常」的宽松策略，保证日志可读。</item>
/// </list>
/// </summary>
public sealed class LynkApiClient
{
    /// <summary>注册在 DI 里的命名 HttpClient，超时 20 秒。</summary>
    public const string HttpClientName = "lynk";

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IHttpClientFactory _factory;

    public LynkApiClient(IHttpClientFactory factory) => _factory = factory;

    /// <summary>每次调用都从工厂取（工厂内部复用 handler 池，零连接开销）。</summary>
    private HttpClient Http => _factory.CreateClient(HttpClientName);

    // ==================== 底层调用 ====================

    /// <summary>
    /// 带签名调用业务接口。等价 Python 的 <c>lynk_call()</c>：
    /// 无论 HTTP 状态码是多少都尝试解析 JSON，失败则回退成 <c>code=状态码, raw=正文前 500 字</c>。
    /// </summary>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="path">接口路径（不含域名与 query）。</param>
    /// <param name="token">access token。</param>
    /// <param name="body">
    /// 请求体。<c>null</c> 表示<b>不带请求体</b>（Content-Length: 0），
    /// 传空字典则会被序列化成字面量 <c>{}</c> —— 两者在网关眼里不一样，别混用。
    /// </param>
    /// <param name="queryParams">GET 查询参数。</param>
    /// <param name="signProfile">
    /// 签名通道。默认 <see cref="SignProfile.H5"/>（所有查询类接口都在它下面）；
    /// 签到写入必须显式传 <see cref="SignProfile.Native"/>，否则会被网关按未授权拒掉。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    public async Task<ApiResponse> CallAsync(
        string method,
        string path,
        string token,
        object? body = null,
        IReadOnlyDictionary<string, string>? queryParams = null,
        SignProfile? signProfile = null,
        CancellationToken ct = default)
    {
        var profile = signProfile ?? SignProfile.H5;
        var headers = LynkSignature.Build(profile, method, path, token, queryParams);

        try
        {
            var url = LynkConstants.ApiBase + path;
            if (queryParams is { Count: > 0 })
            {
                var query = string.Join("&", queryParams
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
                if (query.Length > 0) url = $"{url}?{query}";
            }

            using var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), url);

            foreach (var (name, value) in headers)
            {
                // Content-Type 是 content header，塞进 HttpRequestMessage.Headers 会被 .NET
                // 静默丢弃（TryAddWithoutValidation 返回 false，头根本没发出去），
                // 所以这里跳过它，统一在下面 request.Content.Headers 上落到请求里。
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;

                request.Headers.TryAddWithoutValidation(name, value);
            }

            // 通道固定的 UA（原生通道为 CA_iOS_SDK_2.0）。它不参与签名，但 App 会发，
            // 保持一致可以让网关/风控看到的是它预期的客户端形态。
            if (profile.UserAgent is { Length: > 0 } userAgent)
            {
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            }

            // ⚠️⚠️ 发出去的 Content-Type 必须与参与签名的那个值【逐字节相同】。
            //
            // 阿里云网关是拿「实际请求头里的 Content-Type」参与签名串计算的（签名串第 4 行），
            // 一旦这个头缺失或值不同，网关算出的签名就与客户端不一致，
            // 无条件返回 HTTP 400 + 正文 "Invalid Signature"（连业务层都到不了）。
            //
            // 这里踩过两个连着的坑，都不能用：
            //   1) request.Headers.TryAddWithoutValidation("content-type", ...)
            //      —— Content-Type 属于 content header，.NET 会静默丢弃（返回 false）；
            //   2) StringContent(json, Encoding.UTF8, "application/json")
            //      —— 它会自动追加 "; charset=utf-8"，值一变签名照样不匹配。
            // 所以统一走 ByteArrayContent + TryAddWithoutValidation 写【原始字符串】：
            // 不经框架的格式化器，就不会出现「签名用 A、发出去是 B」这种最隐蔽的错位。
            var json = body is null ? string.Empty : JsonSerializer.Serialize(body, JsonWriteOptions);

            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
            request.Content.Headers.TryAddWithoutValidation("Content-Type", profile.ContentType);

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            return await ParseAsync(response, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ApiResponse.FromFallback("EXCEPTION", message: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<ApiResponse> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var statusCode = (int)response.StatusCode;

        ApiResponse parsed;

        try
        {
            using var doc = JsonDocument.Parse(text);
            parsed = ApiResponse.FromJson(doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            var raw = text.Length > 500 ? text[..500] : text;
            parsed = ApiResponse.FromFallback(statusCode.ToString(), raw);
        }

        // 状态码一律回填：JSON 响应里 CodeText 是业务 code，HTTP 状态码只能靠这个字段带出去。
        return parsed.WithHttpStatus(statusCode);
    }

    // ==================== Token ====================

    /// <summary>
    /// refresh 接口换 accessToken + 新的 refreshToken。
    /// 对应 Python 的 <c>refresh_lynk_token()</c>，返回 (结果, 错误)。
    /// </summary>
    public async Task<(RefreshResult? Result, string? Error)> RefreshTokenAsync(string refreshToken, string deviceId, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["refreshToken"] = refreshToken,
            ["deviceId"] = deviceId,
            ["deviceType"] = "IOS",
            ["appVersion"] = "4.2.0",
        };

        var url = LynkConstants.RefreshUrl + "?" + string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            request.Headers.TryAddWithoutValidation("Authorization", $"APPCODE {LynkConstants.AppCode}");
            request.Headers.TryAddWithoutValidation("accept", "application/json");
            request.Headers.TryAddWithoutValidation("publicplatform", "iOS");
            request.Headers.TryAddWithoutValidation("user-agent", "CA_iOS_SDK_2.0");
            request.Headers.TryAddWithoutValidation("token", string.Empty);
            request.Headers.TryAddWithoutValidation("gl_dev_id", deviceId);
            request.Headers.TryAddWithoutValidation("appversioncode", "4.2.0");
            request.Headers.TryAddWithoutValidation("appversionname", "40200106");
            request.Headers.TryAddWithoutValidation("gl_app_version", "4.2.0");
            request.Headers.TryAddWithoutValidation("gl_app_build", "40200106");
            request.Headers.TryAddWithoutValidation("x-ca-version", "1");

            // content-type 只能通过 Content headers 设置（往 request.Headers 里塞会被 .NET 静默丢弃）。
            // refresh 接口不校验 X-Ca 签名, 所以这里缺失不会报错, 但仍按 Python 版原样发出去。
            request.Content = new ByteArrayContent(Array.Empty<byte>());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (null, $"refresh 失败: HTTP {(int)response.StatusCode} {Truncate(text, 200)}");
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.Str("code") != "success")
            {
                var message = root.Str("message");
                var detail = string.IsNullOrWhiteSpace(message) ? Truncate(text, 200) : message;
                return (null, $"refresh 失败: {detail}");
            }

            var dto = ((JsonElement?)root).Prop("data").Prop("centerTokenDto");
            if (dto is null) return (null, "refresh 失败: 响应缺少 centerTokenDto");

            return (new RefreshResult
            {
                AccessToken = dto.Str("token"),
                RefreshToken = dto.Str("refreshToken"),
                ExpireAt = dto.Prop("expireAt").ToRawObject(),
                RefreshExpireAt = dto.Prop("refreshExpireAt").ToRawObject(),
            }, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"refresh 异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ==================== 业务接口 ====================

    /// <summary>
    /// 当日签到状态（权威来源）。<c>GET /up/api/v1/user/sign/day/info</c>
    /// 返回「今天是否已签到」，供主流程判断要不要执行签到。
    /// </summary>
    public Task<ApiResponse> SignDayInfoAsync(string token, CancellationToken ct = default)
        => CallAsync("GET", LynkConstants.EpSignDayInfo, token, ct: ct);

    /// <summary>
    /// 连续签到天数 + 补签卡（旧接口，仍可用）。
    /// <c>GET /up/api/v1/userReward/getContinueDaysAndSignCard</c>
    /// </summary>
    public Task<ApiResponse> SignCardInfoAsync(string token, CancellationToken ct = default)
        => CallAsync("GET", LynkConstants.EpSignCardInfo, token, ct: ct);

    /// <summary>
    /// 执行签到：<c>POST /up/api/v1/user/sign/upgrade</c>，<b>请求体为空</b>。
    ///
    /// <para>
    /// 必须走原生 iOS SDK 通道。该 API 在网关侧只授权给 <c>X-Ca-Key=203760416</c>：
    /// 用 H5 通道打过去恒为 <c>403 Unauthorized Consumer</c>（已实测；
    /// 且该判定发生在 token 校验之前，与 token 是否有效无关）。
    /// 同目录下 147 条候选路径里只有它和 <see cref="LynkConstants.EpSignLegacy"/> 存在于网关，
    /// 说明「换个路径名」这条捷径并不存在。
    /// </para>
    /// <para>
    /// 原生通道 secret 不在任何抓包里（抓包只能看到 <c>X-Ca-Key</c>），必须由用户配置。
    /// 未配置时<b>不发请求</b>：空 secret 只会换来一个看不出原因的
    /// <c>400 Invalid Signature</c>，这里直接返回 <c>NO_SIGN_KEY</c> 交给上层给出可操作指引。
    /// </para>
    /// </summary>
    /// <param name="token">access token（原生通道的签名要求它参与计算）。</param>
    /// <param name="nativeProfile">原生通道 profile，由配置里的 key/secret 构造。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<ApiResponse> DoSignAsync(string token, SignProfile nativeProfile, CancellationToken ct = default)
    {
        if (!nativeProfile.IsUsable)
        {
            return Task.FromResult(ApiResponse.FromFallback(
                "NO_SIGN_KEY",
                message: $"签到接口 {LynkConstants.EpSign} 需要原生通道密钥 "
                         + $"(X-Ca-Key={LynkConstants.NativeCaKey})，当前未配置 "
                         + $"{LynkConstants.NativeSecretConfigField} / {LynkConstants.NativeSecretEnvVar}"));
        }

        // 请求体为空（与 App 一致，发出去是 Content-Length: 0），所以传 null 而不是空字典。
        return CallAsync("POST", LynkConstants.EpSign, token, body: null, signProfile: nativeProfile, ct: ct);
    }

    public Task<ApiResponse> EnergyAsync(string token, CancellationToken ct = default)
        => CallAsync("GET", LynkConstants.EpEnergy, token, ct: ct);

    public Task<ApiResponse> GrowthAsync(string token, CancellationToken ct = default)
        => CallAsync("GET", LynkConstants.EpGrowth, token, ct: ct);

    public Task<ApiResponse> TasksAsync(string token, CancellationToken ct = default)
        => CallAsync("GET", LynkConstants.EpTasks, token, ct: ct);

    /// <summary>主账号：取自己的 shareCode。</summary>
    public Task<ApiResponse> GetShareCodeAsync(string token, CancellationToken ct = default)
        => CallAsync("GET", LynkConstants.EpGetShareCode, token, ct: ct);

    // ==================== 探索广场（动态取最新文章） ====================

    /// <summary>
    /// 广场内容流某一页。<c>POST /app/explore/home-page/square/index2</c>，走 H5 通道。
    ///
    /// <para>
    /// 请求体的四个字段是<b>必须</b>的（实测）：少了它们只返回首屏，
    /// 而首屏里「动态」居多、经常一篇文章都挑不出来。
    /// </para>
    /// </summary>
    /// <param name="pageNo">页码，从 1 开始。</param>
    public Task<ApiResponse> ExploreSquareAsync(string token, int pageNo, CancellationToken ct = default)
        => CallAsync("POST", LynkConstants.EpExploreSquare, token,
            body: new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["dynamicSort"] = "new",     // 按时间倒序，保证「第一条」就是最新的
                ["uniqueId"] = "",
                ["refreshType"] = "MORE",
                ["pageNo"] = pageNo,
            },
            ct: ct);

    /// <summary>
    /// 翻页找出当前最新的一篇文章（含标题），用于「不把文章 id 写死」。
    ///
    /// <para>
    /// 返回值是 <c>(文章, 失败原因)</c> 二元组而不是单纯可空 —— 调用方需要把「为什么没取到」
    /// 写进日志：分享环节静默用了兜底 id 时，用户看到的只有「分享成功」，
    /// 出问题时无从下手。找不到时 <c>Article</c> 为 null、<c>Error</c> 说明原因。
    /// </para>
    /// </summary>
    public async Task<(ShareArticle? Article, string? Error)> GetLatestArticleAsync(string token, CancellationToken ct = default)
    {
        string? firstError = null;

        for (var pageNo = 1; pageNo <= LynkConstants.ExploreSquarePageCount; pageNo++)
        {
            var resp = await ExploreSquareAsync(token, pageNo, ct).ConfigureAwait(false);

            if (!resp.IsOk)
            {
                // 鉴权/签名类错误翻页也没用，直接停：这种情况每页都会用同一句话失败，
                // 翻满 3 页只会把日志刷长。
                firstError ??= $"第 {pageNo} 页 code={resp.CodeText} {resp.Message}";
                break;
            }

            if (ShareArticleFinder.FindFirst(resp.Data) is { } article) return (article, null);

            firstError ??= $"第 {pageNo} 页没有「文章」类型的内容";
        }

        return (null, firstError ?? $"翻完 {LynkConstants.ExploreSquarePageCount} 页都没有文章");
    }

    /// <summary>点击账号：反查分享人 userId。</summary>
    public Task<ApiResponse> ShareLookupAsync(string token, string shareCode, CancellationToken ct = default)
        => CallAsync("POST", LynkConstants.EpShareLookup, token,
            body: new Dictionary<string, string> { ["shareCode"] = shareCode }, ct: ct);

    public Task<ApiResponse> ShareCheckAsync(string token, string contentId, string shareCode, CancellationToken ct = default)
        => CallAsync("POST", LynkConstants.EpShareCheck, token,
            body: new Dictionary<string, string> { ["contentId"] = contentId, ["shareCode"] = shareCode }, ct: ct);

    public Task<ApiResponse> ShareReportAsync(string token, string contentId, string shareCode, CancellationToken ct = default)
        => CallAsync("POST", LynkConstants.EpShareReport, token,
            body: new Dictionary<string, string> { ["contentId"] = contentId, ["shareCode"] = shareCode }, ct: ct);

    // ==================== 分享链接 ====================

    /// <summary>
    /// 构造可手工复制到微信的 H5 分享链接。
    /// <c>isShare</c> 参数是 URL-encoded 的 <c>lynkco://</c> 协议串，用于 app 唤起。
    /// </summary>
    public static string BuildShareUrl(string contentId, string shareCode)
    {
        var isShareRaw = $"lynkco://wx/?routeUrl=/pages/exploration/article/index.js?id={contentId}";
        // Uri.EscapeDataString 与 Python quote(raw, safe="") 的转义集合一致（仅保留 A-Za-z0-9-_.~）
        var isShareEnc = Uri.EscapeDataString(isShareRaw);

        return LynkConstants.ShareUrlTemplate
            .Replace("{cid}", contentId, StringComparison.Ordinal)
            .Replace("{is_share}", isShareEnc, StringComparison.Ordinal)
            .Replace("{code}", shareCode, StringComparison.Ordinal);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max];
}

/// <summary>refresh 接口返回的令牌信息。</summary>
public sealed class RefreshResult
{
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    /// <summary>原始值（数字或字符串），交给 <see cref="TimeUtil.ParseExpiry"/> 解析。</summary>
    public object? ExpireAt { get; init; }
    public object? RefreshExpireAt { get; init; }
}
