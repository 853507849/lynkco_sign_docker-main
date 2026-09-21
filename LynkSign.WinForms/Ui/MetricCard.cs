using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace LynkSign.WinForms.Ui;

/// <summary>
/// 指标卡：一个小标题 + 一个大数字/大字，是整屏视觉重心。
///
/// <para>
/// 之所以不用 Label 拼：原生 Label 的字号与基线很难对齐，
/// 而"大数字 + 小单位"这种排版必须自己控制基线，所以整张卡一次画完。
/// </para>
///
/// <para>
/// <b>排版上的一个坑</b>：大字号文字必须用「按点绘制」的重载
/// （<c>DrawText(g, text, font, Point, …)</c>），不能用「按矩形绘制」的重载。
/// 矩形版会把文字硬塞进给定高度里，19pt 的字塞进 30px 的框会把字形底部裁掉 ——
/// 「已」看起来会变成「コ」。按点绘制则始终按字体自身的行高排版，不会裁剪。
/// 整个内容块再按实际行高算出总高、在卡片内垂直居中，这样改字号也不会画崩。
/// </para>
///
/// <para>
/// <see cref="Text"/> 与 <see cref="Control.ForeColor"/> 被映射到内容上，
/// 因此调用方仍然可以像操作 Label 一样写 <c>card.Text = "…"</c> / <c>card.ForeColor = …</c>。
/// </para>
/// </summary>
internal sealed class MetricCard : Control
{
    private const int PadX = 15;
    private const int PadY = 12;
    private const int CaptionToValue = 4;
    private const int ValueToHint = 1;
    private const float DotSize = 7F;

    private string _value = "—";
    private string _caption = "";
    private string _unit = "";
    private string _hint = "";

    public MetricCard()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Theme.PageBg;
        ForeColor = Theme.TextStrong;
        Height = Height1Line;
    }

    /// <summary>带底部说明文字时的卡片建议高度（够放 19pt 大字 + 两行小字）。</summary>
    public const int Height1Line = 100;

    /// <summary>没有底部说明文字时的卡片建议高度。</summary>
    public const int Height2Line = 80;

    /// <summary>小标题（卡片上方那行灰字）。</summary>
    public string Caption
    {
        get => _caption;
        set
        {
            _caption = value ?? "";
            Invalidate();
        }
    }

    /// <summary>主内容。等价于 <see cref="Control.Text"/>。</summary>
    public string Value
    {
        get => _value;
        set
        {
            var v = value ?? "";
            if (_value == v) return;
            _value = v;
            Invalidate();
        }
    }

    /// <summary>主内容后面的小单位（如「天」「张」）。</summary>
    public string Unit
    {
        get => _unit;
        set
        {
            _unit = value ?? "";
            Invalidate();
        }
    }

    /// <summary>底部的一行补充说明。</summary>
    public string Hint
    {
        get => _hint;
        set
        {
            _hint = value ?? "";
            Invalidate();
        }
    }

    /// <summary>左上角小圆点的颜色；会跟 <see cref="Control.ForeColor"/> 联动。</summary>
    public Color DotColor { get; set; } = Theme.TextFaint;

    /// <summary>与 <see cref="Value"/> 同义，方便沿用原来的 Label 写法。</summary>
    [AllowNull]
    public override string Text
    {
        get => _value;
        set => Value = value ?? string.Empty;
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        DotColor = ForeColor;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        Theme.ClearBackdrop(g, this);

        Theme.DrawCard(g, new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F));

        var captionH = Theme.Small.Height;
        var valueH = Theme.Metric.Height;
        var hintH = Theme.Small.Height;
        var hasHint = !string.IsNullOrEmpty(_hint);

        // 先把内容总高算出来，再整体垂直居中 —— 这样卡片给多高都不会出现"上面挤、下面空"
        var contentH = captionH + CaptionToValue + valueH + (hasHint ? ValueToHint + hintH : 0);
        var top = Math.Max(2, (Height - contentH) / 2);

        var captionY = top;
        var valueY = captionY + captionH + CaptionToValue;

        // ---- 小圆点 + 小标题 ----
        if (!string.IsNullOrEmpty(_caption))
        {
            var dotCx = PadX + DotSize / 2F;
            var dotCy = captionY + captionH / 2F;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var dot = new SolidBrush(DotColor))
            {
                g.FillEllipse(dot, dotCx - DotSize / 2F, dotCy - DotSize / 2F, DotSize, DotSize);
            }

            TextRenderer.DrawText(
                g,
                _caption,
                Theme.Small,
                new Point(PadX + (int)DotSize + 6, captionY),
                Theme.TextMuted,
                TextFormatFlags.NoPadding);
        }

        // ---- 大数字/大字 ----
        TextRenderer.DrawText(g, _value, Theme.Metric, new Point(PadX, valueY), ForeColor, TextFormatFlags.NoPadding);

        if (!string.IsNullOrEmpty(_unit))
        {
            var valueWidth = TextRenderer.MeasureText(
                g,
                _value,
                Theme.Metric,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding).Width;

            // 单位与大字底部对齐，视觉上像一条基线
            var unitY = valueY + valueH - Theme.Base.Height - 2;

            TextRenderer.DrawText(
                g,
                _unit,
                Theme.Base,
                new Point(PadX + valueWidth + 6, unitY),
                Theme.TextMuted,
                TextFormatFlags.NoPadding);
        }

        // ---- 底部说明 ----
        if (hasHint)
        {
            TextRenderer.DrawText(
                g,
                _hint,
                Theme.Small,
                new Point(PadX, valueY + valueH + ValueToHint),
                Theme.TextFaint,
                TextFormatFlags.NoPadding);
        }
    }
}
