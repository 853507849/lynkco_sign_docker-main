using System.Drawing.Drawing2D;

namespace LynkSign.WinForms.Ui;

/// <summary>
/// 月历：周一开头、7 列 6 行，每格一个日期数字 + 一个状态点。
///
/// <para>
/// 不用 <c>MonthCalendar</c>：原生月历是系统控件，配色、字号、圆角全都改不了，
/// 放进卡片里就是一块"Windows 自带的灰白方块"。
/// </para>
///
/// <para>
/// 状态只用圆点表达：实心主色 = 已签到，橙色 = 补签，空心浅灰 = 无记录。
/// 整格染色会盖住日期数字，而行尾的总结文字已经说了「共几天、签了几天」，
/// 格子里再满色反而看不清是哪一天。
/// </para>
/// </summary>
internal sealed class MonthGrid : Control
{
    private const int WeekHeaderHeight = 28;
    private const int DotSize = 4;

    private static readonly string[] Weekdays = ["一", "二", "三", "四", "五", "六", "日"];

    private DateOnly _month = DateOnly.FromDateTime(DateTime.Today);
    private HashSet<DateOnly> _signed = [];
    private HashSet<DateOnly> _makeup = [];

    public MonthGrid()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.CardBg;
        Height = 380;
    }

    /// <summary>显示哪个月（取该月 1 号即可）。</summary>
    public DateOnly Month
    {
        get => _month;
        set
        {
            var v = new DateOnly(value.Year, value.Month, 1);
            if (_month == v) return;

            _month = v;
            Invalidate();
        }
    }

    /// <summary>已签到日期集合。</summary>
    public void SetMarks(IEnumerable<DateOnly> signed, IEnumerable<DateOnly>? makeup = null)
    {
        _signed = [.. signed];
        _makeup = makeup is null ? [] : [.. makeup];
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        Theme.ClearBackdrop(g, this);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var cellWidth = Width / 7F;

        // ---- 星期表头 ----
        for (var i = 0; i < 7; i++)
        {
            TextRenderer.DrawText(
                g,
                Weekdays[i],
                Theme.Small,
                new Rectangle((int)(cellWidth * i), 0, (int)cellWidth, WeekHeaderHeight),
                Theme.TextFaint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // ---- 日期格 ----
        var first = _month;
        var offset = ((int)first.DayOfWeek + 6) % 7;   // 周一 = 0
        var days = DateTime.DaysInMonth(first.Year, first.Month);

        // 行数按这个月真正占的周数算（5 或 6），不固定 6：
        // 11 月只有 5 行，硬留第 6 行就是白白多出一整行空白，
        // 而表格看起来"下面空一大块"，十有八九是这个原因，不是卡片太高。
        var rows = (offset + days + 6) / 7;

        // 行高封顶 84 且整块垂直居中：直接按可用高度均分会把行高拉到 90+，
        // 每格只剩一个数字飘在中间；不封顶又会让格子全挤在上半截。
        var avail = Height - WeekHeaderHeight;
        var cellHeight = Math.Clamp(avail / rows, 28F, 84F);
        var gridTop = WeekHeaderHeight + Math.Max(0F, (avail - cellHeight * rows) / 2F);
        var today = DateOnly.FromDateTime(DateTime.Today);

        for (var day = 1; day <= days; day++)
        {
            var index = offset + day - 1;
            var col = index % 7;
            var row = index / 7;

            var cell = new RectangleF(
                cellWidth * col,
                gridTop + cellHeight * row,
                cellWidth,
                cellHeight);

            var date = new DateOnly(first.Year, first.Month, day);
            var isToday = date == today;
            var signed = _signed.Contains(date);
            var makeup = _makeup.Contains(date);
            var future = date > today;

            // 今天的格子单独点亮：一层极淡的主色底 + 主色数字
            if (isToday)
            {
                var badge = new RectangleF(cell.X + cell.Width / 2F - 15F, cell.Y + 1F, 30F, 30F);
                Theme.FillRound(g, badge, 8F, Theme.AccentSoft);
            }

            var color = future
                ? Color.FromArgb(0xC8, 0xCF, 0xD8)
                : isToday
                    ? Theme.Accent
                    : signed
                        ? Theme.TextStrong
                        : Theme.TextMuted;

            TextRenderer.DrawText(
                g,
                day.ToString(),
                isToday ? Theme.BaseBold : Theme.Base,
                new Rectangle((int)cell.X, (int)cell.Y + 2, (int)cell.Width, 26),
                color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // ---- 状态点 ----
            var dotY = cell.Y + 2 + 26 + 2;
            var dotX = cell.X + cell.Width / 2F - DotSize / 2F;

            if (makeup)
            {
                using var brush = new SolidBrush(Color.FromArgb(0xE8, 0xAA, 0x45));
                g.FillEllipse(brush, dotX, dotY, DotSize + 1, DotSize + 1);
            }
            else if (signed)
            {
                using var brush = new SolidBrush(Theme.AccentLight);
                g.FillEllipse(brush, dotX, dotY, DotSize + 1, DotSize + 1);
            }
            else if (!future)
            {
                using var pen = new Pen(Color.FromArgb(0xE2, 0xE8, 0xF0), 1F);
                g.DrawEllipse(pen, dotX, dotY + 0.5F, DotSize, DotSize);
            }
        }
    }
}
