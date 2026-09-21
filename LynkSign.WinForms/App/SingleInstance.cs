using System.Runtime.InteropServices;

namespace LynkSign.WinForms.App;

/// <summary>
/// 单实例：重复双击图标时，不再起第二个进程，而是把已有窗口拉到前台。
///
/// <para>
/// 互斥体用 <c>Global\</c> 前缀 —— 托盘程序最怕的就是用户以为「没启动」而反复双击，
/// 结果攒出一堆进程同时跑签到。
/// </para>
/// </summary>
internal static class SingleInstance
{
    private const int SwRestore = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>找到标题为 <paramref name="windowTitle"/> 的已有窗口并激活；找不到则返回 false。</summary>
    public static bool ActivateExisting(string windowTitle)
    {
        try
        {
            var hwnd = FindWindowW(null, windowTitle);
            if (hwnd == IntPtr.Zero) return false;

            ShowWindow(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
