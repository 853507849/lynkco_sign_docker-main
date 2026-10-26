namespace LynkSign.WinForms.Ui;

/// <summary>
/// 页签条：文字 + 选中时下方一条主题色细横线。
///
/// <para>
/// 用它替换原生 <see cref="TabControl"/>。原生页签的字体、配色、边框全跟着系统主题走，
/// 与卡片式界面放在一起会明显"两个时代"；而 <c>TabControl</c> 的页签区属于非客户区，
/// 自绘要跟 <c>WM_NCPAINT</c> 较劲，收益不成正比。
/// </para>
///
/// <para>
/// 视觉上它属于"外壳"（白底），与下面的灰色内容区靠一条 1px 细线分开 ——
/// 顶栏、页签、页脚共用同一块白，界面才像一整件东西，而不是三条横杠叠在一起。
/// </para>
///
/// <para>这里只管画与选中，页面切换由调用方监听 <see cref="SelectedIndexChanged"/> 自己控制。</para>
/// </summary>
internal sealed class TabStrip : Control
{
    private const int ItemPadding = 30;
    private const int UnderlineHeight = 3;

    /// <summary>首个页签的左边距：与标题、卡片左边缘对齐（22 是标题栏文字的左内边距）。</summary>
    private const int LeftInset = 22;

    private readonly List<string> _items = [];
    private readonly List<Rectangle> _bounds = [];

    private int _selected;
    private int _hoverIndex = -1;

    public TabStrip()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.ChromeBg;
        Height = 46;
        Cursor = Cursors.Hand;
    }

    public IReadOnlyList<string> Items => _items;

    public event EventHandler? SelectedIndexChanged;

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

    public void AddItem(string text)
    {
        _items.Add(text);
        RebuildLayout();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RebuildLayout();
    }

    /// <summary>页签宽度按文字自适应；用粗体测量，避免选中变粗时整条页签左右跳动。</summary>
    private void RebuildLayout()
    {
        _bounds.Clear();

        var x = LeftInset;
        foreach (var item in _items)
        {
            var width = TextRenderer.MeasureText(
                item,
                Theme.BaseBold,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding).Width + ItemPadding;

            _bounds.Add(new Rectangle(x, 0, width, Height));
            x += width;
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
        if (hit == _hoverIndex) return;

        _hoverIndex = hit;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
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

        for (var i = 0; i < _items.Count; i++)
        {
            if (i >= _bounds.Count) break;

            var bounds = _bounds[i];
            var selected = i == _selected;
            var hovered = i == _hoverIndex;

            // 选中不靠"变蓝"取胜，而是靠颜色深浅 + 下方那条线；蓝色只留给线。
            var textColor = selected
                ? Theme.TextStrong
                : hovered
                    ? Theme.TextBody
                    : Theme.TextMuted;

            TextRenderer.DrawText(
                g,
                _items[i],
                selected ? Theme.BaseBold : Theme.Base,
                new Rectangle(bounds.X, 0, bounds.Width, bounds.Height - UnderlineHeight),
                textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // 选中：一条实色短横线；悬停：一条更淡的，给点击一点预反馈
            float barWidth;
            Color barColor;

            if (selected)
            {
                barWidth = bounds.Width - ItemPadding + 10F;
                barColor = Theme.Accent;
            }
            else if (hovered)
            {
                barWidth = bounds.Width - ItemPadding - 4F;
                barColor = Color.FromArgb(0xD8, 0xDD, 0xE6);
            }
            else
            {
                continue;
            }

            var bx = bounds.X + (bounds.Width - barWidth) / 2F;
            var by = bounds.Bottom - UnderlineHeight - 1F;

            Theme.FillRound(g, new RectangleF(bx, by, barWidth, UnderlineHeight), UnderlineHeight / 2F, barColor);
        }

        // 整条页签底部压一根淡分隔线，把外壳与内容区分开
        Theme.DrawHairline(g, 0, Height - 1, Width, Theme.Divider);
    }
}
