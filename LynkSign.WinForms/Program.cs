using System.Drawing.Imaging;
using System.Text;
using LynkSign.Logging;
using LynkSign.Lynk;
using LynkSign.Services;
using LynkSign.WinForms.App;
using LynkSign.WinForms.Forms;

namespace LynkSign.WinForms;

/// <summary>
/// 桌面版入口，也是本仓库唯一的可执行入口
/// （Web 配置中心版 <c>src/LynkSign</c> 已删除，业务源码全部住在 <c>Shared/</c> 下）。
///
/// <para>命令行开关（安装包与开机自启会用到）：</para>
/// <list type="bullet">
///   <item><c>--minimized</c>：启动后不显示主窗口，直接缩到托盘（开机自启用）；</item>
///   <item><c>--data-dir &lt;目录&gt;</c>：覆盖数据目录（默认 <c>%APPDATA%\LynkSign</c>）；</item>
///   <item><c>--help</c>：打印用法后退出。<b>必须显式接住</b> —— WinExe 没有控制台自动挂载，
///         不接的话 <c>--help</c> 会被当成"没有参数"，直接弹出一个窗口，比没有帮助更让人困惑；</item>
///   <item><c>--run-once</c> / <c>--once</c>：不进界面，跑一次签到后退出。给任务计划程序 /
///         Jenkins / 排障用 —— 这类场景不该被逼着开窗口点按钮。日志照常落盘，
///         签到参数（<c>--token</c> / <c>--no-cache</c> / <c>--content-id</c> …）在这里才真正生效，
///         退出码沿用签到流程的语义；</item>
///   <item><c>--clear-cache</c>：清空 accessToken 缓存后退出；</item>
///   <item><c>--install-autostart</c> / <c>--uninstall-autostart</c>：只改注册表后退出（安装包用）；</item>
///   <item><c>--selftest</c>：跑一遍链路自检，报告写进数据目录的 selftest.log 后退出；</item>
///   <item><c>--verify-native-secret</c>：拿当前配置里的原生通道密钥，向签到端点发一次
///         <b>探测请求</b>（用假 token，不会真的签到），据此判断密钥对不对；
///         报告同时打到 stdout 与数据目录的 verify-native-secret.log，退出码 0 = 密钥可用；</item>
///   <item><c>--fetch-article</c>：实拉一次探索广场文章流，打印取到的最新一篇的 id 与标题，
///         用来验证「分享文章 ID 自动拉取」这条链路；<b>不会签到、不改配置</b>
///         （除了 refreshToken 轮换必需的写回），报告落数据目录的 fetch-article.log，退出码 0 = 取到文章；</item>
///   <item><c>--refresh-status</c>：只拉一次最新签到状态（day/info + 连续天数 / 补签卡 + 账户信息），
///         <b>不签到、不推送</b>。与托盘版「每天 00:01 自动刷新」是同一个任务，
///         日志落 <c>logs/run-…-status.log</c>，退出码 0 = 拉到状态；</item>
///   <item><c>--import-har &lt;抓包文件&gt;</c>：从 HAR 抓包里认出
///         <c>POST …/auth/login/mobileCodeLogin</c> 那一条，把 <c>deviceId</c>（请求 query）
///         与 <c>refreshToken</c>（响应 <c>data.centerTokenDto</c>）一起写回 config.json，
///         与窗体上那颗「从抓包导入…」按钮共用同一套解析；报告落 import-har.log，退出码 0 = 导入成功；</item>
///   <item><c>--screenshot &lt;目录&gt;</c>：把两个页签离屏渲染成 PNG 后退出（调外观用）；</item>
///   <item><c>--screenshot-live &lt;目录&gt;</c>：真开一次窗口、抓屏存 PNG 后退出。
///         离屏那套走的是 <c>WM_PRINT</c>，<b>不经过双缓冲</b>，缓冲区残留类的问题在它眼里是隐形的；
///         要判断"真机上到底长什么样"，只能看这个。</item>
/// </list>
/// </summary>
internal static class Program
{
    internal const string AppTitle = "领克自动签到";

    private const string MutexName = @"Global\LynkSignGUI.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        // ---------- 1) 数据目录 ----------
        // AppPaths 是静态类，首次访问就把 WorkDir 定死了，所以必须赶在任何业务类型被触碰之前
        // 落到环境变量上（与 Web 版 Program.cs 里同一处讲究）。
        // 桌面版默认落 %APPDATA%\LynkSign：
        //   · 装到 Program Files 时程序目录是只读的，写不了 config.json；
        //   · 覆盖安装 / 卸载重装都不会把配置和日志带走。
        var dataDir = ResolveDataDir(args);
        Environment.SetEnvironmentVariable("LYNK_DATA_DIR", dataDir);

        try
        {
            Directory.CreateDirectory(dataDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"无法创建数据目录：\n{dataDir}\n\n{ex.Message}",
                AppTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        // ---------- 1.5) 只写注册表的开关：做完立刻退出 ----------
        // 安装包勾选「开机自启」时会调这里。放在 WinForms 初始化之前，
        // 这样写一个注册表值不会把窗口弹出来。
        if (HasSwitch(args, "install-autostart") || HasSwitch(args, "uninstall-autostart"))
        {
            var (ok, info) = HasSwitch(args, "install-autostart")
                ? WinAutostart.Install()
                : WinAutostart.Uninstall();

            Console.Error.WriteLine($"{(ok ? "OK" : "FAIL")} {info}");
            return ok ? 0 : 1;
        }

        // ---------- 1.6) --verify-native-secret：只发一次探测请求，不进界面 ----------
        // 放在单实例锁之前：某个实例正开着托盘运行时，用户也该能随时验证密钥，
        // 不该被"程序已经在运行了"挡回来。
        if (HasSwitch(args, "verify-native-secret"))
        {
            return VerifyNativeSecret(args, dataDir);
        }

        // ---------- 1.7) --fetch-article：实拉一次文章流，不进界面 ----------
        // 同上，也放在单实例锁之前：托盘里已经跑着一个实例时，也要能做这次诊断。
        if (HasSwitch(args, "fetch-article"))
        {
            return FetchArticle(args, dataDir);
        }

        // ---------- 1.75) --refresh-status：只拉一次最新签到状态，不进界面 ----------
        // 与「每天 00:01 自动刷新」是同一件事（共用 StatusRefreshJob），
        // 区别只是在这里能立刻跑一次，用来验证这条链路通不通。
        // 同样放在单实例锁之前：托盘里常驻着实例时也该能手动刷一次。
        if (HasSwitch(args, "refresh-status"))
        {
            return RefreshStatus(args, dataDir);
        }

        // ---------- 1.8) --import-har：从抓包文件导入凭据，不进界面 ----------
        // 与窗体上那颗「从抓包导入…」按钮同一件事，区别只在于文件路径从命令行给。
        // 放在单实例锁之前：托盘里常驻着实例时，命令行导入也应该能用。
        //
        // 这里额外认一下内联写法（--import-har=文件）：HasSwitch 只匹配独立的开关，
        // 漏掉它的话 --import-har=x 会被当成「没有这个参数」，于是**静默弹出主窗口** ——
        // 用户以为在导配置，实际得到一个大窗口挂在那里，正是最不该有的表现。
        if (HasSwitch(args, "import-har") || HasInlineValue(args, "import-har"))
        {
            return ImportHar(args, dataDir);
        }

        // ---------- 1.9) --help：打印用法后退出 ----------
        // 这个开关必须显式接住。WinExe 是 GUI 子系统程序，不会自动挂到父控制台上，
        // 所以"没人处理"的表现不是打印不了帮助，而是**直接弹出主窗口** ——
        // 用户想查用法却得到一个界面，比没有帮助更让人困惑。
        if (HasSwitch(args, "help") || HasSwitch(args, "h"))
        {
            CliOptions.PrintHelp();
            return 0;
        }

        // ---------- 1.10) --clear-cache：清空 token 缓存后退出 ----------
        // 换账号 / 怀疑缓存串号时的第一招。与窗体上那颗「清空 token 缓存」按钮同一件事。
        if (HasSwitch(args, "clear-cache"))
        {
            using var cacheHost = new AppHost();
            cacheHost.TokenCache.Clear();
            Console.WriteLine("accessToken 缓存已清空");
            return 0;
        }

        // ---------- 1.11) --run-once：不进界面跑一次签到后退出 ----------
        // 同样放在单实例锁之前：托盘里已经常驻着一个实例时，"只想再补跑一次"也必须能用，
        // 不该被"程序已经在运行了"挡回来（两边的并发约束各自独立，签到流程本身幂等）。
        if (HasSwitch(args, "run-once") || HasSwitch(args, "once"))
        {
            return RunOnce(args, dataDir);
        }

        // ---------- 2) 单实例 ----------
        // 托盘程序最怕用户以为没启动而反复双击，最后攒出一堆进程同时签到。
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (!SingleInstance.ActivateExisting(AppTitle))
            {
                MessageBox.Show(
                    "程序已经在运行了，请查看任务栏右下角的托盘图标。",
                    AppTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return 0;
        }

        // ---------- 3) WinForms 初始化 ----------
        // 等价于模板里的 ApplicationConfiguration.Initialize()，手写是为了能显式指定中文字体。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F));

        // ---------- 4) 兜底异常 ----------
        Application.ThreadException += (_, e) => CrashReport.Show(e.Exception, "UI 线程");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashReport.Show(e.ExceptionObject as Exception, "后台线程");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            CrashReport.Show(e.Exception, "后台任务");
        };

        // ---------- 5) 起界面 ----------
        using var host = new AppHost();

        // --selftest：不显示窗口跑一遍关键链路自检，报告写进数据目录的 selftest.log。
        // 用户反馈「装完打不开 / 界面空白」时，让 TA 跑这个就能拿到现场诊断信息。
        if (HasSwitch(args, "selftest"))
        {
            using var probe = new MainForm(host, startMinimized: false);
            return RunSelfTest(probe, dataDir);
        }

        // --screenshot-live <目录>：真开窗口抓屏。放在离屏截图之前判断 ——
        // 两个开关名有前缀包含关系，先匹配更长的那个，避免 --screenshot 抢走 --screenshot-live。
        if (ResolveValue(args, "screenshot-live") is { } liveDir)
        {
            using var live = new MainForm(host, startMinimized: false, rememberWindowSize: false);
            return RenderLiveScreenshots(live, liveDir) ? 0 : 1;
        }

        // --screenshot <目录>：把界面离屏渲染成 PNG。
        // 界面是自绘的，"编译通过"完全不能代表"长得对"，所以留一个能真看图的入口。
        if (ResolveValue(args, "screenshot") is { } shotDir)
        {
            using var shot = new MainForm(host, startMinimized: false, rememberWindowSize: false);
            return RenderScreenshots(shot, shotDir) ? 0 : 1;
        }

        using var form = new MainForm(host, startMinimized: HasSwitch(args, "minimized"));

        Application.Run(form);
        return 0;
    }

    /// <summary>
    /// 验证 config.json / 环境变量里的原生通道密钥能否被网关接受（<c>--verify-native-secret</c>）。
    ///
    /// <para>
    /// 为什么需要它：签到接口只授权给原生 iOS SDK 通道，而这条通道的 secret
    /// 不在抓包里、也无法从签名反推，只能人工去 App 安装包里挖。挖错了<b>不会有任何提示</b> ——
    /// 要等到下一次真正签到失败才知道，而那时已经白丢一天。这里用一个假 token
    /// 打一次探测请求，把「密钥对不对」实时问清楚，且不可能真的签成。
    /// </para>
    ///
    /// <para>
    /// 退出码：0 = 密钥已被网关接受（可以放心用）；1 = 密钥有问题或网络不通，具体原因见报告。
    /// </para>
    /// </summary>
    private static int VerifyNativeSecret(string[] args, string dataDir)
    {
        var report = "";
        var ok = false;

        try
        {
            using var host = new AppHost();
            host.PrepareStorage();

            // 与签到主流程共用同一套取值顺序（命令行 > 环境变量 > config.json）。
            // 这样「这里验证通过」就等价于「签到时会用同一串密钥」，不会出现两套取值各说各话。
            var options = SettingsResolver.Resolve(CliOptions.Parse(args), host.Config.Read());
            var profile = options.NativeProfile();

            var result = NativeChannelProbe.RunAsync(host.Api, profile).GetAwaiter().GetResult();
            report = NativeChannelProbe.Format(result, profile);
            ok = result.SecretAccepted;
        }
        catch (Exception ex)
        {
            report = $"探测异常: {ex.GetType().Name}: {ex.Message}";
        }

        Console.WriteLine(report);

        // 同时落一份文件：安装包形态下用户多半是从开始菜单起的进程，
        // 没有终端窗口可看，报告只能靠文件带出去。
        try
        {
            File.WriteAllText(Path.Combine(dataDir, "verify-native-secret.log"), report, Encoding.UTF8);
        }
        catch
        {
            // 写不进去就只能靠 stdout 与退出码了
        }

        return ok ? 0 : 1;
    }

    /// <summary>
    /// 实拉一次探索广场文章流，报告取到的最新一篇文章（<c>--fetch-article</c>）。
    ///
    /// <para>
    /// 为什么需要它：「分享文章 ID 自动拉取」依赖第三方接口的返回结构
    /// （哪一层是内容流、articleId 字段叫什么、分页参数要不要带）。结构一变，
    /// 程序只会<b>静默</b>回退到配置里的旧 id，从签到日志上完全看不出异常 ——
    /// 而那时文章 id 可能早已失效，分享也就悄悄不加分了。这个开关把那一步单独拎出来跑，
    /// 能把「接口通不通、能不能解析出文章」当场问清楚；它也顺手验证了 token 链路。
    /// </para>
    ///
    /// <para>退出码：0 = 取到文章；1 = 失败（原因见报告）。</para>
    /// </summary>
    private static int FetchArticle(string[] args, string dataDir)
    {
        var report = "";
        var ok = false;

        try
        {
            using var host = new AppHost();
            host.PrepareStorage();

            // 与签到主流程共用同一套取值顺序（命令行 > 环境变量 > config.json），
            // 保证「这里能取到」意味着「签到时会取到同一篇」。
            var options = SettingsResolver.Resolve(CliOptions.Parse(args), host.Config.Read());

            if (string.IsNullOrWhiteSpace(options.RefreshToken)
                || options.RefreshToken == SettingsResolver.PlaceholderToken)
            {
                report = "未配置 refreshToken, 无法换 accessToken";
            }
            else
            {
                using var log = new ConsoleJobLogger();
                var token = host.Tokens
                    .GetAccessTokenAsync(options.RefreshToken, options.DeviceId, log, force: false, CancellationToken.None)
                    .GetAwaiter().GetResult();

                if (!token.Ok)
                {
                    report = $"拿不到 accessToken: {token.Source}";
                }
                else
                {
                    // refresh 轮换出的新 refreshToken 必须落盘 —— 否则「跑一次诊断」
                    // 反而会把新 token 留在缓存里、配置里还是旧的。
                    var (rotated, wrote, info) = TokenRotationWriteBack.Apply(host.Config, options.RefreshToken, token);

                    var (article, error) = host.Api
                        .GetLatestArticleAsync(token.AccessToken!, CancellationToken.None)
                        .GetAwaiter().GetResult();

                    if (article is null)
                    {
                        report = $"未取到文章: {error}";
                    }
                    else
                    {
                        ok = true;
                        report = string.Join(Environment.NewLine,
                            $"accessToken 来源: {token.Source}",
                            rotated ? (wrote ? "refreshToken 已轮换并写回 config.json" : $"refreshToken 写回失败: {info}") : "refreshToken 未轮换",
                            "",
                            $"最新文章: {article.Display}",
                            $"id={article.ArticleId}",
                            $"端点: {LynkConstants.EpExploreSquare} (最多翻 {LynkConstants.ExploreSquarePageCount} 页)");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            report = $"拉取异常: {ex.GetType().Name}: {ex.Message}";
        }

        Console.WriteLine(report);

        // 与 --verify-native-secret 同理：安装包形态下用户没有终端窗口可看，报告得靠文件带出去。
        try
        {
            File.WriteAllText(Path.Combine(dataDir, "fetch-article.log"), report, Encoding.UTF8);
        }
        catch
        {
            // 写不进去就只能靠 stdout 与退出码了
        }

        return ok ? 0 : 1;
    }

    /// <summary>
    /// 只拉一次最新签到状态后退出（<c>--refresh-status</c>）。
    ///
    /// <para>
    /// 与托盘版「每天 00:01 自动刷新」跑的是同一个 <see cref="StatusRefreshJob"/>：
    /// <b>不签到、不推送</b>，只把 day/info + 连续天数 / 补签卡 + 账户信息读回来，
    /// 并以能被界面解析的状态行写进日志（界面那三张状态卡的数据源就是它）。
    /// 手动跑一次的价值在于：这条链路依赖四个第三方接口的返回结构，结构一变只会静默降级。
    /// </para>
    ///
    /// <para>
    /// 退出码：0 拉到状态 / 1 未配置 refreshToken / 2 拿不到 accessToken /
    /// 3 状态没能解析出来 / 99 未捕获异常。
    /// </para>
    /// </summary>
    private static int RefreshStatus(string[] args, string dataDir)
    {
        try
        {
            using var host = new AppHost();
            host.PrepareStorage();

            // 取值顺序与其它 CLI 入口一致（命令行 > 环境变量 > config.json），
            // 于是「--token 随便填一个」也能拿来试跑这条链路而不碰真实账号。
            var options = SettingsResolver.Resolve(CliOptions.Parse(args), host.Config.Read());

            // 落日志文件：这是诊断入口，报告得能带走（安装包形态下没终端可看）。
            var outcome = host.Runner
                .RunStatusRefreshAsync(
                    trigger: StatusRefreshJob.Trigger,
                    writeLogFile: true,
                    options: options,
                    ct: CancellationToken.None)
                .GetAwaiter().GetResult();

            if (outcome.SkippedReason is { } reason)
            {
                Console.Error.WriteLine(reason);
                return 1;
            }

            if (outcome.LogName is { } logName)
            {
                Console.WriteLine($"日志: {Path.Combine(dataDir, "logs", logName)}");
            }

            return outcome.ExitCode ?? 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"未捕获异常: {ex.GetType().Name}: {ex.Message}");
            return 99;
        }
    }

    /// <summary>
    /// 从抓包文件（HAR）导入 refreshToken + deviceId 后退出
    /// （<c>--import-har &lt;抓包文件&gt;</c>）。
    ///
    /// <para>
    /// 与窗体上那颗「从抓包导入…」按钮同一件事，只是文件路径从命令行给：
    /// 手上有若干份抓包、想逐个试的时候，比反复点界面选文件顺手。
    /// 解析规则完全共用 <see cref="HarCredentialExtractor"/>，不存在两套逻辑。
    /// </para>
    ///
    /// <para>退出码：0 = 导入成功；1 = 失败（原因见报告）。</para>
    /// </summary>
    private static int ImportHar(string[] args, string dataDir)
    {
        var report = "";
        var ok = false;

        try
        {
            var harPath = ResolveValue(args, "import-har");

            if (string.IsNullOrWhiteSpace(harPath))
            {
                report = "用法: LynkSignGUI.exe --import-har <抓包文件.har> [--data-dir <目录>]";
            }
            else
            {
                using var host = new AppHost();
                host.PrepareStorage();

                var scan = HarCredentialExtractor.ScanFile(harPath);

                if (!scan.Ok)
                {
                    report = string.Join(Environment.NewLine,
                        $"导入失败: {scan.Error}",
                        $"文件: {harPath}",
                        scan.MatchedEntries > 0 ? $"抓包里命中登录请求 {scan.MatchedEntries} 条" : "");
                }
                else
                {
                    var credential = scan.Credential!;

                    var (saved, info) = host.Config.UpdateField(cfg =>
                    {
                        cfg.RefreshToken = credential.RefreshToken;
                        cfg.DeviceId = credential.DeviceId;
                    });

                    ok = saved;

                    report = string.Join(Environment.NewLine,
                        saved ? "导入成功" : $"解析成功, 但写回配置失败: {info}",
                        "",
                        credential.Describe(),
                        $"deviceId={credential.DeviceId}",
                        scan.MatchedEntries > 1
                            ? $"[!] 抓包里有 {scan.MatchedEntries} 条登录记录, 已采用最后一条（最新那次登录）"
                            : $"命中登录请求 {scan.MatchedEntries} 条",
                        $"配置文件: {host.Config.Path}",
                        "",
                        "[!] 抓包文件里含明文 token, 导入完成后建议把它删掉");
                }
            }
        }
        catch (Exception ex)
        {
            report = $"导入异常: {ex.GetType().Name}: {ex.Message}";
        }

        Console.WriteLine(report);

        // 与其他无界面入口同理：安装包形态下用户没有终端可看，报告得靠文件带出去。
        try
        {
            File.WriteAllText(Path.Combine(dataDir, "import-har.log"), report, Encoding.UTF8);
        }
        catch
        {
            // 写不进去就只能靠 stdout 与退出码了
        }

        return ok ? 0 : 1;
    }

    /// <summary>
    /// 不进界面跑一次签到后退出（<c>--run-once</c> / <c>--once</c>）。
    ///
    /// <para>
    /// 与窗体上「立即运行」的唯一区别是<b>不进界面、跑完就退</b>：给任务计划程序、
    /// Jenkins、"只想补跑今天这一次"的场景用。触发方式按 <c>manual</c> 记，
    /// 因为这是用户主动发起的 —— 于是 <c>open_share_url=manual</c> 时该弹的浏览器照弹，
    /// 与在窗口里点按钮的行为一致；只有 cron 档才会静音。
    /// </para>
    ///
    /// <para>
    /// 取值顺序与窗体完全一致（命令行 &gt; 环境变量 &gt; config.json），
    /// 所以 <c>--token</c> / <c>--no-cache</c> / <c>--content-id</c> / <c>--share-content-mode</c>
    /// 这些参数<b>只在这条路径上才真正生效</b> —— 窗体里的值一律来自 config.json。
    /// </para>
    ///
    /// <para>
    /// 退出码沿用签到流程自己的语义，便于被调度器判读：
    /// 0 成功 / 1 未配置 refreshToken / 2 续 token 失败 / 3 查签到状态失败 /
    /// 4 签到接口失败 / 99 未捕获异常。
    /// </para>
    /// </summary>
    private static int RunOnce(string[] args, string dataDir)
    {
        try
        {
            using var host = new AppHost();
            host.PrepareStorage();

            var options = SettingsResolver.Resolve(CliOptions.Parse(args), host.Config.Read());

            if (string.IsNullOrWhiteSpace(options.RefreshToken)
                || options.RefreshToken == SettingsResolver.PlaceholderToken)
            {
                Console.Error.WriteLine("未配置 refreshToken, 无法执行签到");
                return 1;
            }

            // writeLogFile: true —— 与托盘版共用同一套日志落盘，
            // 于是事后能在界面「运行日志」里翻到这次 CLI 跑的结果；
            // 控制台那边另有 ConsoleJobLogger 的兄弟实现，这里不需要它。
            var outcome = host.Runner
                .RunAsync("manual", options, writeLogFile: true, CancellationToken.None)
                .GetAwaiter().GetResult();

            if (outcome.SkippedReason is { } reason)
            {
                Console.Error.WriteLine(reason);
                return 1;
            }

            if (outcome.LogName is { } logName)
            {
                Console.WriteLine($"日志: {Path.Combine(dataDir, "logs", logName)}");
            }

            return outcome.ExitCode ?? 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"未捕获异常: {ex.GetType().Name}: {ex.Message}");
            return 99;
        }
    }

    /// <summary>
    /// 离屏渲染两个页签并存成 PNG。
    ///
    /// <para>
    /// 这里必须真的 <c>Show()</c> 一次（挪到屏幕外，不会闪到用户）：
    /// 只 <c>CreateControl()</c> 的话，Dock / AutoSize / 自绘控件的布局不会真正跑完，
    /// <c>DrawToBitmap</c> 出来的是一片空白。
    /// </para>
    /// </summary>
    private static bool RenderScreenshots(MainForm form, string outDir)
    {
        try
        {
            Directory.CreateDirectory(outDir);

            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-4000, -4000);
            form.Show();

            Pump(14);

            var pages = new[] { "01-运行状态", "02-配置" };

            for (var i = 0; i < pages.Length; i++)
            {
                form.SelectTab(i);
                Pump(10);

                // 配置页比窗口高，截图时临时把窗口拉高，让整页都进画面
                // —— 否则最下面几张卡片会被视口裁掉，看不出它自己有没有被边框裁。
                // 拉多高由内容自己算（ConfigContentHeight），不写死像素，
                // 免得以后加了卡片又得回来调这个数字。
                if (i == 1)
                {
                    const int chrome = 240; // 标题栏 + 页签 + 按钮条 + 状态栏
                    form.ClientSize = new Size(form.ClientSize.Width, form.ConfigContentHeight + chrome);
                    Pump(10);
                }

                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Path.Combine(outDir, $"{pages[i]}.png"), ImageFormat.Png);

                // 同时落一份布局体检：实际尺寸 vs 首选尺寸，专门用来抓"底部被裁"。
                // 图是给人看的，这份文本是给自动化看的（人眼比对太慢，而 CI 里没眼睛）。
                File.WriteAllText(
                    Path.Combine(outDir, $"{pages[i]}-layout.txt"),
                    form.DumpLayout(i),
                    Encoding.UTF8);
            }

            form.Close();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把消息泵转起来，等布局与重绘稳定。</summary>
    private static void Pump(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            Application.DoEvents();
            Thread.Sleep(60);
        }
    }

    /// <summary>
    /// 真开一次窗口、从屏幕上抓像素存 PNG。
    ///
    /// <para>
    /// 和 <see cref="RenderScreenshots"/> 的区别是本质性的：那边用
    /// <c>DrawToBitmap</c>（底层是 <c>WM_PRINT</c>），每个控件直接把内容画进传进来的
    /// 内存 DC，<b>完全不碰双缓冲缓冲区</b>。而自绘控件最典型的脏像素来源
    /// ——「缓冲区里失效区域之外的残留」—— 恰好只在这条路上不出现。
    /// 于是离屏截图会给出「一切正常」的假象，真机上却是黑角、重影文字。
    /// </para>
    ///
    /// <para>
    /// 所以凡是判断"真机长什么样"，都得以这个模式为准。代价是窗口必须真的可见、
    /// 不被遮挡，所以它会短暂出现在屏幕中央然后自己关掉。
    /// </para>
    /// </summary>
    private static bool RenderLiveScreenshots(MainForm form, string outDir)
    {
        try
        {
            Directory.CreateDirectory(outDir);

            form.StartPosition = FormStartPosition.CenterScreen;

            // 窗口必须整窗落在屏内，否则 CopyFromScreen 抓到的屏幕外那部分是垃圾像素
            // （下面配置页宁可滚动分屏补拍也是这个原因）。
            // 窗口尺寸现在是可记忆的，用户把它拖得比屏幕还大时，这条验收路径会
            // 静默产出坏图 —— 所以这里主动夹一次，让验收结论始终可信。
            var work = Screen.PrimaryScreen?.WorkingArea.Size ?? Size.Empty;
            if (!work.IsEmpty && (form.Width > work.Width || form.Height > work.Height))
            {
                form.Size = new Size(
                    Math.Min(form.Width, work.Width),
                    Math.Min(form.Height, work.Height));
            }

            form.Show();
            form.Activate();
            form.BringToFront();
            Pump(12);

            var pages = new[] { "01-运行状态", "02-配置" };

            for (var i = 0; i < pages.Length; i++)
            {
                form.SelectTab(i);
                Pump(6);

                // 强制整窗重画一次，确保抓到的不是启动过程中的中间态。
                // 配置页窗口保持原尺寸，靠下面的滚动分屏覆盖整页 ——
                // 窗口一旦高过屏幕，CopyFromScreen 抓到的屏幕外那半张就是垃圾像素。
                form.Refresh();
                Pump(4);

                GrabClient(form, Path.Combine(outDir, $"{pages[i]}.png"));

                File.WriteAllText(
                    Path.Combine(outDir, $"{pages[i]}-layout.txt"),
                    form.DumpLayout(i),
                    Encoding.UTF8);

                // 配置页比窗口高：不拉高窗口（高过屏幕后 CopyFromScreen 抓到的是垃圾像素），
                // 改成按视口高度分屏滚动补拍，保证整页都被真机像素覆盖过。
                if (i == 1)
                {
                    var viewport = Math.Max(1, form.ConfigViewportHeight);
                    var step = Math.Max(1, viewport - 60);   // 留一点重叠，避免正好切在卡片中间
                    var shot = 1;

                    for (var offset = step; offset < form.ConfigContentHeight && shot < 8; offset += step)
                    {
                        shot++;
                        form.ScrollConfigTo(offset);
                        Pump(5);
                        GrabClient(form, Path.Combine(outDir, $"02-配置-{shot}.png"));
                    }
                }
            }

            form.Hide();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>抓取窗口客户区在屏幕上的真实像素。</summary>
    private static void GrabClient(Form form, string path)
    {
        var origin = form.PointToScreen(Point.Empty);
        var size = form.ClientSize;

        using var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height));
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(origin, Point.Empty, size);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    /// <summary>跑自检并落报告；返回进程退出码（0 = 全部通过）。</summary>
    private static int RunSelfTest(MainForm probe, string dataDir)
    {
        var (ok, lines) = probe.SelfTest();

        var report = new StringBuilder()
            .AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"结果: {(ok ? "通过" : "失败")}")
            .AppendLine($"主机: {Environment.MachineName} / {Environment.OSVersion}")
            .AppendLine($"运行时: {Environment.Version}")
            .AppendLine($"数据目录: {dataDir}")
            .AppendLine(new string('-', 48))
            .AppendLine(string.Join(Environment.NewLine, lines))
            .ToString();

        try
        {
            // 数据目录可能还不存在（拿着全新 --data-dir 跑第一次自检时就是这种情况）。
            // 少了这一句，File.WriteAllText 会抛 DirectoryNotFoundException 被下面的
            // catch 吞掉 —— 表现是「退出码 0 但数据目录里没有 selftest.log」，报障时
            // 最需要的那份现场报告恰好丢了。
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, "selftest.log"), report, Encoding.UTF8);
        }
        catch
        {
            // 报告写不进去就只能靠退出码判断了
        }

        return ok ? 0 : 1;
    }

    /// <summary>
    /// 数据目录：<c>--data-dir &lt;dir&gt;</c> / <c>--data-dir=&lt;dir&gt;</c> 优先，
    /// 否则用 <c>%APPDATA%\LynkSign</c>。
    /// </summary>
    private static string ResolveDataDir(string[] args)
    {
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LynkSign");

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            var inline = SplitInlineValue(arg, "data-dir");
            if (inline is not null) return Path.GetFullPath(inline);

            if (IsSwitch(arg, "data-dir")
                && i + 1 < args.Length
                && !args[i + 1].StartsWith('-'))
            {
                return Path.GetFullPath(args[++i]);
            }
        }

        return fallback;
    }

    /// <summary>取一个带值的开关参数，形如 <c>--name &lt;value&gt;</c> 或 <c>--name=&lt;value&gt;</c>；没给返回 null。</summary>
    private static string? ResolveValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var inline = SplitInlineValue(args[i], name);
            if (inline is not null) return inline;

            if (IsSwitch(args[i], name)
                && i + 1 < args.Length
                && !args[i + 1].StartsWith('-'))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>匹配 <c>--name</c> / <c>-name</c> / <c>/name</c>（大小写不敏感）。</summary>
    private static bool IsSwitch(string arg, string name)
        => arg.Equals($"--{name}", StringComparison.OrdinalIgnoreCase)
        || arg.Equals($"-{name}", StringComparison.OrdinalIgnoreCase)
        || arg.Equals($"/{name}", StringComparison.OrdinalIgnoreCase);

    private static bool HasSwitch(string[] args, string name) => args.Any(a => IsSwitch(a, name));

    /// <summary>
    /// 是否出现了 <c>--name=value</c> 内联形态。
    /// <see cref="HasSwitch"/> 只认独立的开关（<c>--name</c>），两者要一起判断才不漏。
    /// </summary>
    private static bool HasInlineValue(string[] args, string name)
        => args.Any(a => SplitInlineValue(a, name) is not null);

    /// <summary>解析 <c>--name=value</c> 形态，返回 null 表示不是这个形态。</summary>
    private static string? SplitInlineValue(string arg, string name)
    {
        var prefix = $"--{name}=";
        if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var value = arg[prefix.Length..].Trim().Trim('"');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
