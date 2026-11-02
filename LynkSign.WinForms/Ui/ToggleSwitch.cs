using System.Drawing.Drawing2D;

namespace LynkSign.WinForms.Ui;

/// <summary>
/// 开关（Switch）：一条圆角轨道 + 一个白色圆钮，开启时轨道染主色。
///
/// <para>
/// 继承 <see cref="CheckBox"/> 而不是从零做控件：<c>Checked</c> / <c>CheckedChanged</c> /
/// 键盘空格切换这些行为全都白拿，只把绘制换掉。
/// </para>
///
/// <para>
/// 只负责画开关本身，不画文字 —— 设置页里"标题在左、开关在右"，
/// 文字交给左边那一列的 Label。自带文字的话就做不到两端对齐。
/// </para>
/// </summary>
internal sealed class ToggleSwitch : CheckBox
{
    private const int TrackWidth = 42;
    private const int TrackHeight = 24;
    private const int Knob = 18;

    private bool _hover;

    public ToggleSwitch()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        AutoSize = false;
        Size = new Size(TrackWidth, TrackHeight);
        BackColor = Theme.CardBg;
        Cursor = Cursors.Hand;
        Text = "";
    }

    protected override void OnMouseEnter(EventArgs eventargs)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(eventargs);
    }

    protected override void OnMouseLeave(EventArgs eventargs)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(eventargs);
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        Invalidate();
        base.OnCheckedChanged(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;

        Theme.ClearBackdrop(g, this);

        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 开关本身不撑满控件：右对齐到控件右边缘，左边留白由调用方给
        var track = new RectangleF(Width - TrackWidth, (Height - TrackHeight) / 2F, TrackWidth, TrackHeight);

        Color fill;
        if (!Enabled)
        {
            fill = Checked ? Theme.Mix(Theme.Accent, Color.White, 0.55) : Color.FromArgb(0xE9, 0xED, 0xF3);
        }
        else if (Checked)
        {
            fill = _hover ? Theme.AccentHover : Theme.Accent;
        }
        else
        {
            fill = _hover ? Color.FromArgb(0xD5, 0xDC, 0xE6) : Color.FromArgb(0xE2, 0xE8, 0xF0);
        }

        Theme.FillRound(g, track, TrackHeight / 2F, fill);

        var knobX = Checked ? track.Right - Knob - 3F : track.X + 3F;
        var knobRect = new RectangleF(knobX, track.Y + 3F, Knob, Knob);

        // 圆钮带一点极淡的投影：不加的话白钮压在浅色轨道上边界发虚
        using (var shadow = new SolidBrush(Color.FromArgb(28, 0, 0, 0)))
        {
            g.FillEllipse(shadow, knobRect.X, knobRect.Y + 1F, knobRect.Width, knobRect.Height);
        }

        using (var brush = new SolidBrush(Color.White))
        {
            g.FillEllipse(brush, knobRect);
        }
    }
}
