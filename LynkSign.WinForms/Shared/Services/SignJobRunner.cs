using LynkSign.Configuration;
using LynkSign.Logging;
using Microsoft.Extensions.Logging;

namespace LynkSign.Services;

/// <summary>一次运行的结局。</summary>
/// <param name="ExitCode">退出码；被跳过时为 null。</param>
/// <param name="LogName">日志文件名；未写日志时为 null。</param>
/// <param name="SkippedReason">被跳过的原因；正常执行时为 null。</param>
public sealed record RunOutcome(int? ExitCode, string? LogName, string? SkippedReason);

/// <summary>
/// 任务执行器，对应 Python 版 <c>config_server.py</c> 的 <c>run_job()</c>。
///
/// 与 Python 版的<b>关键架构差异</b>：Python 版通过 <c>subprocess.Popen</c> 拉起
/// <c>ql_lynk.py</c> 子进程、把 stdout 流式写进日志文件，还要额外处理 600 秒看门狗与
/// Windows GBK 编码问题。.NET 版直接把签到流程跑在进程内，用
/// <see cref="RunLogWriter"/> 同时写控制台与文件，因此没有子进程管理、编码、超时残留等问题。
///
/// 仍然保留「同一时刻只允许一个任务」的并发约束。
/// </summary>
public sealed class SignJobRunner
{
    private readonly ConfigStore _config;
    private readonly SignJob _job;
    private readonly StatusRefreshJob _status;
    private readonly RunLogStore _logs;
    private readonly JobState _state;
    private readonly ILogger<SignJobRunner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SignJobRunner(
        ConfigStore config,
        SignJob job,
        StatusRefreshJob status,
        RunLogStore logs,
        JobState state,
        ILogger<SignJobRunner> logger)
    {
        _config = config;
        _job = job;
        _status = status;
        _logs = logs;
        _state = state;
        _logger = logger;
    }

    /// <summary>当前是否有任务在跑。</summary>
    public bool IsRunning => _state.Running;

    /// <summary>
    /// 只刷一次签到状态：<b>不签到、不推送</b>。给「每天 00:01」的定时刷新与
    /// <c>--refresh-status</c> 用。
    ///
    /// <para>
    /// 与 <see cref="RunAsync"/> 共用同一把闸（避免和签到抢同一个 token 缓存）与同一套日志落盘，
    /// 但<b>不碰 <see cref="JobState"/></b>：状态刷新不是「一次签到运行」，
    /// 不该把界面上的「最近运行 / 调度器」改写成它的结果，否则看着像 00:01 签到了。
    /// </para>
    /// </summary>
    /// <param name="trigger">触发方式，同时是日志文件名后缀。</param>
    /// <param name="writeLogFile">
    /// 是否落日志文件。定时刷新传 <c>false</c>：它每天/每次启动都跑，
    /// 落盘会攒出一堆内容雷同的小日志；输出照常进界面日志框（<see cref="ConsoleJobLogger"/>）。
    /// <c>--refresh-status</c> 传 <c>true</c>，让报告作为文件留档。
    /// </param>
    /// <param name="options">
    /// 已解析的配置；为 null 时从 config.json 现取。命令行入口会传「命令行 &gt; 环境变量 &gt; config.json」
    /// 解析出来的那一份，于是 <c>--token</c> 这类参数在这里也生效，与 <c>--fetch-article</c> 同口径。
    /// </param>
    public async Task<RunOutcome> RunStatusRefreshAsync(
        string trigger = StatusRefreshJob.Trigger,
        bool writeLogFile = true,
        SignJobOptions? options = null,
        CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return new RunOutcome(null, null, "已有任务在运行中, 跳过本次状态刷新");
        }

        try
        {
            options ??= SettingsResolver.ResolveFromConfig(_config.Read());

            // 还没配 token 就干脆不跑，也别落日志文件 —— 否则每次启动都会多一个内容相同的空日志。
            if (string.IsNullOrWhiteSpace(options.RefreshToken)
                || options.RefreshToken == SettingsResolver.PlaceholderToken)
            {
                return new RunOutcome(null, null, "未配置 refreshToken, 跳过状态刷新");
            }

            if (!writeLogFile)
            {
                using var consoleLogger = new ConsoleJobLogger();
                var code = await _status.RunAsync(options, consoleLogger, ct).ConfigureAwait(false);
                return new RunOutcome(code, null, null);
            }

            var logPath = _logs.NewLogPath(trigger);
            using var writer = new RunLogWriter(logPath, trigger);

            var rc = await _status.RunAsync(options, writer, ct).ConfigureAwait(false);

            writer.Complete(rc);
            return new RunOutcome(rc, writer.LogName, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("状态刷新已取消");
            return new RunOutcome(99, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "状态刷新异常");
            return new RunOutcome(99, null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 跑一次签到。
    /// </summary>
    /// <param name="trigger">
    /// 触发方式：cron / manual（会写进日志文件名）。
    /// 它同时也是「签到后要不要自动打开浏览器」的判据 —— 见
    /// <see cref="LynkSign.Infrastructure.ShareUrlOpenModes.ShouldOpen"/>。
    /// </param>
    /// <param name="options">已解析的配置；为 null 时从 config.json 现取。</param>
    /// <param name="writeLogFile">是否落日志文件（命令行单跑模式只需控制台输出）。</param>
    public async Task<RunOutcome> RunAsync(
        string trigger,
        SignJobOptions? options = null,
        bool writeLogFile = true,
        CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return new RunOutcome(null, null, "已有任务在运行中, 跳过本次");
        }

        try
        {
            options ??= SettingsResolver.ResolveFromConfig(_config.Read());

            if (!writeLogFile)
            {
                using var consoleLogger = new ConsoleJobLogger();
                var exitCode = await ExecuteAsync(options, consoleLogger, trigger, ct).ConfigureAwait(false);
                return new RunOutcome(exitCode, null, null);
            }

            var logPath = _logs.NewLogPath(trigger);
            using var writer = new RunLogWriter(logPath, trigger);
            _state.MarkStarted(writer.LogName);

            var rc = await ExecuteAsync(options, writer, trigger, ct).ConfigureAwait(false);

            writer.Complete(rc);
            _state.MarkFinished(rc);

            return new RunOutcome(rc, writer.LogName, null);
        }
        finally
        {
            _state.Running = false;
            _gate.Release();
            _logs.Prune();
        }
    }

    private async Task<int> ExecuteAsync(SignJobOptions options, IJobLogger log, string trigger, CancellationToken ct)
    {
        try
        {
            return await _job.RunAsync(options, log, ct, trigger).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.Err("任务已取消");
            return 99;
        }
        catch (Exception ex)
        {
            log.Err($"未捕获异常: {ex.Message}");
            log.Err(ex.ToString());
            _logger.LogError(ex, "签到任务崩溃");
            return 99;
        }
    }
}
