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
/// 矩形版会把文字硬塞进给定高度里，20pt 的字塞进 30px 的框会把字形底部裁掉 ——
/// 「已」看起来会变成「コ」。按点绘制则始终按字体自身的行高排版，不会裁剪。
/// 整个内容块再按实际行高算出总高、在卡片内垂直居中，这样改字号也不会画崩。
/// </para>
///
/// <para>
/// 高度不写死：<see cref="Height1Line"/> 由字体行高 + 内边距现算。
/// 字号一改、DPI 一换，卡片自己跟着长，不用回来重新量一个魔法数字。
/// </para>
///
/// <para>
/// <see cref="Text"/> 与 <see cref="Control.ForeColor"/> 被映射到内容上，
/// 因此调用方仍然可以像操作 Label 一样写 <c>card.Text = "…"</c> / <c>card.ForeColor = …</c>。
/// </para>
/// </summary>
internal sealed class MetricCard : Control
{
    private const int PadX = 18;
    private const int PadY = 16;
    private const int CaptionToValue = 8;
    private const int ValueToHint = 5;

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

    /// <summary>带底部说明文字时的卡片高度（够放 20pt 大字 + 两行小字）。</summary>
    public static int Height1Line =>
        PadY * 2 + Theme.Small.Height + CaptionToValue + Theme.Metric.Height + ValueToHint + Theme.Small.Height;

    /// <summary>没有底部说明文字时的卡片高度。</summary>
    public static int Height2Line =>
        PadY * 2 + Theme.Small.Height + CaptionToValue + Theme.Metric.Height;

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

    private Color? _tint;

    /// <summary>
    /// 卡片底色。<c>null</c> = 白卡；指定颜色则整块染成该色（如"今天已签到"用淡绿），
    /// 让一张卡在四张里自己站出来，而不用靠加粗边框或图标去抢。
    /// </summary>
    public Color? Tint
    {
        get => _tint;
        set
        {
            _tint = value;
            Invalidate();
        }
    }

    /// <summary>染色卡片的描边色；不指定则用默认淡灰。</summary>
    public Color? TintBorder { get; set; }

    /// <summary>与 <see cref="Value"/> 同义，方便沿用原来的 Label 写法。</summary>
    [AllowNull]
    public override string Text
    {
        get => _value;
        set => Value = value ?? string.Empty;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        Theme.ClearBackdrop(g, this);

        var r = new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F);

        if (Tint is { } tint)
        {
            Theme.DrawCardFilled(g, r, Theme.CardRadius, tint, TintBorder ?? Theme.CardBorder);
        }
        else
        {
            Theme.DrawCard(g, r);
        }

        var captionH = Theme.Small.Height;
        var valueH = Theme.Metric.Height;
        var hintH = Theme.Small.Height;
        var hasHint = !string.IsNullOrEmpty(_hint);

        // 先把内容总高算出来，再整体垂直居中 —— 这样卡片给多高都不会出现"上面挤、下面空"
        var contentH = captionH + CaptionToValue + valueH + (hasHint ? ValueToHint + hintH : 0);
        var top = Math.Max(2, (Height - contentH) / 2);

        var captionY = top;
        var valueY = captionY + captionH + CaptionToValue;

        // ---- 小标题 ----
        if (!string.IsNullOrEmpty(_caption))
        {
            TextRenderer.DrawText(
                g,
                _caption,
                Theme.Small,
                new Point(PadX, captionY),
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
