using System.Drawing.Drawing2D;

namespace LynkSign.WinForms.Ui;

/// <summary>
/// 分段控件：一条浅灰底槽里并排几个等宽选项，选中项是一块白色圆角块。
///
/// <para>
/// 设置页的"网络模式"用它，因为三档之间互斥、又希望三档同时可见 ——
/// 这正是下拉框做不到的（下拉框里另外两档得点开才知道有什么）。
/// </para>
///
/// <para>
/// 选项等宽，而不是按文字自适应：等宽让点击目标大小一致，也不会因为文字长短
/// 让选中块一会在左一会在右地"跳"。
/// </para>
/// </summary>
internal sealed class SegmentedControl : Control
{
    private const int Pad = 3;

    private readonly List<string> _items = [];
    private readonly List<Rectangle> _bounds = [];

    private int _selected;
    private int _hover = -1;

    public SegmentedControl()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.CardBg;
        Font = Theme.Base;
        Height = 34;
        Cursor = Cursors.Hand;
    }

    public event EventHandler? SelectedIndexChanged;

    public IReadOnlyList<string> Items => _items;

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
        Rebuild();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Rebuild();
    }

    private void Rebuild()
    {
        _bounds.Clear();
        if (_items.Count == 0) return;

        var width = (Width - Pad * 2) / _items.Count;
        for (var i = 0; i < _items.Count; i++)
        {
            var w = i == _items.Count - 1 ? Width - Pad - width * i : width;
            _bounds.Add(new Rectangle(Pad + width * i, Pad, w, Height - Pad * 2));
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
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
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

        var outer = new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F);
        Theme.FillRound(g, outer, Theme.FieldRadius, Theme.FieldBg);
        Theme.DrawRound(g, outer, Theme.FieldRadius, Theme.CardBorder);

        for (var i = 0; i < _items.Count && i < _bounds.Count; i++)
        {
            var b = _bounds[i];
            var selected = i == _selected;

            if (selected)
            {
                var pill = new RectangleF(b.X, b.Y, b.Width, b.Height);

                // 选中块是白底 + 一道极淡的边：浅灰槽里只有白色浮得起来
                Theme.FillRound(g, pill, 6F, Color.White);
                Theme.DrawRound(g, pill, 6F, Theme.CardBorder);
            }
            else if (i == _hover)
            {
                Theme.FillRound(g, new RectangleF(b.X, b.Y, b.Width, b.Height), 6F, Theme.HoverBg);
            }

            TextRenderer.DrawText(
                g,
                _items[i],
                selected ? Theme.SmallBold : Theme.Small,
                b,
                selected ? Theme.TextStrong : Theme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
