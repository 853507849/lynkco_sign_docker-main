using System.Drawing.Drawing2D;

namespace LynkSign.WinForms.Ui;

/// <summary>
/// 小标签块：一枚圆角浅底 + 一行小字（可带时钟或圆点前缀）。
///
/// <para>
/// 「运行计划」卡底部的「距下次签到 17:59:52」就是它：数字每两秒变一次，
/// 用一整块底把它圈起来，比单摆一行灰字更容易被扫到。
/// </para>
///
/// <para>宽度按文字自适应，但 Dock 到容器里时会铺满 —— 两种用法都支持。</para>
/// </summary>
internal sealed class ChipLabel : Control
{
    private string _caption = "";

    public ChipLabel()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.CardBg;
        Height = 26;
        Font = Theme.Small;
    }

    public string Caption
    {
        get => _caption;
        set
        {
            _caption = value ?? "";
            if (Dock == DockStyle.None) Width = Measure();
            Invalidate();
        }
    }

    public Color Tone { get; set; } = Theme.TextBody;
    public Color Fill { get; set; } = Theme.ChipBg;
    public Color Border { get; set; } = Theme.ChipBorder;

    /// <summary>画圆点而不是时钟（用于语气标签，如「充足」）。</summary>
    public bool DotPrefix { get; set; }

    public override Size GetPreferredSize(Size proposedSize) => new(Measure(), 26);

    private int Measure()
        => TextRenderer.MeasureText(
            _caption,
            Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding).Width + 44;

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        Theme.ClearBackdrop(g, this);

        g.SmoothingMode = SmoothingMode.AntiAlias;

        var r = new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F);
        Theme.FillRound(g, r, Theme.ChipRadius, Fill);
        Theme.DrawRound(g, r, Theme.ChipRadius, Border);

        var cy = Height / 2F;

        if (DotPrefix)
        {
            using var dot = new SolidBrush(Tone);
            g.FillEllipse(dot, 11F, cy - 3F, 6F, 6F);
        }
        else
        {
            // 小时钟：一圈 + 两根指针
            using var pen = new Pen(Tone, 1.2F);
            g.DrawEllipse(pen, 10.5F, cy - 5.5F, 11F, 11F);
            g.DrawLine(pen, 16F, cy - 2.5F, 16F, cy);
            g.DrawLine(pen, 16F, cy, 18F, cy + 1.5F);
        }

        TextRenderer.DrawText(
            g,
            _caption,
            Font,
            new Rectangle(28, 0, Math.Max(0, Width - 34), Height),
            Tone,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}
