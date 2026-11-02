using System.Drawing.Drawing2D;
using System.Drawing.Text;

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
/// 设计语言：<b>墨绿主色 + 深色导航栏</b>。深色侧栏（<see cref="RailBg"/>）把导航与内容
/// 分成两块，内容区三档中性灰承担 95% 的面积（页面 / 卡片 / 描边）；
/// 只有「主操作」与「当前状态」才允许出现彩色 —— 彩色一旦到处都是，就没人看得见重点了。
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
    /// <summary>左侧导航栏底色。整屏唯一的深色面，负责"把导航和内容分开"。</summary>
    public static readonly Color RailBg = Color.FromArgb(0x10, 0x17, 0x20);
    public static readonly Color RailItemHover = Color.FromArgb(0x1A, 0x24, 0x2E);
    public static readonly Color RailIcon = Color.FromArgb(0x7E, 0x8E, 0x9E);
    public static readonly Color RailDivider = Color.FromArgb(0x22, 0x2C, 0x36);

    /// <summary>内容区画布：比纯白冷一档，卡片浮在上面才立得住。</summary>
    public static readonly Color PageBg = Color.FromArgb(0xF6, 0xF7, 0xFA);

    public static readonly Color CardBg = Color.White;

    /// <summary>输入框的填充底色（无边框，靠底色分隔）。</summary>
    public static readonly Color FieldBg = Color.FromArgb(0xF3, 0xF5, 0xF9);

    public static readonly Color CardBorder = Color.FromArgb(0xE4, 0xE9, 0xF1);
    public static readonly Color Divider = Color.FromArgb(0xEC, 0xEF, 0xF4);
    public static readonly Color HoverBg = Color.FromArgb(0xF1, 0xF3, 0xF7);
    public static readonly Color PressBg = Color.FromArgb(0xE7, 0xEA, 0xF1);

    /// <summary>外壳底色（滚动条槽、页脚等）。</summary>
    public static readonly Color ChromeBg = PageBg;

    /// <summary>弱操作按钮（幽灵按钮）的常态底色。</summary>
    public static readonly Color ChipBg = Color.FromArgb(0xF4, 0xF6, 0xFA);
    public static readonly Color ChipHover = Color.FromArgb(0xEB, 0xEF, 0xF6);
    public static readonly Color ChipBorder = Color.FromArgb(0xE4, 0xE9, 0xF1);
    public static readonly Color ChipBorderHover = Color.FromArgb(0xCF, 0xD7, 0xE6);

    // ---------- 文字（四档，从重到轻） ----------
    public static readonly Color TextStrong = Color.FromArgb(0x00, 0x14, 0x1C);
    public static readonly Color TextBody = Color.FromArgb(0x35, 0x42, 0x4D);
    public static readonly Color TextMuted = Color.FromArgb(0x7E, 0x8E, 0x9E);
    public static readonly Color TextFaint = Color.FromArgb(0xA6, 0xAF, 0xBA);

    // ---------- 主题色：墨绿 ----------
    public static readonly Color Accent = Color.FromArgb(0x0E, 0x9F, 0x84);
    public static readonly Color AccentLight = Color.FromArgb(0x12, 0xB0, 0xA4);
    public static readonly Color AccentHover = Color.FromArgb(0x0C, 0x8C, 0x75);
    public static readonly Color AccentHoverLight = Color.FromArgb(0x11, 0xA0, 0x95);
    public static readonly Color AccentDown = Color.FromArgb(0x0A, 0x76, 0x63);
    public static readonly Color AccentSoft = Color.FromArgb(0xE9, 0xF6, 0xF2);
    public static readonly Color AccentSoftBorder = Color.FromArgb(0xCB, 0xE9, 0xE1);

    // ---------- 语义色 ----------
    public static readonly Color Success = Color.FromArgb(0x0E, 0x9F, 0x84);
    public static readonly Color SuccessSoft = Color.FromArgb(0xED, 0xF8, 0xF4);
    public static readonly Color SuccessBorder = Color.FromArgb(0xCD, 0xEB, 0xE0);
    public static readonly Color Danger = Color.FromArgb(0xA6, 0x52, 0x3E);
    public static readonly Color DangerSoft = Color.FromArgb(0xF9, 0xEC, 0xE8);
    public static readonly Color DangerBorder = Color.FromArgb(0xEF, 0xD5, 0xCC);
    public static readonly Color Warning = Color.FromArgb(0xA9, 0x77, 0x2A);
    public static readonly Color WarningSoft = Color.FromArgb(0xFD, 0xF4, 0xE3);
    public static readonly Color WarningBorder = Color.FromArgb(0xF3, 0xE2, 0xBE);

    /// <summary>深色卡片（侧栏同色系）上用的文字。</summary>
    public static readonly Color OnDark = Color.FromArgb(0xEE, 0xF2, 0xF5);
    public static readonly Color OnDarkMuted = Color.FromArgb(0x8C, 0x9A, 0xA7);
    public static readonly Color OnDarkFaint = Color.FromArgb(0x5B, 0x69, 0x77);

    /// <summary>深色面上的描边（深色卡片 / 深色日志块共用）。</summary>
    public static readonly Color DarkBorder = Color.FromArgb(0x1B, 0x24, 0x30);

    // ---------- 日志面板：两套皮肤 ----------
    // 设计稿里同一份输出在两页上是两种底：状态页的「运行日志」是白卡，
    // 「签到进行中」的「运行输出」是深色终端。所以配色必须成对给 ——
    // 只留一套 LogText 的话，换底之后要么白字白底、要么黑字黑底。
    // 白卡皮肤（运行状态）
    public static readonly Color LogBg = Color.FromArgb(0xFB, 0xFC, 0xFD);
    public static readonly Color LogBorder = Color.FromArgb(0xE4, 0xE9, 0xF1);
    public static readonly Color LogText = Color.FromArgb(0x1F, 0x2A, 0x37);
    public static readonly Color LogErr = Color.FromArgb(0xA6, 0x52, 0x3E);
    public static readonly Color LogWarn = Color.FromArgb(0xA9, 0x77, 0x2A);
    public static readonly Color LogOk = Color.FromArgb(0x0E, 0x9F, 0x84);
    public static readonly Color LogInfo = Color.FromArgb(0x6B, 0x76, 0x82);
    public static readonly Color LogFaint = Color.FromArgb(0x9C, 0xA6, 0xB2);

    // 深色终端皮肤（签到进行中）
    public static readonly Color TermBg = Color.FromArgb(0x10, 0x17, 0x20);
    public static readonly Color TermBorder = Color.FromArgb(0x1B, 0x24, 0x30);
    public static readonly Color TermText = Color.FromArgb(0xB9, 0xC4, 0xD0);
    public static readonly Color TermErr = Color.FromArgb(0xFF, 0x9C, 0x8A);
    public static readonly Color TermWarn = Color.FromArgb(0xF0, 0xC0, 0x69);
    public static readonly Color TermOk = Color.FromArgb(0x57, 0xD9, 0xB8);
    public static readonly Color TermInfo = Color.FromArgb(0x8F, 0xB9, 0xFF);
    public static readonly Color TermFaint = Color.FromArgb(0x5F, 0x69, 0x78);

    // ---------- 自绘滚动条 ----------
    public static readonly Color ScrollThumb = Color.FromArgb(0xD3, 0xD9, 0xE4);
    public static readonly Color ScrollThumbHover = Color.FromArgb(0xB5, 0xBE, 0xCE);
    public static readonly Color ScrollThumbDark = Color.FromArgb(0x2E, 0x38, 0x4A);
    public static readonly Color ScrollThumbDarkHover = Color.FromArgb(0x44, 0x51, 0x68);

    // ---------- 字体 ----------
    // 设计稿用的是 Noto Sans SC / Inter Tight / JetBrains Mono。前两个在这台机器上
    // 不一定装了，所以按"有就用、没有就退到系统自带"的顺序挑一次，挑完缓存住 ——
    // 每次 new Font 都去枚举已安装字体会让界面启动慢几百毫秒。
    public const string UiFamily = "Microsoft YaHei UI";

    /// <summary>界面正文字族：优先 Noto Sans SC（设计稿），否则微软雅黑。</summary>
    public static readonly string SansFamily = PickFamily(
        ["Noto Sans SC", "Microsoft YaHei UI", "Segoe UI"], UiFamily);

    /// <summary>数字字族：优先 Inter Tight（数字更紧凑、字形更挺拔）。</summary>
    public static readonly string NumberFamily = PickFamily(
        ["Inter Tight", "Inter", "Segoe UI", "Microsoft YaHei UI"], "Segoe UI");

    /// <summary>等宽字族：优先 JetBrains Mono（设计稿），退到 Cascadia / Consolas。</summary>
    public static readonly string MonoFamily = PickFamily(
        ["JetBrains Mono", "Cascadia Mono", "Consolas", "Courier New"], "Consolas");

    public static readonly Font Base = new(SansFamily, 9F);
    public static readonly Font BaseBold = new(SansFamily, 9F, FontStyle.Bold);
    public static readonly Font Small = new(SansFamily, 8.25F);
    public static readonly Font SmallBold = new(SansFamily, 8.25F, FontStyle.Bold);
    public static readonly Font Title = new(SansFamily, 14F, FontStyle.Bold);
    public static readonly Font Section = new(SansFamily, 10F, FontStyle.Bold);
    public static readonly Font Meter = new(NumberFamily, 12F, FontStyle.Bold);
    public static readonly Font Metric = new(NumberFamily, 21F, FontStyle.Bold);
    public static readonly Font Log = new(MonoFamily, 8.25F);

    /// <summary>页面大标题（"运行状态"这一行）。</summary>
    public static readonly Font H1 = new(SansFamily, 16F, FontStyle.Bold);

    /// <summary>数字专用（积分余额那种 32px 级的巨号字）。</summary>
    public static readonly Font Display = new(NumberFamily, 24F, FontStyle.Bold);

    /// <summary>时间戳 / 路径 / cron 这类机器文本。</summary>
    public static readonly Font Mono = new(MonoFamily, 8.25F);
    public static readonly Font MonoSmall = new(MonoFamily, 7.5F);

    // ---------- 间距与圆角（对应设计稿的 6/8/10/12/99 圆角阶梯） ----------
    public const int PagePadding = 24;
    public const int CardPadding = 20;
    public const int CardRadius = 12;
    public const int FieldRadius = 8;
    public const int ButtonRadius = 10;
    public const int ChipRadius = 6;
    public const int Gap = 12;

    /// <summary>页面左右留白（卡片与标题共用同一条左边缘）。</summary>
    public const int PadX = 24;

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

    /// <summary>两个颜色按比例混合（0 = a，1 = b）。用于淡色底上的细边。</summary>
    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>在已安装字体里挑第一个存在的；都没有就用兜底。</summary>
    private static string PickFamily(string[] candidates, string fallback)
    {
        try
        {
            using var installed = new InstalledFontCollection();
            var names = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                if (names.Contains(candidate)) return candidate;
            }
        }
        catch
        {
            // 枚举字体失败（极简系统 / 权限受限）就用兜底，不影响启动
        }

        return fallback;
    }
}
