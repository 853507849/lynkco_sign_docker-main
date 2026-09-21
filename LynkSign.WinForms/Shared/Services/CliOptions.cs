using LynkSign.Configuration;
using LynkSign.Infrastructure;
using LynkSign.Push;

namespace LynkSign.Services;

/// <summary>从命令行参数解析出的可选项（等价 Python 的 argparse 结果）。</summary>
public sealed class CliOptions
{
    public string? Token { get; private set; }
    public string? DeviceId { get; private set; }
    public string? TokenB { get; private set; }
    public string? ContentId { get; private set; }

    /// <summary>原生通道 app key（一般不用传，默认即抓包实测值）。</summary>
    public string? CaNativeKey { get; private set; }

    /// <summary>原生通道 app secret —— 签到写入接口的前置条件。</summary>
    public string? CaNativeSecret { get; private set; }
    public bool AutoShare { get; private set; }

    /// <summary>
    /// 签到后自动打开分享链接的策略（<c>off</c> / <c>manual</c> / <c>always</c>）。
    /// 为 null 表示命令行没提这回事，交回环境变量 / config.json 决定。
    /// </summary>
    public string? OpenShareUrl { get; private set; }

    /// <summary>
    /// 分享文章 id 的来源（<c>auto</c> / <c>fixed</c>）。
    /// 同样为 null 表示命令行没提，交回环境变量 / config.json 决定。
    /// </summary>
    public string? ShareContentMode { get; private set; }
    public bool NoCache { get; private set; }
    public bool ClearCache { get; private set; }
    public bool Quiet { get; private set; }

    // 历史：这里曾有 Host / Port / NoBrowser / NoScheduler 四个属性，
    // 服务于 Web 配置中心的 Kestrel 监听（--host / --port / --no-browser / --no-scheduler）。
    // Web 版删除后它们既没人读、也没人写，属于纯粹的迷惑项，已连同解析分支一起移除。

    /// <summary>数据目录（config.json / logs 的父目录），对应 <c>LYNK_DATA_DIR</c>。</summary>
    public string? DataDir { get; private set; }

    /// <summary>只跑一次签到然后退出（等价 <c>python ql_lynk.py</c>）。</summary>
    public bool RunOnce { get; private set; }

    /// <summary>注册开机自启后退出（Windows）。</summary>
    public bool InstallAutostart { get; private set; }

    /// <summary>取消开机自启后退出（Windows）。</summary>
    public bool UninstallAutostart { get; private set; }

    public bool Help { get; private set; }

    /// <summary>解析命令行参数，兼容 <c>--key value</c> 与 <c>--key=value</c> 两种写法。</summary>
    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;

            string name;
            string? inlineValue = null;

            var eq = raw.IndexOf('=');
            if (raw.StartsWith("--", StringComparison.Ordinal) && eq > 0)
            {
                name = raw[..eq];
                inlineValue = raw[(eq + 1)..];
            }
            else
            {
                name = raw;
            }

            string? TakeValue()
            {
                if (inlineValue is not null) return inlineValue;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    return args[++i];
                }
                return null;
            }

            switch (name.ToLowerInvariant())
            {
                case "--token": options.Token = TakeValue(); break;
                case "--device-id": options.DeviceId = TakeValue(); break;
                case "--token-b": options.TokenB = TakeValue(); break;
                case "--content-id": options.ContentId = TakeValue(); break;
                case "--ca-native-key": options.CaNativeKey = TakeValue(); break;
                case "--ca-native-secret": options.CaNativeSecret = TakeValue(); break;
                case "--auto-share": options.AutoShare = true; break;
                case "--open-share-url":
                {
                    // 不带值时按 manual 处理：`--open-share-url` 这种写法，
                    // 用户想表达的就是「打开」，而「连定时任务也弹窗」是更重的副作用，
                    // 需要显式写 --open-share-url=always 才生效。
                    var value = TakeValue();
                    options.OpenShareUrl = string.IsNullOrWhiteSpace(value)
                        ? ShareUrlOpenModes.ManualValue
                        : value.Trim();
                    break;
                }
                case "--no-open-share-url":
                    options.OpenShareUrl = ShareUrlOpenModes.OffValue;
                    break;
                case "--share-content-mode":
                {
                    // 不带值时按 auto 处理：`--share-content-mode` 这种写法，
                    // 用户想表达的就是「动态取最新文章」，这也是该字段的默认档。
                    var value = TakeValue();
                    options.ShareContentMode = string.IsNullOrWhiteSpace(value)
                        ? ShareContentModes.AutoValue
                        : value.Trim();
                    break;
                }
                case "--no-cache": options.NoCache = true; break;
                case "--clear-cache": options.ClearCache = true; break;
                case "--quiet": options.Quiet = true; break;
                case "--data-dir":
                {
                    var dir = TakeValue();
                    if (!string.IsNullOrWhiteSpace(dir)) options.DataDir = dir;
                    break;
                }
                case "--run-once":
                case "--once": options.RunOnce = true; break;
                case "--install-autostart": options.InstallAutostart = true; break;
                case "--uninstall-autostart": options.UninstallAutostart = true; break;
                case "--help":
                case "-h": options.Help = true; break;
                default: break;   // 未知参数忽略, 与 Python 版 --urls 之类的兼容留白
            }
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            领克 APP 自动签到 · Windows 桌面版 (.NET 10 / WinForms)

            用法:
              LynkSignGUI                            启动桌面窗口 (托盘常驻 + cron 调度)
              LynkSignGUI --minimized                启动后直接缩到托盘 (开机自启用的就是它)
              LynkSignGUI --run-once                 只跑一次签到然后退出 (等价 python ql_lynk.py)
              LynkSignGUI --install-autostart        设置开机自启 (Windows, 写 HKCU Run)
              LynkSignGUI --uninstall-autostart      取消开机自启 (Windows)

            通用参数:
              --data-dir <dir>     数据目录 (config.json / logs / 令牌缓存)
                                   默认 %APPDATA%\LynkSign

            签到参数 (仅对 --run-once 生效):
              --token <v>          主账号 refreshToken (覆盖环境变量)
              --device-id <v>      设备 ID (覆盖环境变量)
              --token-b <v>        B 账号 refreshToken 列表, 逗号分隔
              --content-id <v>     分享文章 ID (fixed 档用; auto 档拉不到时的兜底)
              --share-content-mode [v] 分享文章 ID 来源
                                   v = auto(默认, 拉广场最新一篇) | fixed(固定用 --content-id)
              --ca-native-key <v>  原生通道 app key (默认 203760416)
              --ca-native-secret <v> 原生通道 app secret (签到写入必需)
              --auto-share         启用自动分享任务
              --open-share-url [v] 签到后自动用浏览器打开分享链接
                                   v = manual(默认, 仅手动触发) | always(手动+定时) | off
              --no-open-share-url  本次不自动打开分享链接 (覆盖 config.json)
              --no-cache           忽略 accessToken 缓存, 强制 refresh
              --clear-cache        清空 accessToken 缓存后退出
              --quiet              静默模式, 不打印原始响应
              -h, --help           显示本帮助

            环境变量:
              LYNK_DATA_DIR        数据目录 (优先级高于运行目录)
              LYNK_CONFIG_FILE     config.json 路径 (默认 数据目录/config.json)
              LYNK_LOGS_DIR        日志目录 (默认 数据目录/logs)
              LYNK_CACHE_FILE      accessToken 缓存路径
              LYNK_CA_NATIVE_KEY   原生通道 app key (默认 203760416)
              LYNK_CA_NATIVE_SECRET 原生通道 app secret (签到写入必需)
              LYNK_OPEN_SHARE_URL  签到后自动打开分享链接: off / manual / always
              LYNK_SHARE_CONTENT_MODE 分享文章 ID 来源: auto / fixed
              LYNK_SHARE_CONTENT_ID   分享文章 ID (auto 档拉不到时的兜底)
              TZ                   触发时区 (默认 Asia/Shanghai)

            诊断与辅助:
              --selftest               跑一遍链路自检, 报告写进数据目录 selftest.log
              --verify-native-secret   用假 token 探测原生通道密钥是否正确
                                       (不会真的签到; 报告写进 verify-native-secret.log)
              --fetch-article          实拉一次探索广场文章流, 报告取到的最新一篇文章
                                       (不签到; 报告写进 fetch-article.log)
              --refresh-status         只拉一次最新签到状态 (连续天数 / 补签卡 / 账户信息)
                                       (不签到、不推送; 与每天 00:01 的自动刷新同一个任务;
                                        日志写进 logs\run-*-status.log)
              --import-har <file>      从抓包文件 (.har) 里认出短信登录那一条,
                                       把 refreshToken 与 deviceId 一起写回 config.json
                                       (报告写进 import-har.log)
              --screenshot <dir>       把界面离屏渲染成 PNG
              --screenshot-live <dir>  真开窗口抓屏存 PNG
            """);
    }
}

/// <summary>
/// 按 <c>命令行参数 &gt; 环境变量 (LYNK_*) &gt; config.json &gt; 默认值</c> 解析出一次任务的最终配置。
/// </summary>
public static class SettingsResolver
{
    /// <summary>配置文件里的占位符，视同未配置。</summary>
    public const string PlaceholderToken = "bearer<your-refresh-token>";

    public static SignJobOptions Resolve(CliOptions? cli, AppConfig? config)
    {
        var cfg = config ?? AppConfig.CreateDefault();

        // B 账号：命令行 > 环境变量 > config.json（数组或逗号串）
        var tokenBRaw = FirstNonEmpty(cli?.TokenB, Environment.GetEnvironmentVariable("LYNK_TOKEN_B"));
        var tokenB = tokenBRaw is not null
            ? SplitList(tokenBRaw)
            : (cfg.TokenB ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();

        return new SignJobOptions
        {
            RefreshToken = FirstNonEmpty(cli?.Token, Environment.GetEnvironmentVariable("LYNK_REFRESH_TOKEN"), cfg.RefreshToken) ?? "",
            DeviceId = FirstNonEmpty(cli?.DeviceId, Environment.GetEnvironmentVariable("LYNK_DEVICE_ID"), cfg.DeviceId) ?? "",
            TokenB = tokenB,
            ShareContentId = FirstNonEmpty(cli?.ContentId, Environment.GetEnvironmentVariable("LYNK_SHARE_CONTENT_ID"), cfg.ShareContentId)
                             ?? AppConfig.DefaultShareContentId,
            AutoShare = (cli?.AutoShare ?? false) || EnvFlag("LYNK_AUTO_SHARE") || cfg.AutoShare,

            // 分享链接自动打开：命令行 > 环境变量 > config.json。
            // 任何一层留空 / 写错都落到 Off（最保守的一档），详见 ShareUrlOpenModes.Parse。
            OpenShareUrl = ShareUrlOpenModes.Parse(FirstNonEmpty(
                cli?.OpenShareUrl,
                Environment.GetEnvironmentVariable(ShareUrlOpenModes.EnvVar),
                cfg.OpenShareUrl)),

            // 分享文章 id 的来源：命令行 > 环境变量 > config.json。
            // 留空 / 写错都落到 Auto（拉不到会回退到 ShareContentId），详见 ShareContentModes.Parse。
            ShareContentMode = ShareContentModes.Parse(FirstNonEmpty(
                cli?.ShareContentMode,
                Environment.GetEnvironmentVariable(ShareContentModes.EnvVar),
                cfg.ShareContentMode)),

            Quiet = cli?.Quiet ?? false,
            NoCache = cli?.NoCache ?? false,
            Push = PushChannels.Resolve(cfg),

            // 原生通道密钥：命令行 > 环境变量 > config.json。
            // key 留空也没关系 —— SignProfile.Native 会回落到抓包实测的默认值。
            CaNativeKey = FirstNonEmpty(
                cli?.CaNativeKey,
                Environment.GetEnvironmentVariable(LynkSign.Lynk.LynkConstants.NativeKeyEnvVar),
                cfg.CaNativeKey) ?? "",
            CaNativeSecret = FirstNonEmpty(
                cli?.CaNativeSecret,
                Environment.GetEnvironmentVariable(LynkSign.Lynk.LynkConstants.NativeSecretEnvVar),
                cfg.CaNativeSecret) ?? "",
        };
    }

    /// <summary>web 侧（定时 / 手动触发）没有命令行参数，直接用 config.json + 环境变量。</summary>
    public static SignJobOptions ResolveFromConfig(AppConfig config) => Resolve(null, config);

    private static bool EnvFlag(string name)
        => Environment.GetEnvironmentVariable(name)?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    private static List<string> SplitList(string raw)
        => [.. raw.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }
}
