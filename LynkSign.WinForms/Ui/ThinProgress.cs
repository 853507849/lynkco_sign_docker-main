using System.Drawing.Drawing2D;

namespace LynkSign.WinForms.Ui;

/// <summary>
/// 一条细进度条（4~8px），没有文字、没有刻度。
///
/// <para>
/// 进度条只表达"走到哪了"，具体数字写在旁边的文字里 —— 把百分比画进条里，
/// 条一短就挤成一团；而条一长，数字又被拉得太远。
/// </para>
/// </summary>
internal sealed class ThinProgress : Control
{
    private double _value;

    public ThinProgress()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.CardBg;
        Height = 6;
    }

    /// <summary>0~1 之间的进度；超出范围自动夹取。</summary>
    public double Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, 0d, 1d);
            if (Math.Abs(v - _value) < 0.0005) return;

            _value = v;
            Invalidate();
        }
    }

    public Color TrackColor { get; set; } = Color.FromArgb(0xE9, 0xED, 0xF4);
    public Color FillColor { get; set; } = Theme.Accent;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        Theme.ClearBackdrop(g, this);

        g.SmoothingMode = SmoothingMode.AntiAlias;

        var h = Math.Max(3, Height);
        var radius = h / 2F;

        Theme.FillRound(g, new RectangleF(0, 0, Width, h), radius, TrackColor);

        var w = (float)(Width * _value);
        if (w < 2F) return;

        Theme.FillRound(g, new RectangleF(0, 0, Math.Max(radius * 2, w), h), radius, FillColor);
    }
}
