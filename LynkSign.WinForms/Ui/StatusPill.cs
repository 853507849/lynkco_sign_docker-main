using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace LynkSign.WinForms.Ui;

/// <summary>
/// 状态胶囊：一个圆点 + 一行小字，底色是语气色的淡色版。
/// 放在标题栏右侧，一眼看出「空闲 / 运行中」。
/// </summary>
internal sealed class StatusPill : Control
{
    private const int PadX = 13;
    private const int Gap = 7;
    private const int DotSize = 6;

    private string _text = "";
    private Color _tone = Theme.TextMuted;
    private Color _fill = Color.FromArgb(0xEE, 0xF1, 0xF5);

    public StatusPill()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.ChromeBg;
        Font = Theme.SmallBold;
        Height = 28;
    }

    [AllowNull]
    public override string Text
    {
        get => _text;
        set
        {
            var v = value ?? "";
            if (_text == v) return;

            _text = v;
            Width = MeasureWidth();
            Invalidate();
        }
    }

    /// <summary>同时决定圆点颜色与文字颜色。</summary>
    public Color Tone
    {
        get => _tone;
        set
        {
            _tone = value;
            Invalidate();
        }
    }

    /// <summary>胶囊底色。</summary>
    public Color Fill
    {
        get => _fill;
        set
        {
            _fill = value;
            Invalidate();
        }
    }

    /// <summary>一次性设置文字、语气色与底色，避免调用方连写三行。</summary>
    public void SetState(string text, Color tone, Color fill)
    {
        Tone = tone;
        Fill = fill;
        Text = text;
    }

    private int MeasureWidth()
    {
        var text = TextRenderer.MeasureText(
            _text,
            Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding);

        return PadX * 2 + DotSize + Gap + text.Width;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Width = MeasureWidth();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        Theme.ClearBackdrop(g, this);

        g.SmoothingMode = SmoothingMode.AntiAlias;

        var r = new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F);
        Theme.FillRound(g, r, Height / 2F, _fill);

        var dotY = Height / 2F;
        using (var dot = new SolidBrush(_tone))
        {
            g.FillEllipse(dot, PadX, dotY - DotSize / 2F, DotSize, DotSize);
        }

        TextRenderer.DrawText(
            g,
            _text,
            Font,
            new Rectangle(PadX + DotSize + Gap, 0, Math.Max(0, Width - PadX - DotSize - Gap), Height),
            _tone,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
