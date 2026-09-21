using System.Text.Json;
using System.Text.RegularExpressions;
using LynkSign.Configuration;
using LynkSign.Infrastructure;
using LynkSign.Logging;
using LynkSign.Lynk;
using LynkSign.Push;

namespace LynkSign.Services;

/// <summary>一次签到任务的有效配置（按 命令行 &gt; 环境变量 &gt; config.json &gt; 默认值 解析后）。</summary>
public sealed class SignJobOptions
{
    public string RefreshToken { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public List<string> TokenB { get; init; } = [];
    public string ShareContentId { get; init; } = AppConfig.DefaultShareContentId;

    /// <summary>
    /// 分享文章 id 从哪来：默认 <see cref="ShareContentMode.Auto"/>（每次运行拉广场最新一篇），
    /// 拉不到时回退到 <see cref="ShareContentId"/>；<see cref="ShareContentMode.Fixed"/> 则直接用配置值。
    /// </summary>
    public ShareContentMode ShareContentMode { get; init; } = ShareContentMode.Auto;

    public bool AutoShare { get; init; }

    /// <summary>
    /// 拿到分享链接后是否自动用系统默认浏览器打开（<c>off</c> / <c>manual</c> / <c>always</c>）。
    /// 默认 <see cref="ShareUrlOpenMode.Off"/>：老配置里没有这个字段，不该因为升级就多弹窗口。
    /// </summary>
    public ShareUrlOpenMode OpenShareUrl { get; init; } = ShareUrlOpenMode.Off;

    public bool Quiet { get; init; }
    public bool NoCache { get; init; }
    public PushChannels Push { get; init; } = new();

    /// <summary>原生 iOS SDK 通道的 app key；留空即用抓包实测的默认值。</summary>
    public string CaNativeKey { get; init; } = "";

    /// <summary>
    /// 原生 iOS SDK 通道的 app secret。签到写入接口（<c>POST /user/sign/upgrade</c>）
    /// 只被授权给这条通道，留空则签到无法执行 —— 详见 <see cref="SignProfile.Native"/>。
    /// </summary>
    public string CaNativeSecret { get; init; } = "";

    /// <summary>按当前配置构造原生通道 profile。</summary>
    public SignProfile NativeProfile() => SignProfile.Native(CaNativeKey, CaNativeSecret);
}

/// <summary>B 账号刷分享的单项结果。</summary>
public sealed class ShareResult
{
    public int Index { get; set; }
    public bool Ok { get; set; }
    public string Lookup { get; set; } = "-";
    public string Check { get; set; } = "-";
    public string Report { get; set; } = "-";
    public int EnergyDelta { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// 签到主流程，逐行对齐 Python 版的 <c>run()</c> 与 <c>do_share_task()</c>。
///
/// 步骤：
/// <list type="number">
///   <item>续 token（缓存 → refresh → 降级试探）；</item>
///   <item>查签到状态 + 账户信息（能量体 / 成长值 / 任务进度）；</item>
///   <item>执行签到；</item>
///   <item>取 shareCode；</item>
///   <item>可选 auto-share（B 账号点击刷积分）；</item>
///   <item>拼接 Markdown 并多渠道推送。</item>
/// </list>
///
/// 返回退出码与 Python 版一致：0 成功 / 2 续 token 失败 / 3 查签到状态失败 / 4 签到失败 / 99 未捕获异常。
/// </summary>
public sealed class SignJob
{
    private static readonly Regex ShareCodePattern = new("^[A-Fa-f0-9]{32,}$", RegexOptions.Compiled);

    /// <summary>
    /// 签到成功后、复核账户前的等待。奖励入账与签到写入不是同一个事务，
    /// 查得太早会读到签到前的值 —— 那样推送里的「最新积分」就还是旧的。
    /// </summary>
    private static readonly TimeSpan PostSignSettleDelay = TimeSpan.FromSeconds(2);

    private readonly LynkApiClient _api;
    private readonly TokenManager _tokens;
    private readonly PushService _push;
    private readonly ConfigStore _config;

    public SignJob(LynkApiClient api, TokenManager tokens, PushService push, ConfigStore config)
    {
        _api = api;
        _tokens = tokens;
        _push = push;
        _config = config;
    }

    public async Task<int> RunAsync(SignJobOptions options, IJobLogger log, CancellationToken ct = default, string trigger = "manual")
    {
        log.Info(new string('=', 60));
        log.Info("领克 APP 自动签到 - .NET 10 版");
        log.Info(new string('=', 60));
        log.Info($"refreshToken: ...{Tail(options.RefreshToken, 12)}");
        log.Info($"deviceId:     {Head(options.DeviceId, 20)}...");

        // 与 Python 版一致：未配置 refreshToken 直接以退出码 1 结束
        if (string.IsNullOrWhiteSpace(options.RefreshToken)
            || options.RefreshToken == SettingsResolver.PlaceholderToken)
        {
            log.Err("未配置 refreshToken");
            log.Err("1) 在配置页填写 refreshToken, 或");
            log.Err("2) 设置环境变量 LYNK_REFRESH_TOKEN, 或");
            log.Err("3) 传 --token 参数");
            return 1;
        }

        var markdown = new List<string>();

        // ==================== 1) 获取 accessToken ====================
        log.Info("[1/5] 获取 accessToken (优先缓存)...");

        if (options.NoCache)
        {
            _tokens.ClearCache();
        }

        var token = await _tokens.GetAccessTokenAsync(options.RefreshToken, options.DeviceId, log, options.NoCache, ct)
            .ConfigureAwait(false);

        if (!token.Ok)
        {
            var reason = token.Source.StartsWith("refresh_err", StringComparison.Ordinal) ? token.Source : "未知错误";
            log.Err($"获取 accessToken 失败: {reason}");
            await SafePushAsync("领克签到失败", $"**❌ 续 token 失败**\n\n```\n{reason}\n```", options, log, ct).ConfigureAwait(false);
            return 2;
        }

        var accessToken = token.AccessToken!;
        log.Ok(token.Source == "cache" ? "accessToken: 缓存命中, 跳过 refresh" : "accessToken:  refresh 调用成功");
        log.Ok($"accessToken:  ...{Tail(accessToken, 12)}");

        // ---- refreshToken 轮换写回 ----
        // 写回逻辑与界面上的「拉取一次」、命令行的 --fetch-article 共用一处实现，
        // 避免"换了新 token 却忘了持久化"这种要隔天才会爆的漏洞（见 TokenRotationWriteBack）。
        int? rtDaysLeft = null;
        var rtExpireText = "";

        var (rotated, cfgOk, cfgInfo) = TokenRotationWriteBack.Apply(_config, options.RefreshToken, token);

        if (rotated)
        {
            if (cfgOk) log.Ok($"refreshToken 已自动写回 config.json: {cfgInfo}");
            else log.Warn($"refreshToken 写回 config.json 失败: {cfgInfo}");

            // 兼容旧的青龙环境变量方式 (存在 env 文件时才写)
            var (envOk, envInfo) = QlEnvUpdater.Update("LYNK_REFRESH_TOKEN", token.NewRefreshToken!);
            if (envOk) log.Ok($"refreshToken 已同步写回 env: {envInfo}");

            if (!cfgOk && !envOk)
            {
                log.Warn(new string('=', 60));
                log.Warn("refreshToken 已轮换, 但自动写回全部失败!");
                log.Warn($"请手动更新 config.json 的 refresh_token={token.NewRefreshToken}");
                log.Warn(new string('=', 60));
            }
        }

        if (token.RefreshExpireAt is { } refreshExpireAt)
        {
            rtDaysLeft = (int)Math.Floor((refreshExpireAt - TimeUtil.Now()).TotalDays);
            rtExpireText = refreshExpireAt.ToString("yyyy-MM-dd HH:mm");
            log.Ok($"refreshToken: 剩 {rtDaysLeft} 天, 到期 {rtExpireText}");
        }

        // ==================== 2) 账户信息 + 签到状态 ====================
        log.Info("[2/5] 查询账户信息 + 签到状态...");

        // ---- 2a. 当日签到状态 ----
        // 后端已把「今天签没签」的权威来源迁到 /up/api/v1/user/sign/day/info。
        // 旧接口 getContinueDaysAndSignCard 仍然可用，继续负责「连续天数 / 补签卡」，
        // 并兼作签到状态的兜底 —— 两个都调，各司其职。
        var dayInfo = await _api.SignDayInfoAsync(accessToken, ct).ConfigureAwait(false);
        var cardInfo = await _api.SignCardInfoAsync(accessToken, ct).ConfigureAwait(false);

        var probe = dayInfo.IsOk ? SignStatusReader.Read(dayInfo.Data) : SignStatusProbe.NotFound;

        if (!dayInfo.IsOk)
        {
            log.Warn($"day/info 查询失败: code={dayInfo.CodeText}  message={dayInfo.Message}");
            log.Info($"  raw: {dayInfo.ToJsonString(300)}");
        }
        else if (!probe.Found)
        {
            // 接口通了但字段没认出来：把原始 data 打出来，
            // 补一个候选字段名到 SignStatusReader 就能一次改对。
            log.Warn("day/info 已返回, 但未识别出签到状态字段, 回退旧接口判定");
            log.Info($"  day/info data: {dayInfo.Data.ToJsonString(400)}");
        }

        var legacyData = cardInfo.IsOk ? cardInfo.Data : null;

        // 两个接口都没结果，才算「查签到状态失败」（退出码 3 与旧版保持一致）
        if (!probe.Found && legacyData is null)
        {
            log.Err($"查询签到状态失败: day/info code={dayInfo.CodeText} {dayInfo.Message}");
            log.Err($"                  旧接口 code={cardInfo.CodeText} {cardInfo.Message}");
            if (!options.Quiet)
            {
                log.Info($"  day/info raw: {dayInfo.ToJsonString(300)}");
                log.Info($"  旧接口 raw:   {cardInfo.ToJsonString(300)}");
            }
            await SafePushAsync("领克签到失败", $"**❌ 查询签到状态失败**\n\n```\n{dayInfo.ToJsonString(200)}\n```", options, log, ct).ConfigureAwait(false);
            return 3;
        }

        // 「今天签没签」：新接口权威；没认出来才退回旧接口的 signStatus
        var legacyStatus = legacyData.FirstTruthy("signStatus", "todaySigned", "status");
        var alreadySigned = probe.Found ? probe.Signed : SignStatusReader.IsSigned(legacyStatus);

        // 连续天数 / 补签卡：两个来源依次找，新接口有就以新的为准
        JsonElement?[] statusSources = [dayInfo.Data, legacyData];
        var streak = SignStatusQuery.PickInt(statusSources, "continuousSignDays", "serialDays", "continueDays") ?? 0;
        var signCard = SignStatusQuery.PickInt(statusSources, "signCardNumber", "signCardCount", "cardNumber") ?? 0;

        var statusOrigin = probe.Found
            ? $"day/info.{probe.Field}"
            : legacyStatus is null
                ? "旧接口也未识别, 按未签到处理"
                : $"旧接口 signStatus={legacyStatus.Value.AsDisplay()}";

        // 这一行同时也是界面状态卡的数据源，格式与正则统一在 SignStatusLine 里（别在这里手拼字符串）。
        log.Ok(SignStatusLine.Build(alreadySigned, statusOrigin, streak, signCard));

        // ---- 2b. 账户信息 (能量体 + 成长值) ----
        var energyPoint = "-";
        var energyIncome = "-";
        var growthName = "-";
        var growthValue = "-";

        var energyResp = await _api.EnergyAsync(accessToken, ct).ConfigureAwait(false);
        if (energyResp.IsOk)
        {
            var ed = energyResp.Data;
            energyPoint = ed.DisplayProp("point");
            energyIncome = ed.DisplayProp("incomePoint");
        }

        var growthResp = await _api.GrowthAsync(accessToken, ct).ConfigureAwait(false);
        if (growthResp.IsOk)
        {
            var level = growthResp.Data.Prop("accountLevelVo");
            growthName = level.DisplayProp("name");
            growthValue = level.DisplayProp("growth");
        }

        log.Ok($"账户: Co积分={energyPoint}  累计获得={energyIncome}  等级={growthName}  能量体={growthValue}");


        // ---- 2c. 签到任务进度 ----
        // 接口只返回 taskProcess (已签天数), 总天数从任务名 "X天" 里正则提取
        var taskProgress = new List<(string Name, string Progress, string Reward)>();
        var tasksResp = await _api.TasksAsync(accessToken, ct).ConfigureAwait(false);
        if (tasksResp.IsOk)
        {
            foreach (var task in tasksResp.Data.Items())
            {
                var taskName = task.Str("taskName", "?");
                var taskProcess = task.DisplayProp("taskProcess", "?");
                var rewardJoined = task.Prop("rewardContent").JoinStrings(", ");
                var reward = string.IsNullOrEmpty(rewardJoined) ? "无奖励字段" : rewardJoined;

                var match = Regex.Match(taskName, @"(\d+)天");
                string display;
                if (match.Success && int.TryParse(taskProcess, out var done))
                {
                    // 显示「剩余 / 总」, 避免歧义
                    display = $"{int.Parse(match.Groups[1].Value) - done} / {match.Groups[1].Value}";
                }
                else
                {
                    display = taskProcess;
                }

                taskProgress.Add((taskName, display, reward));
            }

            foreach (var (name, progress, reward) in taskProgress)
            {
                log.Ok($"任务: {name}: {progress}  ({reward})");
            }
        }

        // ==================== 3) 执行签到 ====================
        var nativeProfile = options.NativeProfile();

        log.Info($"[3/5] 执行签到...  POST {LynkConstants.EpSign}  ({nativeProfile.Describe()})");

        string signStatusText;
        string rewardText;

        if (alreadySigned)
        {
            log.Ok("今日已签到, 无需重复");

            // 今天不需要签，也要现在就把「明天会失败」说出来 ——
            // 否则要等到明天早上任务跑挂才发现签到根本没执行，白丢一天。
            if (!nativeProfile.IsUsable)
            {
                log.Warn("  ⚠ 但原生通道密钥未配置, 明天需要真正签到时会被跳过");
                log.Warn($"     配置 {LynkConstants.NativeSecretConfigField} (config.json) 或 {LynkConstants.NativeSecretEnvVar}");
            }

            signStatusText = "✅ 已签到";
            rewardText = "无新增";
        }
        else
        {
            var signResp = await _api.DoSignAsync(accessToken, nativeProfile, ct).ConfigureAwait(false);

            // 本地前置校验拦下来的：不是网络失败、也不是网关拒绝，而是密钥根本没配。
            if (signResp.CodeText == "NO_SIGN_KEY")
            {
                LogMissingNativeKey(log);
                await SafePushAsync(
                    "领克签到失败",
                    $"**❌ 签到未执行：缺少原生通道密钥**\n\n"
                    + $"签到接口 `{LynkConstants.EpSign}` 只被授权给原生 iOS SDK 通道"
                    + $"（`X-Ca-Key={LynkConstants.NativeCaKey}`），\n"
                    + $"而该通道的 secret 不在抓包里、也无法从签名反推。\n\n"
                    + $"请配置 `{LynkConstants.NativeSecretConfigField}` 或环境变量 "
                    + $"`{LynkConstants.NativeSecretEnvVar}` 后重试。",
                    options, log, ct).ConfigureAwait(false);
                return 4;
            }

            if (signResp.IsOk)
            {
                log.Ok("签到成功!");
                signStatusText = "✅ 签到成功";

                var d = signResp.Data;
                var parts = new List<string>();
                if (d.TruthyProp("rewardEnergyNumber")) parts.Add($"+{d.Int("rewardEnergyNumber")} 能量体");
                if (d.TruthyProp("rewardPointNumber")) parts.Add($"+{d.Int("rewardPointNumber")} Co积分");
                if (d.TruthyProp("rewardSignCardNumber")) parts.Add($"+{d.Int("rewardSignCardNumber")} 补签卡");

                rewardText = parts.Count > 0 ? string.Join(", ", parts) : "无奖励字段";
                log.Ok($"奖励: {rewardText}");
            }
            else
            {
                log.Err($"签到失败: code={signResp.CodeText}  message={signResp.Message}");

                // 403 是「通道不对」的典型签名：签名本身已经通过，是网关的授权层把 key 拒了。
                // 不点破的话，这条日志看起来和「接口挂了」一模一样。
                var gatewayText = signResp.Raw.Length > 0 ? signResp.Raw : signResp.ToJsonString();
                if (gatewayText.Contains("Unauthorized Consumer", StringComparison.OrdinalIgnoreCase))
                {
                    log.Err("  → 网关【授权层】拒绝: 签名已通过, 但当前 X-Ca-Key 未被授权访问这个 API。");
                    log.Err($"     签到必须走原生通道 (X-Ca-Key={LynkConstants.NativeCaKey})，");
                    log.Err($"     请检查 {LynkConstants.NativeKeyConfigField} 是否被改成了别的值。");
                }

                await SafePushAsync("领克签到失败", $"**❌ 签到接口失败**\n\n```\n{signResp.ToJsonString(200)}\n```", options, log, ct).ConfigureAwait(false);
                return 4;
            }
        }

        // ==================== 3b) 签到后复核 ====================
        // 第 2 步查到的是【签到之前】的快照，而下面拼推送正文用的就是那几个数：
        // 首次签到会让能量体 +1、连续天数 +1，不重新取一次，推出去的就是旧数字。
        // 另一件同样重要的事：这条状态日志是界面状态卡的数据源 —— 少了它，卡片会一直停在
        // 第 2 步那句「今日未签到」上，表现就是「签到成功了但界面不更新」。
        if (!alreadySigned)
        {
            log.Info("[3b] 签到后复核状态与账户...");

            // 奖励入账与签到写入不是同一个事务，给它一点时间再查，否则可能读到旧值
            // （分享那边也是等了 2 秒才对比能量体）。
            await Task.Delay(PostSignSettleDelay, ct).ConfigureAwait(false);

            var after = await SignStatusQuery.ReadAsync(_api, accessToken, ct).ConfigureAwait(false);

            // 状态以接口为准；接口没给出可判读字段时，以「本次确实签成功了」为准。
            var signedNow = after.StatusFound ? after.Signed : true;
            if (!after.StatusFound) log.Warn("  复核没能读回签到状态, 按本次签到结果记");

            if (after.Streak is { } newStreak) streak = newStreak;
            if (after.SignCard is { } newCard) signCard = newCard;

            var preEnergy = energyPoint;
            var preGrowth = growthValue;

            if (after.EnergyOk)
            {
                energyPoint = after.EnergyPoint;
                energyIncome = after.EnergyIncome;
            }

            if (after.GrowthOk)
            {
                growthName = after.GrowthName;
                growthValue = after.GrowthValue;
            }

            log.Ok($"账户(复核): {after.AccountLine()}");
            log.Ok(SignStatusLine.Build(signedNow, "签到后复核", streak, signCard));

            if (after.EnergyOk && after.GrowthOk && energyPoint == preEnergy && growthValue == preGrowth)
            {
                log.Warn($"  提示: 账户数值与签到前一致 (积分余额 {preEnergy} / 成长值 {preGrowth}),");
                log.Warn("        可能是后端奖励尚未入账, 推送里显示的是本次复核到的值");
            }
        }

        markdown.Add($"**签到**: {signStatusText}");
        markdown.Add($"**奖励**: {rewardText}");
        markdown.Add($"**连续**: {streak} 天  /  补签卡: {signCard} 张");
        markdown.Add("");
        markdown.Add("**账户信息**:");
        markdown.Add($"- 积分余额: **{energyPoint}**  /  累计获得: **{energyIncome}**");
        markdown.Add($"- 成长等级: **{growthName}**  /  成长值: **{growthValue}**");


        if (taskProgress.Count > 0)
        {
            markdown.Add("");
            markdown.Add("**签到任务进度**:");
            foreach (var (name, progress, reward) in taskProgress)
            {
                markdown.Add($"- {name}: `{progress}`  ({reward})");
            }
        }

        // ==================== 4) 分享准备：文章 ID + shareCode ====================
        log.Info("[4/5] 准备分享 (文章 ID + shareCode)...");

        // ---- 4a. 文章 ID ----
        // 文章 id 会随文章下架而失效，而「失效」在分享环节完全静默：
        // 接口照样返回成功、能量体不涨，要等事后对能量体才发现。
        // 所以默认每次运行都现拉一篇最新的（见 ShareContentMode.Auto）。
        var contentId = options.ShareContentId;
        var articleTitle = "";
        var contentIdOrigin = "配置值";

        if (options.ShareContentMode == ShareContentMode.Auto)
        {
            var (article, articleError) = await _api.GetLatestArticleAsync(accessToken, ct).ConfigureAwait(false);

            if (article is not null)
            {
                contentId = article.ArticleId;
                articleTitle = article.Title;
                contentIdOrigin = "自动拉取";
                log.Ok($"  文章 ID: {article.Display}   (自动拉取广场最新一篇)");
            }
            else
            {
                // 拉不到不是致命错误 —— 用配置值继续，行为与开这个功能之前完全一致。
                log.Warn($"  自动拉取文章 ID 失败: {articleError}");
                log.Warn($"  → 回退用配置值 {contentId}  ({ShareContentModes.ConfigField}=fixed 可关掉拉取)");
            }
        }
        else
        {
            log.Info($"  文章 ID: {contentId}   ({ShareContentModes.ConfigField}=fixed, 不拉取)");
        }

        if (string.IsNullOrWhiteSpace(contentId))
        {
            contentId = AppConfig.DefaultShareContentId;
            contentIdOrigin = "内置默认";
            log.Warn($"  文章 ID 为空, 回退内置默认值 {contentId}");
        }

        // ---- 4b. shareCode ----
        string? shareCode = null;
        var shareCodeResp = await _api.GetShareCodeAsync(accessToken, ct).ConfigureAwait(false);

        // 注意: 这里后端返回的 code 恰好是字符串 "success", 与其它接口不同, 保持原样判断
        if (shareCodeResp.CodeText == "success"
            && shareCodeResp.Data is { } shareCodeData
            && shareCodeData.ValueKind == JsonValueKind.String)
        {
            var candidate = shareCodeData.GetString() ?? "";
            if (ShareCodePattern.IsMatch(candidate)) shareCode = candidate;
        }

        if (shareCode is not null)
        {
            log.Ok($"shareCode:  ...{Tail(shareCode, 12)}  (完整 {shareCode.Length} hex)");
        }
        else
        {
            log.Err($"拿不到 shareCode: {shareCodeResp.CodeText}  {shareCodeResp.Message}");
        }

        // ==================== 5) auto-share ====================
        var shareResults = new List<ShareResult>();

        if (options.AutoShare && options.TokenB.Count > 0 && shareCode is not null)
        {
            log.Info("[5/5] auto-share: B 账号刷分享积分...");
            log.Info($"  contentId: {contentId} ({contentIdOrigin})  clicker 数: {options.TokenB.Count}");

            for (var i = 0; i < options.TokenB.Count; i++)
            {
                var index = i + 1;
                log.Info($"  --- B{index}: refresh & click ---");

                var tokenB = await _tokens.GetAccessTokenAsync(options.TokenB[i], options.DeviceId, log, force: false, ct)
                    .ConfigureAwait(false);

                if (!tokenB.Ok)
                {
                    log.Err($"  B{index} 拿不到 accessToken  (source={tokenB.Source})");
                    log.Err("     → 这个 B token 已失效/无效, 请在领克 app 里重新抓一个");
                    log.Err("     → 抓包域名 app-api-gw-toc.lynkco.com 的请求头 token 字段");

                    shareResults.Add(new ShareResult { Index = index, Ok = false, Message = $"❌ token 无效 ({tokenB.Source})" });
                    continue;
                }

                log.Ok($"  B{index} accessToken ({tokenB.Source})");

                var result = await DoShareTaskAsync(tokenB.AccessToken!, contentId, shareCode, ct).ConfigureAwait(false);
                result.Index = index;
                shareResults.Add(result);

                var mark = result.Ok ? "✅" : "❌";
                var line = $"  B{index} 分享 {mark}  {result.Message}  (Δ能量体 {Signed(result.EnergyDelta)})";
                if (result.Ok) log.Ok(line); else log.Err(line);
            }

            markdown.Add("");
            markdown.Add($"**auto-share** (contentId `{Tail(contentId, 12)}`, {shareResults.Count} 个 B 账号):");
            foreach (var r in shareResults)
            {
                markdown.Add($"- {(r.Ok ? "✅" : "❌")} B{r.Index}: {r.Message}  (Δ能量体 `{Signed(r.EnergyDelta)}`)");
            }
        }
        else if (options.AutoShare && options.TokenB.Count == 0)
        {
            log.Info("[5/5] auto-share 未启用 (未配 LYNK_TOKEN_B)");
        }

        // ==================== 6) 构造分享链接 ====================
        var shareUrl = "";
        if (shareCode is not null)
        {
            shareUrl = LynkApiClient.BuildShareUrl(contentId, shareCode);
            log.Ok("");
            log.Ok(new string('━', 60));
            log.Ok("📤 分享链接 (复制到微信/朋友圈, 别人点击你得 5 能量体):");
            log.Ok(shareUrl);
            log.Ok($"   文章: {Or(articleTitle, "(未知标题)")}   id={contentId}   ({contentIdOrigin})");
            log.Ok(new string('━', 60));
        }

        if (!string.IsNullOrEmpty(shareUrl))
        {
            markdown.Add("");
            markdown.Add("**📤 分享链接** (复制到微信发, 别人点击你 +5 能量体):");
            if (articleTitle.Length > 0)
            {
                markdown.Add($"- 文章: **{articleTitle}**  `id={contentId}`");
            }
            markdown.Add("");
            // markdown 链接语法 [文字](url), 企业微信/钉钉/飞书/PushPlus/Bark 都能识别为可点击链接
            markdown.Add($"[👉 点击领取 +5 能量体]({shareUrl})");

            // 顺手把链接丢给系统默认浏览器。放在推送之前 —— 这个窗口要在
            // 「人刚点完立即签到、还盯着界面」的时候弹出来才有意义；
            // 排在各渠道推送后面的话，光推送超时就能拖掉十几秒。
            if (MaybeOpenShareUrl(options, shareUrl, trigger, log) is { Length: > 0 } browserNote)
            {
                markdown.Add($"- {browserNote}");
            }
        }

        // ==================== 7) 推送 ====================
        markdown.Insert(0, $"**时间**: `{TimeUtil.Stamp()}`");
        markdown.Insert(1, $"**refreshToken**: `...{Tail(options.RefreshToken, 12)}`");
        markdown.Insert(2, $"**accessToken**: {(token.Source == "cache" ? "缓存命中" : "本次 refresh")}");

        if (rtDaysLeft is not null)
        {
            markdown.Add("");
            markdown.Add($"**refreshToken**: 剩 **{rtDaysLeft}** 天 (到期 `{rtExpireText}`)");
        }

        var title = signStatusText.Contains("成功", StringComparison.Ordinal) ? "领克签到成功" : "领克签到 (已签)";
        var markdownText = string.Join("\n", markdown);

        log.Info($"[推送] {await PushAsync(title, markdownText, options, ct).ConfigureAwait(false)}");
        log.Ok("全部完成");
        return 0;
    }

    // ==================== 分享任务 ====================

    /// <summary>
    /// 按配置决定要不要把分享链接交给系统默认浏览器打开；返回一行给推送用的说明（没有动作时为空串）。
    ///
    /// <para>
    /// 打不开浏览器（无桌面环境、Docker 容器里、没设默认浏览器）只记一条警告，
    /// <b>不影响退出码</b>：此时链接已经完整打在日志和推送里，手动复制一样能用 ——
    /// 把「浏览器没弹出来」升级成「本次签到失败」是本末倒置。
    /// </para>
    /// </summary>
    private static string MaybeOpenShareUrl(SignJobOptions options, string shareUrl, string trigger, IJobLogger log)
    {
        if (options.OpenShareUrl == ShareUrlOpenMode.Off) return "";

        if (!options.OpenShareUrl.ShouldOpen(trigger))
        {
            log.Info($"  (已配置 {ShareUrlOpenModes.ConfigField}={options.OpenShareUrl.ToConfigValue()}, "
                + $"本次由 {trigger} 触发, 不自动打开浏览器)");
            return "";
        }

        if (BrowserLauncher.TryOpen(shareUrl, out var error))
        {
            log.Ok("已用系统默认浏览器打开分享链接");
            return "🖥 已自动用浏览器打开分享链接";
        }

        log.Warn($"自动打开浏览器失败: {error}");
        log.Warn("  链接已完整打印在上方, 可手动复制到微信使用");
        return $"⚠ 自动打开浏览器失败: {error}";
    }

    /// <summary>
    /// 用 B 账号给主账号的 shareCode 点分享加分，对应 Python 的 <c>do_share_task()</c>。
    /// 后端规则：report 成功且能量体 +5 才算真正加分；返回「今日已分享」属预期行为，同样算成功。
    /// </summary>
    private async Task<ShareResult> DoShareTaskAsync(string clientToken, string contentId, string shareCode, CancellationToken ct)
    {
        var result = new ShareResult();

        // 点击前的能量体
        var energyBefore = 0;
        var before = await _api.EnergyAsync(clientToken, ct).ConfigureAwait(false);
        if (before.CodeText == "success") energyBefore = before.Data.Int("point");

        // 1. lookup 分享人 userId
        var lookup = await _api.ShareLookupAsync(clientToken, shareCode, ct).ConfigureAwait(false);
        result.Lookup = Normalize(lookup.CodeText);

        // 2. check 接口 (后端前置校验)
        var check = await _api.ShareCheckAsync(clientToken, contentId, shareCode, ct).ConfigureAwait(false);
        result.Check = Normalize(check.CodeText);
        var checkMessage = check.Message;

        // 3. report 接口 (真正加分)
        var report = await _api.ShareReportAsync(clientToken, contentId, shareCode, ct).ConfigureAwait(false);
        result.Report = Normalize(report.CodeText);
        var reportMessage = report.Message;

        // 4. 看能量体变化
        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        var energyAfter = 0;
        var after = await _api.EnergyAsync(clientToken, ct).ConfigureAwait(false);
        if (after.CodeText == "success") energyAfter = after.Data.Int("point");
        result.EnergyDelta = energyAfter - energyBefore;

        if (report.IsOk && result.EnergyDelta > 0)
        {
            result.Ok = true;
            result.Message = $"+{result.EnergyDelta} 能量体";
        }
        else if (reportMessage.Contains("已分享", StringComparison.Ordinal)
              || reportMessage.Contains("已领取", StringComparison.Ordinal)
              || reportMessage.Contains("今日已", StringComparison.Ordinal)
              || reportMessage.Contains("已结束", StringComparison.Ordinal))
        {
            result.Ok = true;   // 后端拒收是预期行为, 算成功
            result.Message = $"今日已分享 ({reportMessage})";
        }
        else if (report.IsOk)
        {
            result.Ok = true;
            result.Message = $"report OK 但 Δ={result.EnergyDelta} ({Or(reportMessage, checkMessage)})";
        }
        else
        {
            result.Message = $"report failed: code={report.CodeText} {reportMessage}";
        }

        return result;
    }

    // ==================== 小工具 ====================

    /// <summary>
    /// 缺少原生通道密钥时打印可操作的指引。
    ///
    /// <para>
    /// 这类失败最容易被误判：编译能过、自检能过、日志也只有一句「失败」，
    /// 很容易被当成「接口挂了」而去乱改路径。所以这里把「为什么必须是这条通道」
    /// 和「secret 从哪里来」一次说清。
    /// </para>
    /// </summary>
    private static void LogMissingNativeKey(IJobLogger log)
    {
        log.Err($"签到未执行: 缺少原生通道密钥 (X-Ca-Key={LynkConstants.NativeCaKey})");
        log.Err("");
        log.Err($"  签到接口 {LynkConstants.EpSign} 被网关划给了「原生 iOS SDK」通道:");
        log.Err($"    · H5 通道 (X-Ca-Key={LynkConstants.CaKey}) 打过去恒为 403 Unauthorized Consumer");
        log.Err("    · 该判定发生在 token 校验之前, 与 token 是否有效无关");
        log.Err("    · 网关里只有这一条签到写入接口 (已枚举 147 条候选路径确认), 换路径名绕不过去");
        log.Err("");
        log.Err("  该通道的 secret 不会出现在任何请求/响应里, 抓包和反推都拿不到, 必须自行提供:");
        log.Err($"    1) 配置页填「原生通道 Secret」, 或在 config.json 写 \"{LynkConstants.NativeSecretConfigField}\"");
        log.Err($"    2) 或设环境变量 {LynkConstants.NativeSecretEnvVar}");
        log.Err("");
        log.Err("  未配置期间: 签到动作被跳过, 其余功能 (签到状态/连续天数/能量体/任务/分享) 照常工作。");
    }

    /// <summary>Python 的 <c>d.get("code", "?")</c>：没有 code 字段时显示 "?"。</summary>
    private static string Normalize(string code) => string.IsNullOrEmpty(code) ? "?" : code;

    /// <summary>Python 的 <c>a or b</c>。</summary>
    private static string Or(string first, string second) => string.IsNullOrEmpty(first) ? second : first;

    /// <summary>等价 <c>f"{value:+d}"</c>：正数带 +，负数带 -，零为 +0。</summary>
    private static string Signed(int value) => value.ToString("+#;-#;+0");

    private static string Tail(string text, int length)
        => string.IsNullOrEmpty(text) ? "" : (text.Length <= length ? text : text[^length..]);

    private static string Head(string text, int length)
        => string.IsNullOrEmpty(text) ? "" : (text.Length <= length ? text : text[..length]);

    /// <summary>推送并把异常吞成日志（避免推送失败中断签到主流程）。</summary>
    private async Task SafePushAsync(string title, string markdownText, SignJobOptions options, IJobLogger log, CancellationToken ct)
    {
        try
        {
            await _push.PushAsync(title, markdownText, options.Push, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warn($"推送失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<string> PushAsync(string title, string markdownText, SignJobOptions options, CancellationToken ct)
    {
        try
        {
            return await _push.PushAsync(title, markdownText, options.Push, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"推送异常: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
