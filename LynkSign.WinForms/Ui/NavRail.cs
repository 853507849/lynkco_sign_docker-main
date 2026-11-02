using System.Drawing.Drawing2D;

namespace LynkSign.WinForms.Ui;

/// <summary>导航栏里可用的图标（全部几何自绘，不依赖图标字体）。</summary>
internal enum NavIcon
{
    Dashboard,
    Check,
    Calendar,
    Settings,
}

/// <summary>
/// 左侧导航栏：一块深色竖向栏，顶部产品标、中间若干图标项、底部设置。
///
/// <para>
/// 用它替换原来的顶部文字页签。图标项比文字页签更省横向空间，也把「当前在哪一页」
/// 表达得更重 —— 选中项是一块实色圆角块，而不是一条 2px 的下划线。
/// </para>
///
/// <para>
/// 悬停提示（ToolTip）是必需的：只有图标的导航如果不给文字，第一次用的人得挨个点一遍。
/// </para>
///
/// <para>这里只管画与选中，页面切换由调用方监听 <see cref="SelectedIndexChanged"/>。</para>
/// </summary>
internal sealed class NavRail : Control
{
    public const int RailWidth = 76;

    private const int ItemSize = 44;
    private const int ItemGap = 8;
    private const int TopInset = 74;
    private const int LogoSize = 40;

    private readonly List<(NavIcon Icon, string Tip)> _items = [];
    private readonly List<Rectangle> _bounds = [];
    private readonly ToolTip _tip = new() { InitialDelay = 380, ReshowDelay = 120 };

    private int _selected;
    private int _hover = -1;

    public NavRail()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.RailBg;
        Width = RailWidth;
        Dock = DockStyle.Left;
        Cursor = Cursors.Hand;
    }

    public event EventHandler? SelectedIndexChanged;

    public IReadOnlyList<string> Items => _items.Select(i => i.Tip).ToList();

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            var v = Math.Clamp(value, 0, Math.Max(0, _items.Count - 1));
            if (_selected == v) return;

            _selected = v;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void AddItem(NavIcon icon, string tip)
    {
        _items.Add((icon, tip));
        _tip.SetToolTip(this, "");
        Rebuild();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Rebuild();
    }

    /// <summary>
    /// 重新算每个图标项的矩形。
    ///
    /// <para>
    /// 竖向排列、水平居中；<b>设置项单独贴在底部</b>（最后一项），与上面几项之间留空 ——
    /// 设计稿就是这么分的，"页面导航"和"设置"不是同一类入口。
    /// </para>
    /// </summary>
    private void Rebuild()
    {
        _bounds.Clear();

        var x = (Width - ItemSize) / 2;
        var y = TopInset;

        for (var i = 0; i < _items.Count; i++)
        {
            var isLast = i == _items.Count - 1 && _items[i].Icon == NavIcon.Settings;
            var top = isLast ? Math.Max(y, Height - ItemSize - 20) : y;

            _bounds.Add(new Rectangle(x, top, ItemSize, ItemSize));
            if (!isLast) y += ItemSize + ItemGap;
        }

        Invalidate();
    }

    private int HitTest(Point p)
    {
        for (var i = 0; i < _bounds.Count; i++)
        {
            if (_bounds[i].Contains(p)) return i;
        }

        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var hit = HitTest(e.Location);
        if (hit == _hover) return;

        _hover = hit;

        // 悬停提示按项显示：只有真的停在某一项上才给文字
        _tip.SetToolTip(this, hit >= 0 ? _items[hit].Tip : "");

        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _tip.SetToolTip(this, "");
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        var hit = HitTest(e.Location);
        if (hit >= 0) SelectedIndex = hit;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        Theme.ClearBackdrop(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // ---- 产品标：一块主色圆角方块 + 白色勾 ----
        var logo = new RectangleF((Width - LogoSize) / 2F, 16F, LogoSize, LogoSize);
        Theme.FillRound(g, logo, 12F, Theme.AccentLight);
        DrawCheck(g, logo, Color.White, 1.9F);

        for (var i = 0; i < _items.Count && i < _bounds.Count; i++)
        {
            var b = _bounds[i];
            var selected = i == _selected;
            var hovered = i == _hover;

            // 设置项与上方页面之间画一条极淡的分隔线
            if (_items[i].Icon == NavIcon.Settings && i > 0)
            {
                Theme.DrawHairline(g, 22, b.Top - 12, Width - 22, Theme.RailDivider);
            }

            if (selected)
            {
                Theme.FillRound(g, new RectangleF(b.X, b.Y, b.Width, b.Height), 12F, Theme.AccentLight);
            }
            else if (hovered)
            {
                Theme.FillRound(g, new RectangleF(b.X, b.Y, b.Width, b.Height), 12F, Theme.RailItemHover);
            }

            var color = selected ? Color.White : hovered ? Theme.OnDark : Theme.RailIcon;
            DrawIcon(g, _items[i].Icon, b, color);
        }
    }

    /// <summary>把一个图标画进给定方块（居中、按短边缩放）。</summary>
    private static void DrawIcon(Graphics g, NavIcon icon, Rectangle box, Color color)
    {
        const int size = 19;
        var x = box.X + (box.Width - size) / 2F;
        var y = box.Y + (box.Height - size) / 2F;

        switch (icon)
        {
            case NavIcon.Dashboard:
            {
                // 2×2 圆角小方块，右上一块留空 —— 与"网格"图标的常见形态一致
                var cell = (size - 3) / 2F;
                foreach (var (cx, cy) in new[] { (0F, 0F), (cell + 3, 0F), (0F, cell + 3), (cell + 3, cell + 3) })
                {
                    Theme.FillRound(g, new RectangleF(x + cx, y + cy, cell, cell), 2F, color);
                }

                break;
            }

            case NavIcon.Check:
            {
                using var pen = new Pen(color, 1.7F);
                var r = new RectangleF(x, y, size, size);
                g.DrawEllipse(pen, r);

                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.Width = 1.9F;
                g.DrawLines(pen,
                [
                    new PointF(x + 5.2F, y + 9.8F),
                    new PointF(x + 8.4F, y + 13.0F),
                    new PointF(x + 14.0F, y + 6.2F),
                ]);

                break;
            }

            case NavIcon.Calendar:
            {
                using var pen = new Pen(color, 1.5F);
                var body = new RectangleF(x + 0.5F, y + 2.5F, size - 1F, size - 3F);
                using var path = Theme.Round(body, 3.5F);
                g.DrawPath(pen, path);

                g.DrawLine(pen, x + 0.5F, y + 7.5F, x + size - 0.5F, y + 7.5F);
                g.DrawLine(pen, x + 5F, y + 0.5F, x + 5F, y + 4F);
                g.DrawLine(pen, x + size - 5F, y + 0.5F, x + size - 5F, y + 4F);

                using var dot = new SolidBrush(color);
                g.FillEllipse(dot, x + 5F, y + 11F, 2.4F, 2.4F);
                g.FillEllipse(dot, x + 11F, y + 11F, 2.4F, 2.4F);

                break;
            }

            default:
            {
                // 设置：两条滑杆
                using var pen = new Pen(color, 1.5F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(pen, x, y + 6F, x + size, y + 6F);
                g.DrawLine(pen, x, y + 13F, x + size, y + 13F);

                using var knob = new SolidBrush(color);
                g.FillEllipse(knob, x + 3F, y + 3F, 6F, 6F);
                g.FillEllipse(knob, x + size - 9F, y + 10F, 6F, 6F);

                break;
            }
        }
    }

    /// <summary>在产品标方块中央画一个白色勾。</summary>
    private static void DrawCheck(Graphics g, RectangleF box, Color color, float width)
    {
        using var pen = new Pen(color, width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        var cx = box.X + box.Width / 2F;
        var cy = box.Y + box.Height / 2F;
        var s = box.Width * 0.28F;

        g.DrawLines(pen,
        [
            new PointF(cx - s, cy + 0.5F),
            new PointF(cx - s * 0.25F, cy + s * 0.75F),
            new PointF(cx + s, cy - s * 0.7F),
        ]);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
