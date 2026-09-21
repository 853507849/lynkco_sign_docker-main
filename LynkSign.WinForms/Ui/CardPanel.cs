namespace LynkSign.WinForms.Ui;

/// <summary>
/// 圆角卡片容器：白底、淡边框，可选标题（左侧带主题色小竖条）。
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
/// </summary>
internal class CardPanel : Panel
{
    /// <summary>标题带的净高度（含标题文字与它下面留给内容的一点空隙）。</summary>
    private const int TitleBand = 32;

    private const float BarWidth = 3F;
    private const float BarHeight = 15F;

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

    /// <summary>卡片边框色，可按状态换色（如运行中描成主题色）。</summary>
    public Color BorderColor { get; set; } = Theme.CardBorder;

    /// <summary>左侧竖条的颜色。<c>null</c> 表示不画竖条。</summary>
    public Color? AccentColor { get; set; } = Theme.Accent;

    /// <summary>标题带是否占用布局空间。</summary>
    private int TitleOffset => string.IsNullOrEmpty(_title) ? 0 : TitleBand;

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
    /// 而卡片又是靠 <c>AutoSize</c> 撑高的，不补这 32px 卡片就会矮一条，
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

        Theme.DrawCard(g, full, BorderColor);

        if (!string.IsNullOrEmpty(_title))
        {
            var y = Padding.Top;

            if (AccentColor is { } accent)
            {
                Theme.DrawAccentBar(g, Padding.Left, y + 1F, BarHeight, accent, BarWidth);
            }

            var textX = Padding.Left + (AccentColor is null ? 0F : BarWidth + 8F);
            var textRect = new RectangleF(textX, y - 2F, Width - textX - Padding.Right, BarHeight + 6F);

            TextRenderer.DrawText(
                g,
                _title,
                Theme.Section,
                Rectangle.Round(textRect),
                Theme.TextStrong,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

            if (!string.IsNullOrEmpty(_note))
            {
                TextRenderer.DrawText(
                    g,
                    _note,
                    Theme.Small,
                    Rectangle.Round(textRect),
                    Theme.TextFaint,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }

        base.OnPaint(e);
    }
}
