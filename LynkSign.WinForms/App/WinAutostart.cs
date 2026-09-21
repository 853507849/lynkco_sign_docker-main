using Microsoft.Win32;

namespace LynkSign.WinForms.App;

/// <summary>
/// 桌面版的开机自启开关。
///
/// <para>
/// 为什么没有直接复用原项目的 <c>LynkSign.Services.WindowsAutostart</c>：
/// 那一版是给「Web 配置中心」设计的，自启命令会优先指向同目录的 <c>start-hidden.vbs</c>
/// （隐藏控制台），否则退化成 `LynkSign.exe --no-browser`。
/// 桌面版本体就是窗口程序，需要的是「开机后静默缩到托盘」，
/// 所以这里把命令写成 `LynkSignGUI.exe --minimized`。
/// </para>
///
/// <para>
/// 写的是 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>：
/// 每用户生效、<b>不需要管理员权限</b>，也不污染 HKLM。
/// 值名与 Web 版（<c>LynkSign</c>）刻意错开，两个版本可以同时设置、互不覆盖。
/// </para>
/// </summary>
internal static class WinAutostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LynkSignGUI";

    /// <summary>开机自启要附加的命令行开关（进托盘、不弹主窗口）。</summary>
    public const string MinimizedSwitch = "--minimized";

    /// <summary>已注册的自启命令；未设置时返回 null。</summary>
    public static string? Current()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>当前是否已注册自启。</summary>
    public static bool IsEnabled => !string.IsNullOrWhiteSpace(Current());

    /// <summary>注册开机自启。返回 (是否成功, 说明)。</summary>
    public static (bool Ok, string Info) Install()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return (false, "取不到当前程序路径");

            var command = $"\"{exe}\" {MinimizedSwitch}";

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return (false, $@"打不开注册表键 HKCU\{RunKeyPath}");

            key.SetValue(ValueName, command, RegistryValueKind.String);
            return (true, command);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>取消开机自启。返回 (是否成功, 说明)。</summary>
    public static (bool Ok, string Info) Uninstall()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return (false, $@"打不开注册表键 HKCU\{RunKeyPath}");

            if (key.GetValue(ValueName) is null) return (true, "本来就没有设置开机自启");

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return (true, $@"已移除 HKCU\{RunKeyPath}\{ValueName}");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
