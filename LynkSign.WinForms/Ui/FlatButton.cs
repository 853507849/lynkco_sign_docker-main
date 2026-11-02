using System.Drawing.Drawing2D;

namespace LynkSign.WinForms.Ui;

/// <summary>按钮的视觉层级。</summary>
internal enum ButtonKind
{
    /// <summary>主操作：主题色实心。</summary>
    Primary,

    /// <summary>次要操作：白底描边。</summary>
    Secondary,

    /// <summary>弱操作：浅灰填充的胶囊，没有描边。</summary>
    Ghost,

    /// <summary>破坏性操作：砖红文字 + 极淡红底。</summary>
    Danger,
}

/// <summary>小图标按钮可画的图标。</summary>
internal enum GlyphIcon
{
    Refresh,
    Copy,
    Folder,
    ChevronLeft,
    ChevronRight,
    Download,
    Close,
}

/// <summary>
/// 自绘按钮：圆角、有 hover / pressed / disabled 三态。
///
/// <para>
/// 原生 <c>FlatStyle.System</c> 按钮在不同 Windows 版本上长得不一样，
/// 而且没法做圆角与主题色，所以整只重画。
/// </para>
///
/// <para>
/// 层级只靠"实心 / 描边 / 浅底"三档区分，不靠换色：一屏里同时出现三种彩色按钮，
/// 就等于一个重点都没有。主按钮是整页唯一带渐变的那颗，其余全是平的。
/// </para>
/// </summary>
internal sealed class FlatButton : Button
{
    private const int PreferredHeight = 38;
    private const int MinWidth = 84;

    private bool _hover;
    private bool _pressed;

    public FlatButton()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Theme.PageBg;
        ForeColor = Theme.TextBody;
        Font = Theme.Base;

        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Height = PreferredHeight;

        Cursor = Cursors.Hand;
    }

    public ButtonKind Kind { get; set; } = ButtonKind.Secondary;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(
            Text,
            Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding);

        var padding = Kind == ButtonKind.Ghost ? 20 : 40;
        return new Size(Math.Max(text.Width + padding, Kind == ButtonKind.Ghost ? 54 : MinWidth), PreferredHeight);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left) _pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;

        // 幽灵按钮自己不画任何底色，不先整块刷底的话，双缓冲缓冲区里的残留像素
        // （别的控件留下的白框、上一帧画在旧位置上的文字）会原样露出来。
        // 刷的是「父容器」的底色而不是自己的 BackColor：同一颗按钮既会出现在白卡片里，
        // 也会出现在浅灰页面上，按固定色刷就会显出一块比周围暗一档的方块。
        Theme.ClearBackdrop(g, this, Backdrop());

        var r = new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F);

        var (fill, top, border, text) = ResolveColors();

        if (fill != Color.Transparent)
        {
            // 主按钮走同色系微渐变：上沿亮、下沿沉，看起来像一块有厚度的色块。
            // 其余按钮是平的 —— 一屏里只允许一颗按钮有"体积"。
            if (top is { } topColor)
            {
                Theme.FillRoundGradient(g, r, Theme.ButtonRadius, topColor, fill);
            }
            else
            {
                Theme.FillRound(g, r, Theme.ButtonRadius, fill);
            }
        }

        if (border != Color.Transparent)
        {
            Theme.DrawRound(g, r, Theme.ButtonRadius, border);
        }

        var font = Kind is ButtonKind.Primary ? Theme.BaseBold : Font;

        TextRenderer.DrawText(
            g,
            Text,
            font,
            ClientRectangle,
            text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    /// <summary>按钮所在容器的实际底色（透明时再往上一层找）。</summary>
    private Color Backdrop()
    {
        var back = Parent?.BackColor ?? Theme.CardBg;
        if (back == Color.Transparent) back = Parent?.Parent?.BackColor ?? Theme.PageBg;
        return back == Color.Transparent ? Theme.PageBg : back;
    }

    /// <summary>
    /// 解析当前状态下的三层颜色。
    /// <c>Top</c> 为 null 表示这块按钮是平的（只有填充色）；非 null 表示要画竖向渐变。
    /// </summary>
    private (Color Fill, Color? Top, Color Border, Color Text) ResolveColors()
    {
        if (!Enabled)
        {
            return Kind switch
            {
                ButtonKind.Primary => (Theme.Mix(Theme.Accent, Color.White, 0.62), null, Color.Transparent,
                    Color.FromArgb(0xF4, 0xFA, 0xF8)),
                ButtonKind.Ghost => (Color.Transparent, null, Color.Transparent, Theme.TextFaint),
                ButtonKind.Danger => (Theme.DangerSoft, null, Color.Transparent, Theme.Mix(Theme.Danger, Color.White, 0.5)),
                _ => (Color.White, null, Theme.Divider, Theme.TextFaint),
            };
        }

        return Kind switch
        {
            ButtonKind.Primary => _pressed
                ? (Theme.AccentDown, Theme.AccentHover, Color.Transparent, Color.White)
                : _hover
                    ? (Theme.AccentHover, Theme.AccentHoverLight, Color.Transparent, Color.White)
                    : (Theme.Accent, Theme.AccentLight, Color.Transparent, Color.White),

            ButtonKind.Ghost => (
                _pressed ? Theme.PressBg : _hover ? Theme.ChipHover : Color.Transparent,
                null,
                Color.Transparent,
                _hover || _pressed ? Theme.TextStrong : Theme.TextMuted),

            ButtonKind.Danger => (
                _pressed ? Theme.DangerBorder : _hover ? Theme.DangerSoft : Color.Transparent,
                null,
                Color.Transparent,
                Theme.Danger),

            _ => (
                _pressed ? Theme.PressBg : _hover ? Theme.CardBg : Theme.CardBg,
                null,
                _hover || _pressed ? Theme.ChipBorderHover : Theme.CardBorder,
                _pressed ? Theme.TextStrong : Theme.TextBody),
        };
    }
}

/// <summary>
/// 正方形图标按钮（刷新、翻月、导出这类"只有图标"的动作）。
///
/// <para>
/// 图标用几何线条画，不依赖图标字体：项目是零 UI 依赖的，
/// 而 <c>Segoe MDL2</c> 在精简版 Windows 上不一定装全。
/// </para>
/// </summary>
internal sealed class IconButton : Control
{
    private bool _hover;
    private bool _pressed;

    public IconButton()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        Size = new Size(38, 38);
        BackColor = Theme.PageBg;
        Cursor = Cursors.Hand;
    }

    public GlyphIcon Icon { get; set; } = GlyphIcon.Refresh;

    /// <summary>是否画白底描边（默认画；页面上并排的图标按钮需要一致的白底）。</summary>
    public bool Outlined { get; set; } = true;

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left) _pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        Theme.ClearBackdrop(g, this);

        g.SmoothingMode = SmoothingMode.AntiAlias;

        var r = new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F);

        if (Outlined)
        {
            var fill = _pressed ? Theme.PressBg : _hover ? Theme.CardBg : Theme.CardBg;
            var border = _hover || _pressed ? Theme.ChipBorderHover : Theme.CardBorder;

            Theme.FillRound(g, r, Theme.ButtonRadius, fill);
            Theme.DrawRound(g, r, Theme.ButtonRadius, border);
        }
        else if (_hover)
        {
            Theme.FillRound(g, r, Theme.ButtonRadius, Theme.ChipHover);
        }

        var color = !Enabled ? Theme.TextFaint : _hover || _pressed ? Theme.TextStrong : Theme.TextBody;

        DrawGlyph(g, Icon, new RectangleF(Width / 2F - 8F, Height / 2F - 8F, 16F, 16F), color);
    }

    internal static void DrawGlyph(Graphics g, GlyphIcon icon, RectangleF box, Color color)
    {
        using var pen = new Pen(color, 1.5F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var x = box.X;
        var y = box.Y;
        var w = box.Width;
        var h = box.Height;

        switch (icon)
        {
            case GlyphIcon.Refresh:
            {
                // 3/4 圆弧 + 一个箭头：比画整圈更像"刷新"
                g.DrawArc(pen, x + 1, y + 1, w - 2, h - 2, 40, 280);
                g.DrawLines(pen,
                [
                    new PointF(x + w - 5.5F, y + 1.0F),
                    new PointF(x + w - 1.0F, y + 3.2F),
                    new PointF(x + w - 4.4F, y + 6.6F),
                ]);

                break;
            }

            case GlyphIcon.Copy:
            {
                var back = new RectangleF(x + 1, y + 1, w - 6, h - 6);
                var front = new RectangleF(x + 5, y + 5, w - 6, h - 6);

                using var backPath = Theme.Round(back, 2.5F);
                using var frontPath = Theme.Round(front, 2.5F);
                g.DrawPath(pen, backPath);
                g.DrawPath(pen, frontPath);

                break;
            }

            case GlyphIcon.Folder:
            {
                var path = new GraphicsPath();
                path.AddLines(
                [
                    new PointF(x + 1, y + 4),
                    new PointF(x + 6.5F, y + 4),
                    new PointF(x + 8.5F, y + 6),
                    new PointF(x + w - 1, y + 6),
                    new PointF(x + w - 1, y + h - 2),
                    new PointF(x + 1, y + h - 2),
                ]);
                path.CloseFigure();
                g.DrawPath(pen, path);
                path.Dispose();

                break;
            }

            case GlyphIcon.ChevronLeft:
            case GlyphIcon.ChevronRight:
            {
                var left = icon == GlyphIcon.ChevronLeft;
                var cx = x + w / 2F;
                var cy = y + h / 2F;
                var d = 3.5F;

                g.DrawLines(pen,
                [
                    new PointF(left ? cx + d : cx - d, cy - d),
                    new PointF(left ? cx - d : cx + d, cy),
                    new PointF(left ? cx + d : cx - d, cy + d),
                ]);

                break;
            }

            case GlyphIcon.Download:
            {
                g.DrawLine(pen, x + w / 2F, y + 1.5F, x + w / 2F, y + h - 5.5F);
                g.DrawLines(pen,
                [
                    new PointF(x + w / 2F - 4F, y + h - 9.5F),
                    new PointF(x + w / 2F, y + h - 5.5F),
                    new PointF(x + w / 2F + 4F, y + h - 9.5F),
                ]);
                g.DrawLine(pen, x + 1.5F, y + h - 2F, x + w - 1.5F, y + h - 2F);

                break;
            }

            default:
            {
                g.DrawLine(pen, x + 4F, y + 4F, x + w - 4F, y + h - 4F);
                g.DrawLine(pen, x + w - 4F, y + 4F, x + 4F, y + h - 4F);

                break;
            }
        }
    }
}
