using LynkSign.Configuration;
using LynkSign.Lynk;

namespace LynkSign.Services;

/// <summary>
/// 把 refresh 接口轮换出来的新 refreshToken 写回 config.json。
///
/// <para>
/// 为什么值得单独抽一个方法：<see cref="TokenManager"/> 拿到新 refreshToken 后只把它写进
/// <b>缓存</b>，写回配置文件这一步一直是调用方自己做的。而「换了新 token 却忘了持久化」
/// 这个漏洞不会当场报错 —— 它要等到下一次刷新时用旧 token 去换、被服务端拒掉才爆出来，
/// 那时已经很难回忆起是哪次改动引入的。现在三个调用方
/// （签到主流程 / 界面上的「拉取一次」/ 命令行的 <c>--fetch-article</c>）共用这一处实现。
/// </para>
/// </summary>
public static class TokenRotationWriteBack
{
    /// <summary>
    /// 若本次 refresh 轮换了 token，则写回 config.json；没有轮换时什么都不做。
    /// </summary>
    /// <param name="config">配置存储。</param>
    /// <param name="currentRefreshToken">本次请求用的那个 refreshToken（用来判断是否真的轮换了）。</param>
    /// <param name="token">令牌获取结果。</param>
    /// <returns>
    /// <c>Rotated</c> 表示本次确实发生了轮换；<c>Ok</c> 只在 <c>Rotated</c> 为真时有意义，
    /// 表示写回成功；<c>Info</c> 是给日志用的一句话。
    /// </returns>
    public static (bool Rotated, bool Ok, string Info) Apply(
        ConfigStore config,
        string currentRefreshToken,
        TokenResult token)
    {
        var rotated = token.NewRefreshToken;

        if (string.IsNullOrEmpty(rotated)
            || string.Equals(rotated, currentRefreshToken, StringComparison.Ordinal))
        {
            return (false, true, "本次没有轮换 refreshToken");
        }

        var (ok, info) = config.UpdateField(c => c.RefreshToken = rotated);
        return (true, ok, info);
    }
}
