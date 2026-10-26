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
/// 层级只靠"实心 / 描边 / 纯文字"三档区分，不靠换色：一屏里同时出现三种彩色按钮，
/// 就等于一个重点都没有。次级操作永远是白底 + 细边，弱操作连边都没有。
/// </para>
/// </summary>
internal sealed class FlatButton : Button
{
    private const int PreferredHeight = 42;
    private const int MinWidth = 96;

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

        var padding = Kind == ButtonKind.Ghost ? 22 : 52;
        return new Size(Math.Max(text.Width + padding, Kind == ButtonKind.Ghost ? 62 : MinWidth), PreferredHeight);
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
        //
        // 刷的是<b>父容器</b>的底色而不是自己的 BackColor：按钮既会出现在白色页脚上，
        // 也会出现在白色卡片里，如果按固定色刷，幽灵按钮在页脚上就会显出一块
        // 比周围暗一档的灰方块 —— 明明没有边框，却像是贴了张纸。
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

        var font = Kind == ButtonKind.Primary ? Theme.BaseBold : Font;

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
        return back == Color.Transparent ? Theme.ChromeBg : back;
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
                ButtonKind.Primary => (Color.FromArgb(0xC9, 0xD7, 0xF5), null, Color.Transparent, Color.FromArgb(0xF7, 0xF9, 0xFD)),
                ButtonKind.Ghost => (Color.Transparent, null, Color.Transparent, Theme.TextFaint),
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
                _pressed ? Theme.PressBg : _hover ? Theme.ChipHover : Theme.ChipBg,
                null,
                _hover || _pressed ? Theme.ChipBorderHover : Theme.ChipBorder,
                _hover || _pressed ? Theme.TextStrong : Theme.TextBody),

            _ => (
                _pressed ? Theme.PressBg : _hover ? Color.FromArgb(0xFA, 0xFB, 0xFE) : Theme.CardBg,
                null,
                _hover || _pressed ? Color.FromArgb(0xC9, 0xD5, 0xEC) : Theme.CardBorder,
                _pressed ? Theme.TextStrong : Theme.TextBody),
        };
    }
}
