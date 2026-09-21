using System.Text;
using LynkSign.Infrastructure;

namespace LynkSign.WinForms.App;

/// <summary>
/// 兜底异常处理：先把堆栈落到数据目录的 <c>crash.log</c>，再弹一个提示框。
///
/// <para>
/// 桌面版没有控制台，未捕获异常如果只弹框，用户报障时拿不到堆栈；
/// 只写文件又没人知道出事了。所以两件事都做。
/// </para>
/// </summary>
internal static class CrashReport
{
    public static void Show(Exception? ex, string source)
    {
        if (ex is null) return;

        var path = Write(ex, source);

        try
        {
            var text = $"""
                {source} 发生未处理异常：

                {ex.GetType().Name}: {ex.Message}

                完整堆栈已写入：
                {path ?? "(写入失败)"}

                程序会继续运行，但该次操作可能没有完成。
                """;

            MessageBox.Show(text, $"{Program.AppTitle} - 出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // 连提示框都弹不出来（比如已经在关闭过程中），就只能算了
        }
    }

    private static string? Write(Exception ex, string source)
    {
        try
        {
            var dir = AppPaths.WorkDir;
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "crash.log");
            var block = new StringBuilder()
                .AppendLine(new string('=', 60))
                .AppendLine($"[{TimeUtil.Stamp()}] {source}")
                .AppendLine(ex.ToString())
                .AppendLine()
                .ToString();

            File.AppendAllText(path, block, Encoding.UTF8);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
