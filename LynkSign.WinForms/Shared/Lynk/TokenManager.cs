using LynkSign.Infrastructure;
using LynkSign.Logging;

namespace LynkSign.Lynk;

/// <summary>accessToken 解析结果。</summary>
public sealed class TokenResult
{
    /// <summary>可用的 accessToken；失败时为 null。</summary>
    public string? AccessToken { get; init; }

    /// <summary>轮换后的新 refreshToken（没有轮换则为 null）。</summary>
    public string? NewRefreshToken { get; init; }

    /// <summary>refreshToken 的到期时间。</summary>
    public DateTime? RefreshExpireAt { get; init; }

    /// <summary>来源：cache / refresh / bare_access / refresh_err: ...</summary>
    public string Source { get; init; } = "";

    public bool Ok => !string.IsNullOrEmpty(AccessToken);
}

/// <summary>
/// accessToken 三级获取策略，逐行对齐 Python 版的 <c>get_access_token()</c>：
/// <list type="number">
///   <item><b>缓存命中</b> → 直接返回，跳过 refresh（与 APP 行为一致，降低风控概率）；</item>
///   <item><b>调 refresh 接口</b> → 换新 accessToken + refreshToken，并把轮换结果写回缓存；</item>
///   <item><b>降级试探</b> → refresh 被拒时，把它本身当 accessToken 试一次（短时有效，缓存 8 分钟）。</item>
/// </list>
/// </summary>
public sealed class TokenManager
{
    private readonly LynkApiClient _api;
    private readonly TokenCache _cache;

    public TokenManager(LynkApiClient api, TokenCache cache)
    {
        _api = api;
        _cache = cache;
    }

    public async Task<TokenResult> GetAccessTokenAsync(
        string refreshTokenOrAccessToken,
        string deviceId,
        IJobLogger log,
        bool force = false,
        CancellationToken ct = default)
    {
        // ---- 第一步: 尝试缓存 ----
        if (!force)
        {
            var cached = _cache.Load(refreshTokenOrAccessToken, deviceId);
            if (cached is not null && !string.IsNullOrEmpty(cached.AccessToken))
            {
                return new TokenResult
                {
                    AccessToken = cached.AccessToken,
                    NewRefreshToken = cached.RefreshTokenNew,
                    Source = "cache",
                };
            }
        }

        // ---- 第二步: 调 refresh 接口 ----
        var (result, error) = await _api.RefreshTokenAsync(refreshTokenOrAccessToken, deviceId, ct).ConfigureAwait(false);

        if (result is not null && !string.IsNullOrEmpty(result.AccessToken))
        {
            var newRefreshToken = string.IsNullOrEmpty(result.RefreshToken) ? refreshTokenOrAccessToken : result.RefreshToken;
            var expireAt = TimeUtil.ParseExpiry(result.ExpireAt);
            var refreshExpireAt = TimeUtil.ParseExpiry(result.RefreshExpireAt);

            _cache.Save(refreshTokenOrAccessToken, deviceId, result.AccessToken, expireAt, newRefreshToken, refreshExpireAt);

            return new TokenResult
            {
                AccessToken = result.AccessToken,
                NewRefreshToken = newRefreshToken,
                RefreshExpireAt = refreshExpireAt,
                Source = "refresh",
            };
        }

        // ---- 第三步: refresh 被拒, 把它当 accessToken 再试一次 ----
        // 探活用签到状态接口: 新接口 /up/api/v1/user/sign/day/info 优先,
        // 旧接口 getContinueDaysAndSignCard 兜底(两者都只是 GET, 都不需要请求体)。
        var dayProbe = await _api.SignDayInfoAsync(refreshTokenOrAccessToken, ct).ConfigureAwait(false);
        var legacyProbe = dayProbe.IsOk
            ? dayProbe
            : await _api.SignCardInfoAsync(refreshTokenOrAccessToken, ct).ConfigureAwait(false);

        if (legacyProbe.IsOk)
        {
            _cache.Save(refreshTokenOrAccessToken, deviceId, refreshTokenOrAccessToken,
                        TimeUtil.Now().AddMinutes(8), newRefreshToken: null, refreshExpireAt: null);

            log.Ok("  ⚠ 降级: token 不是 refreshToken, 当作 accessToken 直接用 (短时有效)");

            return new TokenResult
            {
                AccessToken = refreshTokenOrAccessToken,
                Source = "bare_access",
            };
        }

        _cache.Clear();
        return new TokenResult
        {
            // 两个探活接口的返回码都带上：万一哪天新接口挂了，
            // 光看这一行就能区分「token 无效」还是「接口/签名被网关拒了」。
            Source = $"refresh_err: {error}; bare_access 也无效: "
                   + $"day/info={dayProbe.CodeText} {dayProbe.Message} / "
                   + $"旧接口={legacyProbe.CodeText} {legacyProbe.Message}",
        };
    }

    /// <summary>当前使用的缓存文件路径（日志展示用）。</summary>
    public string CacheFilePath => _cache.Path;

    /// <summary>清空缓存。</summary>
    public string ClearCache() => _cache.Clear();
}
