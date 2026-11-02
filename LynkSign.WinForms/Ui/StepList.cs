using System.Drawing.Drawing2D;

namespace LynkSign.WinForms.Ui;

/// <summary>
/// 步骤清单：每行一个圆点 + 标题，当前那一步额外显示一行说明。
///
/// <para>
/// 「签到进行中」页用它把一次运行拆成可读的五步。数据来自日志里的 <c>[n/5]</c> 行 ——
/// 签到流程本身已经在打这些行，界面只是照着点亮，不必给流程加回传通道。
/// </para>
///
/// <para>
/// 三种状态的视觉必须一眼可分：已完成是实心圆 + 白勾，进行中是空心环 + 内点 + 加粗标题，
/// 待执行是浅灰空心环 + 灰字。只靠颜色深浅区分的话，色弱用户与低对比屏幕上都读不出来。
/// </para>
/// </summary>
internal sealed class StepList : Control
{
    private const int DotSize = 18;
    private const int TextLeft = 34;
    // 行距 10：五行加起来 5×(字高+8)+4×10 ≈ 215，和设计稿里那一列五步的高度一致。
    // 早先是 16，五行撑到 260 出头，卡片下半截空得让人以为漏画了一步。
    private const int RowGap = 10;

    private string[] _titles = [];
    private string[] _details = [];
    private int _current;

    public StepList()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.CardBg;
    }

    /// <summary>当前进行到第几步（0 基）。等于 <c>titles.Length</c> 表示全部走完。</summary>
    public int CurrentIndex
    {
        get => _current;
        set
        {
            var v = Math.Max(0, value);
            if (_current == v) return;

            _current = v;
            Invalidate();
        }
    }

    public int StepCount => _titles.Length;

    public void SetSteps(string[] titles, string[] details)
    {
        _titles = titles;
        _details = details;
        Invalidate();
    }

    /// <summary>本控件需要的高度（按行数现算，供外层布局用）。</summary>
    public int PreferredListHeight
    {
        get
        {
            var h = 0;
            for (var i = 0; i < _titles.Length; i++) h += RowHeight(i) + (i < _titles.Length - 1 ? RowGap : 0);
            return h;
        }
    }

    private int RowHeight(int index)
    {
        // 说明槽位是按步数预分配的数组，没用到的位置是 null（不是空串），
        // 直接取 .Length 会在第一次绘制时抛 NullReference —— 而 OnPaint 里的异常
        // 会被 WinForms 吞掉并留下一块空白，界面上完全看不出是哪一步出的问题。
        var detail = index < _details.Length ? _details[index] : null;

        var extra = index == _current && !string.IsNullOrEmpty(detail)
            ? Theme.MonoSmall.Height + 6
            : 0;

        return Math.Max(DotSize + 4, Theme.Base.Height + 8) + extra;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        Theme.ClearBackdrop(g, this);

        g.SmoothingMode = SmoothingMode.AntiAlias;

        var y = 0;

        for (var i = 0; i < _titles.Length; i++)
        {
            var rowH = RowHeight(i);
            var done = i < _current;
            var active = i == _current;

            var cx = 2F;
            var cy = y + Math.Max(DotSize, Theme.Base.Height + 10) / 2F;
            var dot = new RectangleF(cx, cy - DotSize / 2F, DotSize, DotSize);

            if (done)
            {
                using var brush = new SolidBrush(Theme.Accent);
                g.FillEllipse(brush, dot);
                DrawCheck(g, dot, Color.White);
            }
            else if (active)
            {
                using var ring = new Pen(Theme.Accent, 2F);
                g.DrawEllipse(ring, RectangleF.Inflate(dot, -1F, -1F));

                using var core = new SolidBrush(Theme.Accent);
                g.FillEllipse(core, RectangleF.Inflate(dot, -6F, -6F));
            }
            else
            {
                using var ring = new Pen(Color.FromArgb(0xD8, 0xDF, 0xE9), 1.5F);
                g.DrawEllipse(ring, RectangleF.Inflate(dot, -0.5F, -0.5F));
            }

            var titleY = y + Math.Max(0, (Math.Max(DotSize, Theme.Base.Height + 10) - Theme.Base.Height) / 2);

            TextRenderer.DrawText(
                g,
                _titles[i],
                active ? Theme.BaseBold : Theme.Base,
                new Point(TextLeft, titleY),
                done ? Theme.TextBody : active ? Theme.TextStrong : Theme.TextFaint,
                TextFormatFlags.NoPadding);

            // 右侧状态
            var status = done ? "完成" : active ? "进行中 …" : "等待";

            TextRenderer.DrawText(
                g,
                status,
                Theme.Small,
                new Rectangle(TextLeft, y, Math.Max(0, Width - TextLeft), Theme.Base.Height + 10),
                active ? Theme.Accent : Theme.TextFaint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // 当前步的补充说明（接口路径之类），只有进行中的那一步才值得显示
            if (active && i < _details.Length && !string.IsNullOrEmpty(_details[i]))
            {
                TextRenderer.DrawText(
                    g,
                    _details[i],
                    Theme.MonoSmall,
                    new Point(TextLeft, titleY + Theme.Base.Height + 5),
                    Theme.TextMuted,
                    TextFormatFlags.NoPadding);
            }

            y += rowH;
            if (i < _titles.Length - 1)
            {
                // 步骤之间的连接线：已完成的部分用主色，剩下的用浅灰
                using var pen = new Pen(i < _current ? Theme.Accent : Color.FromArgb(0xE4, 0xE9, 0xF1), 1.5F);
                var lineX = cx + DotSize / 2F;
                g.DrawLine(pen, lineX, dot.Bottom + 2, lineX, dot.Bottom + RowGap - 2);
            }

            y += RowGap;
        }
    }

    private static void DrawCheck(Graphics g, RectangleF circle, Color color)
    {
        using var pen = new Pen(color, 1.9F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        var cx = circle.X + circle.Width / 2F;
        var cy = circle.Y + circle.Height / 2F;
        var s = circle.Width * 0.22F;

        g.DrawLines(pen,
        [
            new PointF(cx - s, cy),
            new PointF(cx - s * 0.2F, cy + s * 0.8F),
            new PointF(cx + s, cy - s * 0.7F),
        ]);
    }
}
