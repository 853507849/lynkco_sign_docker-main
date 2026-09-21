using System.Diagnostics;

namespace LynkSign.Infrastructure;

/// <summary>
/// 用系统默认浏览器打开一个 URL。
///
/// <para>
/// 失败一律返回 <c>false</c> + 一句原因，<b>不抛异常</b>：
/// 调用点在签到主流程里，而「打不开浏览器」这件事（无桌面环境、Docker 容器里、
/// 装了但没设默认浏览器）绝不该把已经成功的签到变成一次失败任务。
/// </para>
///
/// <para>
/// 用 <c>UseShellExecute = true</c> 交给 shell 去解析：这样
/// <c>https://…</c> 会走用户设置的默认浏览器（而不是某个写死的 exe），
/// 与 <c>--screenshot</c> 之外那些「打开目录」的动作同源。
/// </para>
/// </summary>
public static class BrowserLauncher
{
    /// <summary>尝试打开 URL。返回是否已交给系统处理。</summary>
    public static bool TryOpen(string? url, out string error)
    {
        error = "";

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "链接为空";
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }

            if (OperatingSystem.IsLinux())
            {
                // Linux 没有统一的 shell 关联，只能按惯例调 xdg-open。
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
                return true;
            }

            error = "当前系统没有可用的浏览器启动方式";
            return false;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
