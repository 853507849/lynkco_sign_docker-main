namespace LynkSign.WinForms.Ui;

/// <summary>
/// 页面头部：左边是大标题 + 一行说明，右边是一组操作按钮。
///
/// <para>
/// 四张页面都用同一个头部，页与页之间切换时标题的位置不跳 —— 页面标题如果每页都自己摆，
/// 切换时会左右相差几个像素，看着像界面在抖。
/// </para>
///
/// <para>
/// 说明行（副标题）用半角的等宽字体：里面装的是时间、日期、统计这类机器文本，
/// 等宽之后数字不会随内容变化左右横跳。
/// </para>
/// </summary>
internal sealed class PageHeader : Panel
{
    private const int TopInset = 18;
    private const int TitleToSub = 7;
    private const int BottomInset = 14;

    private string _title = "";
    private string _subtitle = "";

    private readonly Label _titleLabel;
    private readonly Label _subtitleLabel;

    public PageHeader()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        Dock = DockStyle.Top;
        BackColor = Theme.PageBg;

        _titleLabel = new Label
        {
            AutoSize = true,
            Font = Theme.H1,
            ForeColor = Theme.TextStrong,
            BackColor = Color.Transparent,
        };

        _subtitleLabel = new Label
        {
            AutoSize = true,
            Font = Theme.Mono,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
        };

        Actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.PageBg,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        Controls.Add(_titleLabel);
        Controls.Add(_subtitleLabel);
        Controls.Add(Actions);

        // Padding 放到最后再设：set_Padding 会立刻触发一次 PerformLayout，
        // 而那时标题/操作区还没建出来，OnLayout 里就会吃到 null。
        Padding = new Padding(Theme.PadX, 0, Theme.PadX, 0);

        Height = TopInset + Theme.H1.Height + TitleToSub + Theme.Mono.Height + BottomInset;
    }

    /// <summary>右侧操作区（先加的在最右边）。由调用方往里塞按钮。</summary>
    public FlowLayoutPanel Actions { get; }

    public string Title
    {
        get => _title;
        set
        {
            _title = value ?? "";
            _titleLabel.Text = _title;
            PerformLayout();
        }
    }

    public string Subtitle
    {
        get => _subtitle;
        set
        {
            _subtitle = value ?? "";
            _subtitleLabel.Text = _subtitle;
            _subtitleLabel.Visible = _subtitle.Length > 0;
            PerformLayout();
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);

        // 构造期 set_Padding 会在子控件就位前触发一次布局
        if (_titleLabel is null || Actions is null) return;

        var left = Padding.Left;
        var y = TopInset;

        _titleLabel.Location = new Point(left, y);
        y += _titleLabel.Height + TitleToSub;
        _subtitleLabel.Location = new Point(left, y);

        // 操作区：靠右、与标题块垂直居中。宽度由内容决定，其余空间归左侧文字。
        var actions = Actions;
        var size = actions.PreferredSize;
        actions.SetBounds(
            Math.Max(left, Width - Padding.Right - size.Width),
            Math.Max(0, (Height - size.Height) / 2),
            size.Width,
            size.Height);
    }
}
