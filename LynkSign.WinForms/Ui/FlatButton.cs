namespace LynkSign.WinForms.Ui;

/// <summary>按钮的视觉层级。</summary>
internal enum ButtonKind
{
    /// <summary>主操作：主题色实心。</summary>
    Primary,

    /// <summary>次要操作：白底描边。</summary>
    Secondary,

    /// <summary>弱操作：无边框，只有文字，鼠标移上去才浮出底色。</summary>
    Ghost,
}

/// <summary>
/// 自绘按钮：圆角、有 hover / pressed / disabled 三态。
///
/// <para>
/// 原生 <c>FlatStyle.System</c> 按钮在不同 Windows 版本上长得不一样，
/// 而且没法做圆角与主题色，所以整只重画。
/// </para>
/// </summary>
internal sealed class FlatButton : Button
{
    private const int PreferredHeight = 34;
    private const int MinWidth = 76;

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

        var padding = Kind == ButtonKind.Ghost ? 22 : 34;
        return new Size(Math.Max(text.Width + padding, MinWidth), PreferredHeight);
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
        Theme.ClearBackdrop(g, this);

        var r = new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F);

        var (fill, border, text) = ResolveColors();

        if (fill != Color.Transparent)
        {
            Theme.FillRound(g, r, Theme.ButtonRadius, fill);
        }

        if (border != Color.Transparent)
        {
            Theme.DrawRound(g, r, Theme.ButtonRadius, border);
        }

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            ClientRectangle,
            text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private (Color Fill, Color Border, Color Text) ResolveColors()
    {
        if (!Enabled)
        {
            return Kind switch
            {
                ButtonKind.Primary => (Color.FromArgb(0xC6, 0xD3, 0xEA), Color.Transparent, Color.FromArgb(0xF6, 0xF8, 0xFC)),
                ButtonKind.Ghost => (Color.Transparent, Color.Transparent, Theme.TextFaint),
                _ => (Color.FromArgb(0xF5, 0xF6, 0xF8), Theme.CardBorder, Theme.TextFaint),
            };
        }

        return Kind switch
        {
            ButtonKind.Primary => (
                _pressed ? Theme.AccentDown : _hover ? Theme.AccentHover : Theme.Accent,
                Color.Transparent,
                Color.White),

            ButtonKind.Ghost => (
                _pressed
                    ? Color.FromArgb(0xE6, 0xEA, 0xF1)
                    : _hover
                        ? Color.FromArgb(0xEE, 0xF1, 0xF6)
                        : Color.Transparent,
                Color.Transparent,
                _hover || _pressed ? Theme.TextBody : Theme.TextMuted),

            _ => (
                _pressed
                    ? Color.FromArgb(0xEC, 0xF0, 0xF6)
                    : _hover
                        ? Color.FromArgb(0xF8, 0xFA, 0xFD)
                        : Color.White,
                _hover || _pressed ? Color.FromArgb(0xCF, 0xD8, 0xE6) : Theme.CardBorder,
                _pressed ? Theme.TextStrong : Theme.TextBody),
        };
    }
}
