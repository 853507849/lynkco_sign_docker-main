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
/// 设计语言：<b>墨黑 + 钴蓝</b>。三档中性灰承担 95% 的面积（页面 / 卡片 / 描边），
/// 只有「主操作」与「当前状态」才允许出现彩色；彩色一旦到处都是，就没人看得见重点了。
/// 阴影一律不用 —— 一块 0.5px 的冷灰细边 + 一档背景差，在 150% 缩放的屏幕上比
/// 任何 box-shadow 都干净（钝阴影会糊成一圈脏边）。
/// </para>
///
/// <para>
/// 全部为自绘，不依赖任何第三方 UI 包（本项目坚持零 UI 依赖，见 csproj 注释）。
/// </para>
/// </summary>
internal static class Theme
{
    // ---------- 表面（从外到内三层） ----------
    /// <summary>顶栏 / 页签 / 页脚的外壳底色。与内容区拉开一档，形成"框"。</summary>
    public static readonly Color ChromeBg = Color.FromArgb(0xFF, 0xFF, 0xFF);

    /// <summary>内容区画布：比纯白冷一档，卡片浮在上面才立得住。</summary>
    public static readonly Color PageBg = Color.FromArgb(0xF5, 0xF6, 0xF9);

    public static readonly Color CardBg = Color.White;

    /// <summary>输入框的填充底色（无边框，靠底色分隔）。</summary>
    public static readonly Color FieldBg = Color.FromArgb(0xF3, 0xF5, 0xF9);

    public static readonly Color CardBorder = Color.FromArgb(0xE7, 0xEA, 0xF0);
    public static readonly Color Divider = Color.FromArgb(0xEC, 0xEE, 0xF3);
    public static readonly Color HoverBg = Color.FromArgb(0xF1, 0xF3, 0xF7);
    public static readonly Color PressBg = Color.FromArgb(0xE7, 0xEA, 0xF1);

    /// <summary>弱操作按钮（页脚那几颗「打开目录 / 重新载入」）的常态底色。</summary>
    public static readonly Color ChipBg = Color.FromArgb(0xF4, 0xF6, 0xFB);
    public static readonly Color ChipHover = Color.FromArgb(0xE9, 0xED, 0xF6);
    public static readonly Color ChipBorder = Color.FromArgb(0xE3, 0xE8, 0xF1);
    public static readonly Color ChipBorderHover = Color.FromArgb(0xCF, 0xD7, 0xE6);

    // ---------- 文字（四档，从重到轻） ----------
    public static readonly Color TextStrong = Color.FromArgb(0x0F, 0x13, 0x19);
    public static readonly Color TextBody = Color.FromArgb(0x3A, 0x41, 0x4D);
    public static readonly Color TextMuted = Color.FromArgb(0x6B, 0x74, 0x84);
    public static readonly Color TextFaint = Color.FromArgb(0x98, 0xA0, 0xAE);

    // ---------- 主题色：钴蓝 ----------
    // 比原来的 #2452D8 亮一档、饱和度略降：深钴蓝在浅灰画布上偏"企业后台"，
    // 亮一档之后与白卡、冷灰边的关系更松，整屏看着不那么紧绷。
    public static readonly Color Accent = Color.FromArgb(0x3B, 0x6E, 0xF5);
    public static readonly Color AccentLight = Color.FromArgb(0x5E, 0x8B, 0xF8);
    public static readonly Color AccentHover = Color.FromArgb(0x2F, 0x5E, 0xE3);
    public static readonly Color AccentHoverLight = Color.FromArgb(0x4E, 0x7D, 0xF6);
    public static readonly Color AccentDown = Color.FromArgb(0x25, 0x49, 0xBA);
    public static readonly Color AccentSoft = Color.FromArgb(0xEF, 0xF3, 0xFE);

    // ---------- 语义色 ----------
    public static readonly Color Success = Color.FromArgb(0x0E, 0x9B, 0x6C);
    public static readonly Color SuccessSoft = Color.FromArgb(0xE8, 0xF7, 0xF0);
    public static readonly Color SuccessBorder = Color.FromArgb(0xC2, 0xE8, 0xD8);
    public static readonly Color Danger = Color.FromArgb(0xD8, 0x39, 0x3F);
    public static readonly Color DangerSoft = Color.FromArgb(0xFD, 0xEC, 0xEC);
    public static readonly Color DangerBorder = Color.FromArgb(0xF6, 0xC9, 0xC9);
    public static readonly Color Warning = Color.FromArgb(0xC0, 0x79, 0x0A);
    public static readonly Color WarningSoft = Color.FromArgb(0xFC, 0xF3, 0xE2);
    public static readonly Color WarningBorder = Color.FromArgb(0xF2, 0xDD, 0xB6);

    // ---------- 日志面板（深色终端风，整块卡片都是它） ----------
    public static readonly Color LogBg = Color.FromArgb(0x0F, 0x13, 0x1A);
    public static readonly Color LogBorder = Color.FromArgb(0x1C, 0x22, 0x2E);
    public static readonly Color LogText = Color.FromArgb(0xC2, 0xCA, 0xD6);
    public static readonly Color LogErr = Color.FromArgb(0xFF, 0x90, 0x86);
    public static readonly Color LogWarn = Color.FromArgb(0xFF, 0xCC, 0x6E);
    public static readonly Color LogOk = Color.FromArgb(0x74, 0xE0, 0xA4);
    public static readonly Color LogInfo = Color.FromArgb(0x8F, 0xB9, 0xFF);
    public static readonly Color LogFaint = Color.FromArgb(0x5F, 0x69, 0x78);

    // ---------- 自绘滚动条 ----------
    // 原生滚动条是一根 17px 的白轨道 + 三颗灰箭头，压在深色日志卡上像贴了块胶布。
    // 自绘之后只剩一根 6px 圆角滑块，浅色卡片与深色卡片各一套。
    public static readonly Color ScrollThumb = Color.FromArgb(0xD3, 0xD9, 0xE4);
    public static readonly Color ScrollThumbHover = Color.FromArgb(0xB5, 0xBE, 0xCE);
    public static readonly Color ScrollThumbDark = Color.FromArgb(0x2E, 0x38, 0x4A);
    public static readonly Color ScrollThumbDarkHover = Color.FromArgb(0x44, 0x51, 0x68);

    // ---------- 字体 ----------
    public const string UiFamily = "Microsoft YaHei UI";

    public static readonly Font Base = new(UiFamily, 9F);
    public static readonly Font BaseBold = new(UiFamily, 9F, FontStyle.Bold);
    public static readonly Font Small = new(UiFamily, 8.25F);
    public static readonly Font SmallBold = new(UiFamily, 8.25F, FontStyle.Bold);
    public static readonly Font Title = new(UiFamily, 12F, FontStyle.Bold);
    public static readonly Font Section = new(UiFamily, 9.75F, FontStyle.Bold);
    public static readonly Font Meter = new(UiFamily, 12F, FontStyle.Bold);
    public static readonly Font Metric = new(UiFamily, 20F, FontStyle.Bold);
    public static readonly Font Log = new("Consolas", 8.75F);

    // ---------- 间距与圆角 ----------
    public const int CardPadding = 18;
    public const int CardRadius = 14;
    public const int FieldRadius = 9;
    public const int ButtonRadius = 9;
    public const int Gap = 14;

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

    /// <summary>
    /// 竖向渐变的圆角矩形。
    ///
    /// <para>
    /// 主按钮靠它做出一点"实体感"：上沿亮一档、下沿沉一档，看起来像一块有厚度的色块，
    /// 而不是一张贴在界面上的纯色纸片。渐变跨度刻意做得很小（同色系相差不到 10%），
    /// 大到能看出明暗就会显脏 —— 那种 2010 年前后的立体按钮正是这么来的。
    /// </para>
    /// </summary>
    public static void FillRoundGradient(Graphics g, RectangleF r, float radius, Color top, Color bottom)
    {
        if (r.Width <= 0.5F || r.Height <= 0.5F) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Round(r, radius);
        using var brush = new LinearGradientBrush(
            new PointF(r.X, r.Y),
            new PointF(r.X, r.Bottom),
            top,
            bottom);
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

    /// <summary>一条 1px 分隔线（对齐到像素中心，避免发虚）。</summary>
    public static void DrawHairline(Graphics g, float x1, float y, float x2, Color color)
    {
        using var pen = new Pen(color, 1F);
        g.DrawLine(pen, x1, y + 0.5F, x2, y + 0.5F);
    }

    /// <summary>这块底色算不算"深色"—— 决定卡片标题用浅字还是深字。</summary>
    public static bool IsDark(Color c)
        => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;
}
