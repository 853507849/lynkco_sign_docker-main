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
/// 矩形版会把文字硬塞进给定高度里，24pt 的字塞进 30px 的框会把字形底部裁掉 ——
/// 「已」看起来会变成「コ」。按点绘制则始终按字体自身的行高排版，不会裁剪。
/// </para>
///
/// <para>
/// 高度不写死：<see cref="Height1Line"/> 由字体行高 + 内边距现算。
/// 字号一改、DPI 一换，卡片自己跟着长，不用回来重新量一个魔法数字。
/// </para>
///
/// <para>
/// 三种外观：白卡（默认）、<see cref="Tint"/> 染色卡（今日签到按状态染绿/染红）、
/// <see cref="Dark"/> 深色卡（积分余额是四张里唯一"数据最重"的那张，用深色压住视线）。
/// </para>
/// </summary>
internal sealed class MetricCard : Control
{
    private const int PadX = 18;
    private const int PadY = 16;
    private const int CaptionToValue = 10;
    private const int ValueToHint = 6;

    private string _value = "—";
    private string _caption = "";
    private string _unit = "";
    private string _hint = "";
    private string _badge = "";
    private string _corner = "";
    private bool _dark;

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

    /// <summary>带底部说明文字时的卡片高度。</summary>
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

    /// <summary>右上角的小徽标（如补签卡的「充足」）。空字符串 = 不画。</summary>
    public string Badge
    {
        get => _badge;
        set
        {
            _badge = value ?? "";
            Invalidate();
        }
    }

    /// <summary>徽标的语气色（默认警示橙）。</summary>
    public Color BadgeTone { get; set; } = Theme.Warning;

    /// <summary>右上角的一枚淡色小字（如今日签到时间「08:00」）。空字符串 = 不画。</summary>
    public string Corner
    {
        get => _corner;
        set
        {
            _corner = value ?? "";
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

    /// <summary>深色卡（积分余额）。深色时标题/说明自动换成浅色。</summary>
    public bool Dark
    {
        get => _dark;
        set
        {
            _dark = value;
            Invalidate();
        }
    }

    /// <summary>右下角的小柱状走势（传 null 则不画）。长度建议 8~14。</summary>
    public int[]? Spark { get; set; }

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

        if (Dark)
        {
            Theme.DrawCardFilled(g, r, Theme.CardRadius, Theme.RailBg, Theme.DarkBorder);
        }
        else if (Tint is { } tint)
        {
            Theme.DrawCardFilled(g, r, Theme.CardRadius, tint, TintBorder ?? Theme.CardBorder);
        }
        else
        {
            Theme.DrawCard(g, r);
        }

        var captionColor = Dark ? Theme.OnDarkMuted : Theme.TextMuted;
        var hintColor = Dark ? Theme.OnDarkFaint : Theme.TextFaint;
        var valueColor = Dark ? Theme.OnDark : ForeColor;

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
                g, _caption, Theme.Small, new Point(PadX, captionY), captionColor, TextFormatFlags.NoPadding);
        }

        // ---- 右上角徽标 ----
        if (!string.IsNullOrEmpty(_badge))
        {
            var w = TextRenderer.MeasureText(
                _badge, Theme.Small, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + 14;

            var badgeRect = new RectangleF(Width - PadX - w, captionY - 4, w, Theme.Small.Height + 6);
            Theme.FillRound(g, badgeRect, 6F, Dark ? Color.FromArgb(0x2A, 0x24, 0x18) : Theme.WarningSoft);
            Theme.DrawRound(g, badgeRect, 6F, Dark ? Color.FromArgb(0x3A, 0x31, 0x22) : Theme.WarningBorder);

            TextRenderer.DrawText(
                g, _badge, Theme.Small, Rectangle.Round(badgeRect),
                Dark ? Theme.LogWarn : BadgeTone,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // ---- 右上角的小字（时间）----
        if (!string.IsNullOrEmpty(_corner))
        {
            TextRenderer.DrawText(
                g, _corner, Theme.MonoSmall,
                new Rectangle(PadX, captionY - 1, Width - PadX * 2, Theme.Small.Height + 2),
                Dark ? Theme.OnDarkFaint : Theme.TextFaint,
                TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }

        // ---- 大数字/大字 ----
        TextRenderer.DrawText(g, _value, Theme.Metric, new Point(PadX, valueY), valueColor, TextFormatFlags.NoPadding);

        var valueWidth = TextRenderer.MeasureText(
            g, _value, Theme.Metric, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;

        if (!string.IsNullOrEmpty(_unit))
        {
            // 单位与大字底部对齐，视觉上像一条基线
            var unitY = valueY + valueH - Theme.Base.Height - 2;

            TextRenderer.DrawText(
                g, _unit, Theme.Base, new Point(PadX + valueWidth + 6, unitY),
                Dark ? Theme.OnDarkMuted : Theme.TextMuted, TextFormatFlags.NoPadding);
        }

        // ---- 走势小柱 ----
        if (Spark is { Length: > 1 } spark)
        {
            DrawSpark(g, spark, valueY + valueH - 22, 22);
        }

        // ---- 底部说明 ----
        if (hasHint)
        {
            TextRenderer.DrawText(
                g, _hint, Theme.Small, new Point(PadX, valueY + valueH + ValueToHint),
                hintColor, TextFormatFlags.NoPadding);
        }
    }

    /// <summary>
    /// 右下角的迷你柱状图：只表达"最近几次的趋势"，没有坐标轴、没有数值。
    /// 用最少的数据墨水换一个"这是在波动"的直觉，超出卡片情绪的部分一律不画。
    /// </summary>
    private void DrawSpark(Graphics g, int[] values, float bottom, float height)
    {
        // 只画最近 7 个点：点数再多就退化成"条形码"，趋势反而看不出来。
        if (values.Length > 7) values = values[^7..];

        const int barWidth = 4;
        const int gap = 5;

        var max = values.Max();
        if (max <= 0) max = 1;

        var total = values.Length * barWidth + (values.Length - 1) * gap;
        var x = Width - PadX - total;

        g.SmoothingMode = SmoothingMode.AntiAlias;

        var baseColor = Dark ? Theme.AccentLight : Theme.Mix(Theme.AccentLight, Color.White, 0.55);
        var hotColor = Dark ? Theme.AccentLight : Theme.Accent;

        for (var i = 0; i < values.Length; i++)
        {
            var h = Math.Max(3F, height * values[i] / max);
            var color = i == values.Length - 1 ? hotColor : baseColor;

            Theme.FillRound(g, new RectangleF(x, bottom - h, barWidth, h), 1.5F, color);
            x += barWidth + gap;
        }
    }
}
