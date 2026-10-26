using System.Drawing.Drawing2D;

namespace LynkSign.WinForms.Ui;

/// <summary>
/// 自绘复选框：圆角小方块 + 主题色勾号。
///
/// <para>原生 CheckBox 的方块是系统画的，改不了颜色，和卡片式界面放一起很跳。</para>
/// </summary>
internal sealed class FlatCheckBox : CheckBox
{
    private const int BoxSize = 16;
    private const int Gap = 8;

    public FlatCheckBox()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.CardBg;
        ForeColor = Theme.TextBody;
        Font = Theme.Base;
        Cursor = Cursors.Hand;

        AutoSize = true;
        Height = 22;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(
            Text,
            Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding);

        return new Size(BoxSize + Gap + text.Width + 2, Math.Max(BoxSize + 6, 22));
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        Invalidate();
        base.OnCheckedChanged(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;

        Theme.ClearBackdrop(g, this);

        g.SmoothingMode = SmoothingMode.AntiAlias;

        var x = 1F;
        var y = (Height - BoxSize) / 2F;
        var box = new RectangleF(x, y, BoxSize, BoxSize);

        if (Checked)
        {
            Theme.FillRound(g, box, 5F, Enabled ? Theme.Accent : Color.FromArgb(0xC9, 0xD5, 0xEE));

            using var pen = new Pen(Color.White, 2F)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };

            g.DrawLines(pen,
            [
                new PointF(x + 3.8F, y + 8.3F),
                new PointF(x + 6.7F, y + 11.2F),
                new PointF(x + 12.2F, y + 5.0F),
            ]);
        }
        else
        {
            Theme.FillRound(g, box, 5F, Color.White);
            Theme.DrawRound(g, box, 5F, Color.FromArgb(0xCF, 0xD5, 0xDF));
        }

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            new Rectangle(BoxSize + Gap, 0, Math.Max(0, Width - BoxSize - Gap), Height),
            Enabled ? ForeColor : Theme.TextFaint,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
