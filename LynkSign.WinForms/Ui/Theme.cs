using System.Drawing.Drawing2D;

namespace LynkSign.WinForms.Ui;

/// <summary>
/// 全局视觉规范：配色、字体、圆角与常用绘制原语。
///
/// <para>
/// 整个界面只从这里取色取字体，改一处就全变 —— 避免颜色散落在各个 Build 方法里
/// 慢慢跑偏（这也是原生 WinForms 界面显旧的根源：每个控件各用各的系统默认色）。
/// </para>
///
/// <para>
/// 全部为自绘，不依赖任何第三方 UI 包（本项目坚持零 UI 依赖，见 csproj 注释）。
/// </para>
/// </summary>
internal static class Theme
{
    // ---------- 页面与卡片 ----------
    public static readonly Color PageBg = Color.FromArgb(0xF3, 0xF5, 0xF8);
    public static readonly Color CardBg = Color.White;
    public static readonly Color CardBorder = Color.FromArgb(0xE6, 0xE9, 0xEF);
    public static readonly Color Divider = Color.FromArgb(0xEF, 0xF1, 0xF5);

    // ---------- 文字 ----------
    public static readonly Color TextStrong = Color.FromArgb(0x1C, 0x22, 0x30);
    public static readonly Color TextBody = Color.FromArgb(0x33, 0x3A, 0x47);
    public static readonly Color TextMuted = Color.FromArgb(0x6C, 0x76, 0x86);
    public static readonly Color TextFaint = Color.FromArgb(0x9B, 0xA3, 0xB0);

    // ---------- 主题色 ----------
    public static readonly Color Accent = Color.FromArgb(0x2F, 0x6F, 0xED);
    public static readonly Color AccentHover = Color.FromArgb(0x24, 0x5B, 0xD0);
    public static readonly Color AccentDown = Color.FromArgb(0x1D, 0x4A, 0xAB);
    public static readonly Color AccentSoft = Color.FromArgb(0xEA, 0xF1, 0xFE);

    // ---------- 语义色 ----------
    public static readonly Color Success = Color.FromArgb(0x16, 0xA3, 0x4A);
    public static readonly Color SuccessSoft = Color.FromArgb(0xE8, 0xF7, 0xEE);
    public static readonly Color Danger = Color.FromArgb(0xDC, 0x26, 0x26);
    public static readonly Color DangerSoft = Color.FromArgb(0xFD, 0xED, 0xED);
    public static readonly Color Warning = Color.FromArgb(0xD9, 0x77, 0x06);
    public static readonly Color WarningSoft = Color.FromArgb(0xFD, 0xF4, 0xE4);

    // ---------- 日志面板（深色终端风） ----------
    public static readonly Color LogBg = Color.FromArgb(0x1B, 0x21, 0x30);
    public static readonly Color LogHeaderBg = Color.FromArgb(0x23, 0x2B, 0x3C);
    public static readonly Color LogText = Color.FromArgb(0xC4, 0xCC, 0xDA);
    public static readonly Color LogErr = Color.FromArgb(0xFF, 0x8A, 0x80);
    public static readonly Color LogWarn = Color.FromArgb(0xFF, 0xCF, 0x6B);
    public static readonly Color LogOk = Color.FromArgb(0x6E, 0xE7, 0xA0);
    public static readonly Color LogInfo = Color.FromArgb(0x7F, 0xB6, 0xFF);
    public static readonly Color LogFaint = Color.FromArgb(0x6B, 0x76, 0x8C);

    // ---------- 字体 ----------
    public const string UiFamily = "Microsoft YaHei UI";

    public static readonly Font Base = new(UiFamily, 9F);
    public static readonly Font BaseBold = new(UiFamily, 9F, FontStyle.Bold);
    public static readonly Font Small = new(UiFamily, 8.25F);
    public static readonly Font SmallBold = new(UiFamily, 8.25F, FontStyle.Bold);
    public static readonly Font Title = new(UiFamily, 11F, FontStyle.Bold);
    public static readonly Font Section = new(UiFamily, 9.75F, FontStyle.Bold);
    public static readonly Font Metric = new(UiFamily, 19F, FontStyle.Bold);
    public static readonly Font Log = new("Consolas", 9F);

    // ---------- 间距 ----------
    public const int CardPadding = 16;
    public const int CardRadius = 10;
    public const int FieldRadius = 8;
    public const int ButtonRadius = 8;
    public const int Gap = 12;

    // ==================================================================
    //  绘制原语
    // ==================================================================

    /// <summary>
    /// 把整块客户区刷成底色。<b>所有自绘控件的 OnPaint 第一步都要调它。</b>
    ///
    /// <para>
    /// 自绘控件都开了 <c>ControlStyles.OptimizedDoubleBuffer</c>。开了之后
    /// OnPaint 拿到的 Graphics 指向的是一块<b>缓冲区</b>，而 WinForms 只会把
    /// 「本次失效的那一小块区域」在缓冲区里重画一遍，缓冲区其余部分保留的是
    /// 上一次的内容、甚至是没初始化过的内存。这块缓冲区最后会被整块 blit 到屏幕，
    /// 于是失效区域之外就露出了残留像素 —— 表现为：
    /// </para>
    ///
    /// <list type="bullet">
    ///   <item>圆角控件四个角落出现黑色/彩色楔形（缓冲区初始值是 0 = 黑）；</item>
    ///   <item>控件移动过之后，旧位置的白框、旧文字还印在原地（重影）；</item>
    ///   <item>不画底色的控件（如幽灵按钮）整块都是别的控件的残影。</item>
    /// </list>
    ///
    /// <para>
    /// 这个坑很阴：<c>Control.DrawToBitmap</c> 走的是 <c>WM_PRINT</c>，
    /// <b>不经过缓冲区</b>，所以离屏截图看着完全正常，只有真机上才现形。
    /// </para>
    ///
    /// <para>
    /// 对策就是每帧先无视现有裁剪、把整个 ClientRectangle 刷成底色，再往上画形状。
    /// </para>
    /// </summary>
    /// <param name="g">当前绘制上下文。</param>
    /// <param name="control">要刷底的控件（取它的 ClientRectangle 与底色）。</param>
    /// <param name="color">指定底色；不传则用控件自身 BackColor，透明时向上取父容器底色。</param>
    public static void ClearBackdrop(Graphics g, Control control, Color? color = null)
    {
        var back = color ?? control.BackColor;

        // 透明底色（如自绘在卡片上的胶囊）要落到父容器的实际底色上，
        // 否则会刷成 Color.Transparent 对应的黑。
        if (back == Color.Transparent)
        {
            back = control.Parent is { } p && p.BackColor != Color.Transparent
                ? p.BackColor
                : PageBg;
        }

        // 关键：把裁剪区换成整个客户区（Replace），这样 Clear 才能覆盖整块缓冲区，
        // 而不是只覆盖当前失效的那一小块。
        g.SetClip(control.ClientRectangle, CombineMode.Replace);
        g.Clear(back);
    }

    /// <summary>构造圆角矩形路径。半径会自动收敛到不超过短边的一半，避免小尺寸画崩。</summary>
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath();

        if (r.Width <= 0 || r.Height <= 0)
        {
            path.AddRectangle(r);
            return path;
        }

        var d = Math.Min(radius * 2F, Math.Min(r.Width, r.Height));
        if (d <= 0.5F)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, RectangleF r, float radius, Color fill)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Round(r, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
    }

    /// <summary>描边圆角矩形。为了 1px 线不发虚，自动内缩半个线宽。</summary>
    public static void DrawRound(Graphics g, RectangleF r, float radius, Color stroke, float width = 1F)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var inset = width / 2F;
        var rr = RectangleF.Inflate(r, -inset, -inset);
        using var path = Round(rr, Math.Max(0F, radius - inset));
        using var pen = new Pen(stroke, width);
        g.DrawPath(pen, path);
    }

    /// <summary>卡片：白底 + 圆角 + 淡边框。界面上所有内容块都走这个。</summary>
    public static void DrawCard(Graphics g, RectangleF r, Color? border = null, Color? fill = null)
        => DrawCardFilled(g, r, CardRadius, fill ?? CardBg, border ?? CardBorder);

    public static void DrawCardFilled(Graphics g, RectangleF r, float radius, Color fill, Color border)
    {
        FillRound(g, r, radius, fill);
        DrawRound(g, r, radius, border);
    }

    /// <summary>左侧竖起的小色条，用于给卡片标题一个视觉锚点。</summary>
    public static void DrawAccentBar(Graphics g, float x, float y, float height, Color color, float width = 3F)
        => FillRound(g, new RectangleF(x, y, width, height), width / 2F, color);
}
