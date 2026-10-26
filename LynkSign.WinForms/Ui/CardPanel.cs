namespace LynkSign.WinForms.Ui;

/// <summary>
/// 圆角卡片容器：一块平面 + 0.5px 冷灰细边，可选标题。
///
/// <para>
/// <b>关键点</b>：<see cref="DisplayRectangle"/> 被改写，把标题占用的那一条从可布局区域里挖掉。
/// 这样内部的 Dock=Fill / Dock=Top 子控件会自动从标题下方开始排，
/// 不需要每个调用点自己算 "标题多高、要留多少 Padding"。
/// </para>
///
/// <para>
/// 卡片圆角外的像素填成页面底色，于是圆角自然"融"进页面背景，不会有白角。
/// </para>
///
/// <para>
/// <see cref="Fill"/> 允许换底色（日志卡整套是深色的）。标题/备注的文字颜色会按底色
/// 自动切到深字或浅字，调用方不用两处都记着改。
/// </para>
/// </summary>
internal class CardPanel : Panel
{
    /// <summary>标题带的净高度（含标题文字与它下面留给内容的一点空隙）。</summary>
    public const int TitleHeight = 34;

    private string? _title;
    private string? _note;

    public CardPanel()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        // 圆角外的像素 = 页面底色，于是四角看起来是"镂空"的
        BackColor = Theme.PageBg;
        Padding = new Padding(Theme.CardPadding);
    }

    /// <summary>卡片标题。为空则整张卡片就是一个纯容器。</summary>
    public string? Title
    {
        get => _title;
        set
        {
            _title = value;
            PerformLayout();
            Invalidate();
        }
    }

    /// <summary>标题右侧的小字备注（如单位、说明）。</summary>
    public string? Note
    {
        get => _note;
        set
        {
            _note = value;
            Invalidate();
        }
    }

    private Color _fill = Theme.CardBg;

    /// <summary>卡片填充色。深色卡片（日志）由调用方指定。</summary>
    public Color Fill
    {
        get => _fill;
        set
        {
            _fill = value;
            Invalidate();
        }
    }

    /// <summary>卡片边框色，可按状态换色。</summary>
    public Color BorderColor { get; set; } = Theme.CardBorder;

    /// <summary>标题带是否占用布局空间。</summary>
    private int TitleOffset => string.IsNullOrEmpty(_title) ? 0 : TitleHeight;

    public override Rectangle DisplayRectangle
    {
        get
        {
            var r = base.DisplayRectangle;
            var offset = TitleOffset;
            if (offset == 0) return r;

            return new Rectangle(r.X, r.Y + offset, r.Width, Math.Max(0, r.Height - offset));
        }
    }

    /// <summary>
    /// 期望高度要把标题带补回来。
    ///
    /// <para>
    /// 基类算期望高度时只按子控件的尺寸 + Padding，<b>不会</b>把
    /// <see cref="DisplayRectangle"/> 里挖掉的标题带算进去；
    /// 而卡片又是靠 <c>AutoSize</c> 撑高的，不补这 34px 卡片就会矮一条，
    /// 表现为最下面一行内容被卡片下边缘裁掉。
    /// </para>
    /// </summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        var size = base.GetPreferredSize(proposedSize);
        return string.IsNullOrEmpty(_title) ? size : new Size(size.Width, size.Height + TitleOffset);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        // 圆角之外的四个角必须主动刷成页面底色，否则露出来的是双缓冲缓冲区的残留（黑角）。
        Theme.ClearBackdrop(g, this);

        var full = new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F);

        Theme.DrawCardFilled(g, full, Theme.CardRadius, Fill, BorderColor);

        if (!string.IsNullOrEmpty(_title))
        {
            // 深色卡片上标题要反过来：底色一深，深字就整块糊掉了
            var dark = Theme.IsDark(Fill);
            var titleColor = dark ? Color.FromArgb(0xEE, 0xF1, 0xF6) : Theme.TextStrong;
            var noteColor = dark ? Color.FromArgb(0x7C, 0x86, 0x94) : Theme.TextFaint;

            var y = Padding.Top;
            var textRect = new RectangleF(Padding.Left, y - 2F, Width - Padding.Left - Padding.Right, 20F);

            TextRenderer.DrawText(
                g,
                _title,
                Theme.Section,
                Rectangle.Round(textRect),
                titleColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

            if (!string.IsNullOrEmpty(_note))
            {
                TextRenderer.DrawText(
                    g,
                    _note,
                    Theme.Small,
                    Rectangle.Round(textRect),
                    noteColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }

        base.OnPaint(e);
    }
}
