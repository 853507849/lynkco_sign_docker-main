using LynkSign.Configuration;
using LynkSign.Logging;
using LynkSign.Lynk;

namespace LynkSign.Services;

/// <summary>
/// 只刷签到状态的轻量任务：<b>不签到、不推送</b>，也不改配置（除了 refreshToken 轮换必需的写回）。
///
/// <para>
/// 用途有两个，都是「把最新状态拉出来摆在界面上」：
/// <list type="bullet">
///   <item>每天 00:01 自动刷一次（跨过零点后「今天签没签」已归零、连续天数按昨天的结果落定，
///         而界面上的三张状态卡还是昨天的值）；</item>
///   <item><c>--refresh-status</c> 手动入口，用来当场验证这条链路。</item>
/// </list>
/// </para>
///
/// <para>
/// 它<b>不发推送</b>：这是无人值守时间点的例行刷新，推送只会变成骚扰。
/// 状态行照常写进日志 —— 界面状态卡就是靠解析它刷新的。
/// </para>
///
/// <para>
/// 退出码沿用签到流程的前半段语义：0 拉到状态 / 1 未配置 refreshToken / 2 拿不到 accessToken /
/// 3 状态接口没给出可判读的状态 / 99 未捕获异常（由调用方兜）。
/// </para>
/// </summary>
public sealed class StatusRefreshJob
{
    /// <summary>触发方式与日志文件名后缀（<c>run-…-status.log</c>）。</summary>
    public const string Trigger = "status";

    private readonly LynkApiClient _api;
    private readonly TokenManager _tokens;
    private readonly ConfigStore _config;
    private readonly PointsHistoryStore _points;

    public StatusRefreshJob(LynkApiClient api, TokenManager tokens, ConfigStore config, PointsHistoryStore points)
    {
        _api = api;
        _tokens = tokens;
        _config = config;
        _points = points;
    }

    public async Task<int> RunAsync(SignJobOptions options, IJobLogger log, CancellationToken ct = default)
    {
        log.Info(new string('=', 60));
        log.Info("领克签到状态刷新 - 只读 (不签到 / 不推送)");
        log.Info(new string('=', 60));

        if (string.IsNullOrWhiteSpace(options.RefreshToken)
            || options.RefreshToken == SettingsResolver.PlaceholderToken)
        {
            log.Err("未配置 refreshToken");
            return 1;
        }

        var token = await _tokens
            .GetAccessTokenAsync(options.RefreshToken, options.DeviceId, log, force: false, ct)
            .ConfigureAwait(false);

        if (!token.Ok)
        {
            log.Err($"获取 accessToken 失败: {token.Source}");
            return 2;
        }

        // 与签到主流程共用同一处写回实现：refresh 轮换出的新 token 必须落盘，
        // 否则「跑一次只读刷新」反而会让配置里留着一个已作废的旧 token。
        var (rotated, cfgOk, cfgInfo) = TokenRotationWriteBack.Apply(_config, options.RefreshToken, token);
        if (rotated && cfgOk) log.Ok($"refreshToken 已自动写回 config.json: {cfgInfo}");

        var snapshot = await SignStatusQuery.ReadAsync(_api, token.AccessToken!, ct).ConfigureAwait(false);

        if (!snapshot.StatusQueryOk)
        {
            log.Err($"状态接口都没通: day/info code={snapshot.DayInfoCode}  旧接口 code={snapshot.CardInfoCode}");
        }
        else if (!snapshot.StatusFound)
        {
            // 接口通了但字段没认出来：把原始 data 打出来，补一个候选字段名到 SignStatusReader 就能一次改对。
            log.Warn($"未识别出签到状态字段 (来源 {snapshot.Origin})");
            log.Info($"  day/info data: {snapshot.DayInfoRaw}");
        }

        // 这行同时是界面「账户积分」卡的数据源（格式与正则统一在 AccountLine 里）。
        // 顺带记一条积分变化：每天 00:01 这一次是「无人值守也照样留下积分轨迹」的唯一机会，
        // 签到那天若是没跑成，至少还能从这条记录看出积分有没有动过。
        var account = snapshot.ToAccount("状态刷新");
        log.Ok(AccountLine.Build(account));

        if (account.HasNumbers)
        {
            log.Ok(PointsHistoryStore.DescribeChange(_points.Observe(Trigger, account)));
        }

        // 三个值缺一个就刷不了卡片（界面那条正则要求它们同行出现）。
        // 与其拿 0 去顶替「没拿到」，不如干脆不输出状态行 —— 卡片停在旧值，
        // 也强过跳到一个假的「连续 0 天」。
        if (snapshot.StatusFound && snapshot.Streak is { } streak && snapshot.SignCard is { } signCard)
        {
            log.Ok(SignStatusLine.Build(snapshot.Signed, snapshot.Origin, streak, signCard));
            log.Info("状态刷新完成 (未签到, 未推送)");
            return 0;
        }

        log.Warn("本次没拿到完整的状态 / 连续天数 / 补签卡, 界面状态卡保持原值");
        return 3;
    }
}
