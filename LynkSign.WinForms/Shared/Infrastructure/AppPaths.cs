namespace LynkSign.Infrastructure;

/// <summary>
/// 路径解析，对应 Python 版的 <c>BASE_DIR</c> / <c>CONFIG_FILE</c> / <c>LOGS_DIR</c>。
///
/// <para>
/// <b>数据目录</b>（WorkDir）= <c>config.json</c> / <c>logs</c> / token 缓存 的落盘位置。
/// 取值优先级：<c>LYNK_DATA_DIR</c> &gt; 进程当前工作目录。
/// 桌面版由入口在启动最早期把 <c>LYNK_DATA_DIR</c> 指到 <c>%APPDATA%\LynkSign</c>，
/// 这样程序目录可以只读、数据也不会随卸载一起丢掉；
/// 从源码 <c>dotnet run</c> 时没设过，就落在项目目录。
/// </para>
///
/// <para>
/// 历史：这里曾有一个 <c>ContentRoot</c>（含 <c>wwwroot</c> 的站点目录），
/// 只为给 Web 配置中心版提供静态页。Web 版删除后没有任何调用方，已移除。
/// </para>
/// </summary>
public static class AppPaths
{
    /// <summary>数据目录（config.json / logs / 缓存都放这里）。</summary>
    public static string WorkDir { get; } = ResolveWorkDir();

    /// <summary>config.json 路径。</summary>
    public static string ConfigFile { get; } =
        NonEmpty(Environment.GetEnvironmentVariable("LYNK_CONFIG_FILE"))
        ?? Path.Combine(WorkDir, "config.json");

    /// <summary>运行日志目录。</summary>
    public static string LogsDir { get; } =
        NonEmpty(Environment.GetEnvironmentVariable("LYNK_LOGS_DIR"))
        ?? Path.Combine(WorkDir, "logs");

    /// <summary>accessToken 缓存文件路径（见 <c>TokenCache</c>）。为空表示按候选列表自动选择。</summary>
    public static string CacheFile { get; } =
        NonEmpty(Environment.GetEnvironmentVariable("LYNK_CACHE_FILE")) ?? string.Empty;

    /// <summary>
    /// 数据目录：<c>LYNK_DATA_DIR</c> 优先，否则用进程工作目录。
    /// 相对路径按工作目录展开，便于 <c>--data-dir .\data</c> 这种写法。
    /// </summary>
    private static string ResolveWorkDir()
    {
        var configured = NonEmpty(Environment.GetEnvironmentVariable("LYNK_DATA_DIR"));
        if (configured is null) return Directory.GetCurrentDirectory();

        try
        {
            return Path.GetFullPath(configured);
        }
        catch
        {
            // 路径非法（含非法字符等）时退回工作目录，不让程序起不来
            return Directory.GetCurrentDirectory();
        }
    }

    private static string? NonEmpty(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
