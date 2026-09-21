using LynkSign.Configuration;
using LynkSign.Infrastructure;
using LynkSign.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LynkSign.Scheduling;

/// <summary>
/// cron 调度后台服务，对应 Python 版 <c>config_server.py</c> 的 <c>scheduler_loop()</c>。
///
/// 每 20 秒读一次 <c>config.json</c>，判断当前分钟是否命中 cron 表达式；
/// 因为每次都重新读配置，所以<b>改完 cron 即时生效，无需重启容器</b>。
///
/// <para>
/// 除 cron 签到之外，它还负责每天一次的状态刷新（见 <see cref="DailyRefreshTime"/>）——
/// 「今天签没签 / 连续几天」在跨过零点之后就变了，而界面卡片不会自己变。
/// </para>
/// </summary>
public sealed class SignScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 每天自动刷一次签到状态的时间（本地时区）。定在 00:01：新的一天刚开始，
    /// 「今天签没签」归零、连续天数按昨天的结果落定，这时候刷出来的状态最准。
    /// </summary>
    public static readonly TimeOnly DailyRefreshTime = new(0, 1);

    /// <summary>
    /// 判断此刻是否该做「每日状态刷新」。
    ///
    /// <para>
    /// 判据是<b>已经过了 00:01 且今天还没刷过</b>，而不是「正好等于 00:01」：
    /// 电脑在休眠、或程序当时没开着的时候，卡在那一分钟会整天都刷不到。
    /// 代价是启动时（只要已过 00:01）也会补刷一次 —— 这正是想要的：重启后卡片不至于
    /// 还挂着上一次运行时的旧状态。
    /// </para>
    /// </summary>
    /// <param name="now">当前本地时间。</param>
    /// <param name="lastRefreshDay">本进程上次刷新的日期；没刷过传 null。</param>
    public static bool IsRefreshDue(DateTime now, DateOnly? lastRefreshDay)
        => lastRefreshDay != DateOnly.FromDateTime(now)
           && TimeOnly.FromDateTime(now) >= DailyRefreshTime;

    /// <summary>
    /// 离线回归 <see cref="IsRefreshDue"/>。这条判定写错不会报任何错，
    /// 只会表现为「再也没自动刷新过」或「每次轮询都刷一次」，
    /// 所以把边界（00:01 那一分钟、跨天、当天已刷）用表格钉住。
    /// </summary>
    public static (bool Ok, List<string> Lines) SelfCheck()
    {
        var today = new DateOnly(2026, 9, 21);
        var yesterday = today.AddDays(-1);

        (DateTime Now, DateOnly? LastDay, bool Expected, string Note)[] cases =
        [
            (today.ToDateTime(new TimeOnly(0, 0)), null, false, "00:00 还没到点"),
            (today.ToDateTime(DailyRefreshTime), null, true, "正好 00:01 (边界含在内)"),
            (today.ToDateTime(new TimeOnly(0, 2)), null, true, "00:02 刚过点"),
            (today.ToDateTime(new TimeOnly(9, 0)), null, true, "启动补刷 (早上才开机)"),
            (today.ToDateTime(new TimeOnly(9, 0)), today, false, "今天已经刷过"),
            (today.ToDateTime(new TimeOnly(23, 59)), yesterday, true, "跨天后的第一次"),
        ];

        var lines = new List<string>();
        var ok = true;

        foreach (var (now, lastDay, expected, note) in cases)
        {
            var actual = IsRefreshDue(now, lastDay);
            if (actual != expected) ok = false;

            lines.Add($"[{(actual == expected ? "OK" : "X")}] 每日刷新判定 {note}: "
                + $"{now:MM-dd HH:mm} / 上次={lastDay?.ToString("MM-dd") ?? "无"} → 实际={actual} 期望={expected}");
        }

        lines.Add($"状态刷新时间: 每天 {DailyRefreshTime:HH\\:mm} (本地时区), 判定逻辑 {(ok ? "正确" : "有问题")}");
        return (ok, lines);
    }

    private readonly ConfigStore _config;
    private readonly SignJobRunner _runner;
    private readonly ILogger<SignScheduler> _logger;

    public SignScheduler(ConfigStore config, SignJobRunner runner, ILogger<SignScheduler> logger)
    {
        _config = config;
        _runner = runner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("cron 调度器已启动");

        DateTime? lastRunMinute = null;
        DateOnly? lastRefreshDay = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = TimeUtil.Now();

                // ---- 每天一次的状态刷新（只读，不签到、不推送）----
                // 判据见 IsRefreshDue（过了 00:01 且今天没刷过 → 会补刷，不会因为
                // 那一刻机器睡着/程序没开就整天刷不到）。
                if (IsRefreshDue(now, lastRefreshDay))
                {
                    lastRefreshDay = DateOnly.FromDateTime(now);
                    _logger.LogInformation("每日状态刷新 ({Time}) → 触发", DailyRefreshTime);
                    RefreshStatusOnce();
                }

                // ---- cron 签到 ----
                var current = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Unspecified);

                var cron = _config.Read().Cron?.Trim();

                if (CronExpression.TryParse(cron, out var expression)
                    && expression.Matches(current)
                    && current != lastRunMinute)
                {
                    lastRunMinute = current;
                    _logger.LogInformation("cron 命中 {Time} → 触发运行", current.ToString("HH:mm"));

                    var options = SettingsResolver.ResolveFromConfig(_config.Read());
                    StartFireAndForget(options);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("调度器异常: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("cron 调度器已停止");
    }

    /// <summary>
    /// 异步跑一次状态刷新，不阻塞调度循环。
    ///
    /// <para>
    /// 不落日志文件：它每天 / 每次启动都跑，落盘会攒出一堆内容雷同的小日志；
    /// 输出照样会进界面日志框（<c>ConsoleJobLogger</c> 写 Console，窗体把 Console 接管了）。
    /// </para>
    /// </summary>
    private void RefreshStatusOnce()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await _runner.RunStatusRefreshAsync(writeLogFile: false).ConfigureAwait(false);

                if (outcome.SkippedReason is { } reason)
                {
                    _logger.LogInformation("{Reason}", reason);
                }
                else
                {
                    _logger.LogInformation("状态刷新退出码 {Code}", outcome.ExitCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "状态刷新异常");
            }
        });
    }

    /// <summary>异步触发一次签到，不阻塞调度循环。</summary>
    private void StartFireAndForget(SignJobOptions options)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await _runner.RunAsync("cron", options, writeLogFile: true).ConfigureAwait(false);
                if (outcome.SkippedReason is not null)
                {
                    _logger.LogInformation("{Reason}", outcome.SkippedReason);
                }
                else
                {
                    _logger.LogInformation("本次运行退出码 {Code}, 日志 {Log}", outcome.ExitCode, outcome.LogName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "cron 任务执行异常");
            }
        });
    }
}
