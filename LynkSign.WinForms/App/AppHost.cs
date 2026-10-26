using LynkSign.Configuration;
using LynkSign.Infrastructure;
using LynkSign.Lynk;
using LynkSign.Push;
using LynkSign.Scheduling;
using LynkSign.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LynkSign.WinForms.App;

/// <summary>
/// 桌面版的服务容器。
///
/// <para>
/// 对象图是从 Web 版（已删除）的 <c>builder.Services</c> 段落整体搬过来的，只有两处差别：
/// <list type="number">
///   <item>没有 Kestrel / Minimal API（桌面版不需要 HTTP 服务）；</item>
///   <item>没有 <c>AddHostedService</c>（调度器由窗体在后台线程手动 <c>StartAsync</c>）。</item>
/// </list>
/// 刻意保持同一套对象图，是为了让业务行为不因「换了个壳」而跑偏 ——
/// 界面、<c>--run-once</c>、cron 三条入口跑的都是这里装配出来的同一批对象。
/// </para>
/// </summary>
internal sealed class AppHost : IDisposable
{
    private readonly ServiceProvider _provider;
    private bool _disposed;

    public AppHost()
    {
        var services = new ServiceCollection();

        // 业务日志一律走 IJobLogger（最终被 UiLogWriter 接进窗体日志框）。
        // 这里只承接框架内部（HttpClient / 调度器）的告警，避免噪音。
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

        services.AddSingleton<ConfigStore>();
        services.AddSingleton<TokenCache>();
        services.AddSingleton<JobState>();
        services.AddSingleton<RunLogStore>();
        services.AddSingleton<PointsHistoryStore>();

        // 命名 HttpClient：与原项目一致（lynk 20s / push 10s）。
        // handler 走 HttpClientSetup 显式构造 —— 默认**不吃系统代理**：
        // 代理软件（Clash/Mihomo…）退出后注册表里的 127.0.0.1:7897 会残留，
        // 默认 handler 会把所有请求打向死端口，报"由于目标计算机积极拒绝，无法连接"。
        services.AddHttpClient(LynkApiClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(sp => HttpClientSetup.CreateHandler(sp.GetRequiredService<ConfigStore>().Read().HttpProxy));
        services.AddHttpClient(PushService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(sp => HttpClientSetup.CreateHandler(sp.GetRequiredService<ConfigStore>().Read().HttpProxy));

        services.AddSingleton<LynkApiClient>();
        services.AddSingleton<PushService>();
        services.AddSingleton<TokenManager>();
        services.AddSingleton<SignJob>();
        services.AddSingleton<StatusRefreshJob>();
        services.AddSingleton<SignJobRunner>();
        services.AddSingleton<SignScheduler>();

        _provider = services.BuildServiceProvider();
    }

    public ConfigStore Config => _provider.GetRequiredService<ConfigStore>();

    public RunLogStore Logs => _provider.GetRequiredService<RunLogStore>();

    /// <summary>
    /// 积分变化历史（数据目录的 <c>points-history.log</c>）。
    /// 写它的是签到流程与每日状态刷新；界面读它填「账户积分」与「积分变化」两块。
    /// </summary>
    public PointsHistoryStore Points => _provider.GetRequiredService<PointsHistoryStore>();

    public JobState State => _provider.GetRequiredService<JobState>();

    public TokenCache TokenCache => _provider.GetRequiredService<TokenCache>();

    /// <summary>
    /// 领克接口客户端。窗口自己逻辑上用不到它（业务都走 <see cref="Runner"/>），
    /// 唯一的使用者是「验证原生通道密钥」那次探测请求 —— 它需要直接打一次网关，
    /// 而不是跑整套签到流程。
    /// </summary>
    public LynkApiClient Api => _provider.GetRequiredService<LynkApiClient>();

    public SignJobRunner Runner => _provider.GetRequiredService<SignJobRunner>();

    /// <summary>
    /// 令牌管理。窗口本身不跑签到，唯一的使用者是「拉取一次最新文章」——
    /// 那个接口也要 accessToken，复用同一条「缓存 → refresh」链路而不是另起一套。
    /// </summary>
    public TokenManager Tokens => _provider.GetRequiredService<TokenManager>();

    public SignScheduler Scheduler => _provider.GetRequiredService<SignScheduler>();

    /// <summary>
    /// 当前出站网络策略的一行描述（如 <c>直连 · 已忽略系统代理 http://127.0.0.1:7897</c>）。
    /// 每次读取都现算，保证与 config.json 的当前值一致；
    /// 由窗体在接管 Console 之后打进日志框 —— 排查"连接被拒"时第一眼就该看到这行。
    /// </summary>
    public string NetworkMode => HttpClientSetup.Describe(Config.Read().HttpProxy);

    /// <summary>
    /// 首次运行的数据准备：建目录、补默认 config.json、清理过期日志。
    /// 对应原项目 <c>Program.cs</c> 里 Web 启动前的那一段初始化。
    /// </summary>
    public (bool Created, string ConfigPath, string DataDir) PrepareStorage()
    {
        Directory.CreateDirectory(AppPaths.WorkDir);

        var created = false;
        if (!File.Exists(Config.Path))
        {
            Config.Write(AppConfig.CreateDefault());
            created = true;
        }

        Directory.CreateDirectory(Logs.Dir);
        Logs.Prune();

        return (created, Config.Path, AppPaths.WorkDir);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _provider.Dispose();
        }
        catch
        {
            // 退出阶段忽略释放异常
        }
    }
}
