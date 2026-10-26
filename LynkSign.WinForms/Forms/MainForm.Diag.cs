using System.Text;
using LynkSign.WinForms.Ui;

namespace LynkSign.WinForms.Forms;

/// <summary>
/// MainForm 的离线诊断部分（只在 <c>--screenshot</c> / <c>--selftest</c> 这类开发开关下走到）。
///
/// <para>
/// 界面是自绘的，肉眼看图是最终标准，但"每改一处就跑起来截图再用眼睛比对"太慢，
/// 而且自动化环境里根本没眼睛。所以这里把布局结果导成一份纯文本清单：
/// 每个控件的实际尺寸 vs 它自己算出来的首选尺寸，差值就是被裁掉的部分。
/// </para>
///
/// <para>两条判据，足以抓出绝大多数自绘布局 bug：</para>
/// <list type="number">
///   <item><b>AutoSize 的控件，实际高度必须 ≥ 首选高度</b> —— 矮了就是父容器没给够，
///         底部内容会被压掉（典型场景：卡片自己算的高度里漏算了标题带）。</item>
///   <item><b>不会自动省略号的文字控件，实际宽度必须 ≥ 首选宽度</b> ——
///         窄了文字会被硬生生切掉半截（"Telegram Bot Token" 只剩 "Telegram B"）。
///         本身带 AutoEllipsis 的控件会退化成 "+…"，只提示不报错。</item>
/// </list>
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>导出指定页签的控件树与布局体检结果。</summary>
    /// <param name="tabIndex">0 = 运行状态，1 = 配置。</param>
    internal string DumpLayout(int tabIndex)
    {
        var page = tabIndex == 0 ? _tabStatus : _tabConfig;
        var sb = new StringBuilder();

        sb.AppendLine($"页签: {(tabIndex == 0 ? "运行状态" : "配置")}");
        sb.AppendLine($"页面可视区: {page.ClientSize.Width}x{page.ClientSize.Height}"
            + (page is ScrollableControl sc ? $", AutoScroll={sc.AutoScroll}, 内容区高={sc.DisplayRectangle.Height}" : ""));
        sb.AppendLine($"整页内容高度: {ConfigContentHeight}");
        sb.AppendLine(new string('-', 118));
        sb.AppendLine($"{"控件".PadRight(34)}{"实际".PadRight(15)}{"首选".PadRight(15)}{"结论".PadRight(26)}文字");
        sb.AppendLine(new string('-', 118));

        var heights = 0;
        var widths = 0;
        var overflows = 0;
        Walk(page, 1, sb, ref heights, ref widths, ref overflows);

        sb.AppendLine(new string('-', 118));
        sb.AppendLine(heights == 0
            ? "高度: 无裁剪（AutoSize 控件都拿到了 >= 首选高度）"
            : $"高度: {heights} 处不足，底部内容会被裁掉");
        sb.AppendLine(widths == 0
            ? "宽度: 无截断（文字控件都拿到了 >= 首选宽度）"
            : $"宽度: {widths} 处文字被切，需要加宽或开 AutoEllipsis");
        sb.AppendLine(overflows == 0
            ? "越界: 无（没有子控件超出父容器可视区）"
            : $"越界: {overflows} 处子控件比父容器还宽/高，会被切边");

        return sb.ToString();
    }

    private static void Walk(Control parent, int depth, StringBuilder sb, ref int heights, ref int widths, ref int overflows)
    {
        foreach (Control child in parent.Controls)
        {
            var preferred = child.PreferredSize;

            var notes = new List<string>();

            // 不可见的控件不参与体检。
            // 配置页的「怎么弄？」折叠起来时，那段说明 Label 仍然挂在控件树上、
            // 但高度被 TableLayoutPanel 排成 0 —— 这是设计如此，不是裁剪。
            // 不排除掉的话，每次折叠都会稳定产出几条「矮 Npx」的假警报，
            // 真出问题时反而要在一堆噪音里找。
            if (!child.Visible)
            {
                sb.AppendLine(
                    Pad($"{new string(' ', depth * 2)}{child.GetType().Name}", 34)
                    + Pad($"{child.Width}x{child.Height}", 15)
                    + Pad($"{preferred.Width}x{preferred.Height}", 15)
                    + Pad("隐藏", 26)
                    + TextOf(child));

                Walk(child, depth + 1, sb, ref heights, ref widths, ref overflows);
                continue;
            }

            // 判据 1：自适应高度没给够
            if (child.AutoSize && preferred.Height - child.Height > 1)
            {
                heights++;
                notes.Add($"矮 {preferred.Height - child.Height}px");
            }

            // 判据 2：文字宽度不够。只查真正显示文字、又不会自己加省略号的控件；
            // 多行/自动换行的控件宽度不够会换行，不算截断。
            if (ShowsText(child) && !child.AutoSize && !WrapsOrEllipsizes(child))
            {
                var spare = child.ClientSize.Width - preferred.Width;
                if (spare < -1)
                {
                    widths++;
                    notes.Add($"窄 {-spare}px");
                }
            }

            // 判据 3：子控件比父容器还宽 —— 自绘控件不会被父容器自动裁剪，
            // 会直接画到卡片外面去（输入框戳出圆角边框就是这么来的）。
            if (child.Width > parent.ClientSize.Width + 1)
            {
                overflows++;
                notes.Add($"超父宽 {child.Width - parent.ClientSize.Width}px");
            }

            var head = $"{new string(' ', depth * 2)}{child.GetType().Name}";
            var text = TextOf(child);

            sb.AppendLine(
                Pad(head, 34)
                + Pad($"{child.Width}x{child.Height}", 15)
                + Pad($"{preferred.Width}x{preferred.Height}", 15)
                + Pad(notes.Count == 0 ? "ok" : string.Join(", ", notes), 26)
                + text);

            Walk(child, depth + 1, sb, ref heights, ref widths, ref overflows);
        }
    }

    /// <summary>按显示列宽补齐（中文字符占两列，否则表格会歪）。</summary>
    private static string Pad(string value, int columns)
    {
        var width = value.Sum(ch => ch > 0x2E7F ? 2 : 1);
        if (width >= columns) return value + " ";

        // 超宽的不截断（截断了就读不出名字），只补一个空格分隔
        return value + new string(' ', columns - width);
    }

    private static bool ShowsText(Control c)
        => c is Label or Button or FlatCheckBox or StatusPill or MetricCard;

    /// <summary>宽度不够时能优雅降级（换行或省略号）的控件，不算截断。</summary>
    private static bool WrapsOrEllipsizes(Control c) => c switch
    {
        // LinkLabel 也是 Label，一并被这条覆盖
        Label { AutoEllipsis: true } => true,
        FlatCheckBox => true,   // 勾选框文字超宽自己换行
        MetricCard => true,     // 大数字卡按点定位自绘，不参与矩形约束
        StatusPill => true,     // 胶囊按文字自适应宽度
        FlatButton => true,     // 按钮按文字算首选宽度，外部只会给更大
        _ => c.Text.Contains('\n'),
    };

    private static string TextOf(Control c)
    {
        var text = c switch
        {
            CardPanel card => card.Title,
            MetricCard metric => metric.Caption,
            _ => c.Text,
        };

        if (string.IsNullOrWhiteSpace(text)) return "";
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length > 22 ? text[..22] + "…" : text;
    }

    /// <summary>整页内容需要多高（配置页截图时按它拉窗口）。</summary>
    internal int ConfigContentHeight => _configCanvas.PreferredSize.Height;

    /// <summary>配置页当前的可视高度（分屏补拍时按它算步长）。</summary>
    internal int ConfigViewportHeight => _tabConfig.ClientSize.Height;

    /// <summary>
    /// 把配置页滚动到指定偏移（传内容坐标，内部会转成 AutoScrollPosition 需要的值）。
    ///
    /// <para>
    /// <c>--screenshot-live</c> 靠它分屏拍完整页。
    /// 之所以不把窗口直接拉高：窗口一旦高过屏幕，<c>CopyFromScreen</c> 抓到的
    /// 屏幕外那半张就是垃圾像素，白白多出一堆误判。
    /// </para>
    /// </summary>
    internal void ScrollConfigTo(int offset)
    {
        _tabConfig.AutoScrollPosition = new Point(0, Math.Max(0, offset));
    }
}
