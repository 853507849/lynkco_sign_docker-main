using System.Diagnostics;
using System.Drawing.Drawing2D;
using LynkSign.Infrastructure;
using LynkSign.Lynk;
using LynkSign.WinForms.App;
using LynkSign.WinForms.Ui;
using Microsoft.Extensions.Hosting;

namespace LynkSign.WinForms.Forms;

/// <summary>
/// 主窗口：左侧导航 + 四张页面（运行状态 / 签到进行中 / 签到记录 / 设置）+ 托盘常驻。
///
/// <para>
/// 界面全部用代码搭建，没有 .Designer.cs / .resx。
/// 一是省掉设计器文件与手写代码互相覆盖的老问题，
/// 二是窗口结构能一眼看全（谁 Dock 在哪、控件叫什么）。
/// </para>
///
/// <para>
/// 外观一律走 <c>Ui/</c> 下那套自绘控件与 <see cref="Theme"/>：
/// 原生 WinForms 控件的字体、配色、边框都跟着系统主题走，凑在一起就是"系统默认脸"，
/// 所以卡片、按钮、输入框、导航全部重画（仍然零第三方 UI 依赖）。
/// </para>
///
/// <para>
/// 业务逻辑完全不在这里 —— 该文件只负责「把 <see cref="AppHost"/> 里那套
/// 与原项目共用的服务接到控件上」。真正的签到流程仍然跑在原项目的 SignJob 里。
/// </para>
/// </summary>
internal sealed partial class MainForm : Form
{
    private const int MaxLogLines = 3000;
    private const int LogTrimStep = 800;

    private const int CardGap = 12;
    private const int StatusBarHeight = 30;

    private readonly AppHost _host;
    private readonly bool _startMinimized;

    /// <summary>
    /// 是否读写窗口尺寸偏好。离屏截图时为 <c>false</c> ——
    /// 否则截出来的图尺寸会随"这台机器上用户拖过多大"变化，布局体检就没法比对了。
    /// </summary>
    private readonly bool _rememberWindowSize;

    /// <summary>
    /// 最近一次「非最大化」状态下的客户区尺寸（设备像素）。
    /// 最大化时 <see cref="Control.ClientSize"/> 等于屏幕大小，不是用户调的尺寸，
    /// 退出时得靠这个字段还原。
    /// </summary>
    private Size _lastNormalClientSize = Size.Empty;

    // ----- 日志：接管 Console.Out，并做批量刷新（避免高频 AppendText 闪烁）-----
    private readonly UiLogWriter _logWriter;
    private readonly TextWriter _originalOut;
    private readonly Queue<string> _logQueue = new();
    private readonly System.Windows.Forms.Timer _logTimer = new() { Interval = 120 };

    // ----- 定时刷新状态 -----
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 2000 };

    // ----- 调度 -----
    private CancellationTokenSource? _schedulerCts;

    // ----- 骨架 -----
    private NavRail _nav = null!;
    private Panel _pageHost = null!;
    private Panel _pageStatus = null!;
    private Panel _pageRunning = null!;
    private Panel _pageHistory = null!;
    private Panel _pageSettings = null!;

    /// <summary>设置页的滚动内容画布：高度等于整页内容高度，供 <c>--screenshot</c> 整页渲染用。</summary>
    private TableLayoutPanel _settingsCanvas = null!;

    // ----- 状态页 -----
    private PageHeader _hdrStatus = null!;
    private StatusPill _pillState = null!;
    private IconButton _btnRefresh = null!;
    private MetricCard _cardToday = null!;
    private MetricCard _cardStreak = null!;
    private MetricCard _cardCards = null!;
    private MetricCard _cardPoints = null!;

    private Label _valNext = null!;
    private Label _valRefresh = null!;
    private Label _valLast = null!;
    private ChipLabel _planCountdown = null!;
    private CardPanel _planCard = null!;

    private AccountCard _accountCard = null!;
    private RichTextBox _logBox = null!;
    private Label _logPath = null!;
    private CardPanel _logCard = null!;

    // ----- 签到进行中页 -----
    private PageHeader _hdrRunning = null!;
    private StatusPill _pillRun = null!;
    private FlatButton _btnCancel = null!;
    private FlatButton _btnRunBusy = null!;
    private ThinProgress _runProgress = null!;
    private Label _runStepText = null!;
    private Label _runEta = null!;
    private StepList _steps = null!;
    private RichTextBox _runLogBox = null!;

    /// <summary>「签到进行中」页运行输出面板的空态提示（有真实输出后就藏起来）。</summary>
    private Label _runLogEmpty = null!;
    private CancellationTokenSource? _runCts;
    private DateTime _runStartedAt;
    private int _runStep;

    /// <summary>一次运行里出现过的步骤序号（用来点亮步骤清单）。</summary>
    private readonly HashSet<int> _runStepsSeen = [];

    // ----- 签到记录页 -----
    private PageHeader _hdrHistory = null!;
    private MonthGrid _calendar = null!;
    private Label _calSummary = null!;
    private Label _calMakeup = null!;
    private Label _statBig = null!;
    private Label _statRange = null!;
    private Label _statRate = null!;
    private Label _statCards = null!;
    private ThinProgress _statProgress = null!;
    private RecordList _recentList = null!;
    private CardPanel _calendarCard = null!;
    private DateTime _monthCursor = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    // ----- 设置页：账号 / 凭证 -----
    private InputField _txtRefreshToken = null!;
    private FlatCheckBox _chkShowToken = null!;
    private InputField _txtDeviceId = null!;
    private FlatButton _btnImportHar = null!;

    /// <summary>导入结果的临时回显（写在按钮左边，几秒后自动清空）。</summary>
    private Label _importEcho = null!;

    /// <summary>回显的清空计时器（第一次用时才建）。</summary>
    private System.Windows.Forms.Timer? _echoTimer;

    private InputField _txtCaNativeKey = null!;
    private InputField _txtCaNativeSecret = null!;
    private FlatCheckBox _chkShowNativeSecret = null!;
    private FlatButton _btnVerifyNative = null!;

    // ----- 设置页：网络 -----
    private SegmentedControl _segProxy = null!;
    private InputField _txtHttpProxy = null!;
    private ChipLabel _proxyState = null!;

    // ----- 设置页：调度 / 分享 / 推送 -----
    private InputField _txtCron = null!;
    private Label _cronPreview = null!;
    private ToggleSwitch _chkAutostart = null!;

    private InputField _txtContentId = null!;
    private ToggleSwitch _chkAutoShare = null!;
    private InputField _txtTokenB = null!;
    private ToggleSwitch _chkAutoArticleId = null!;
    private FlatButton _btnFetchArticle = null!;
    private Label _articlePreview = null!;
    private ToggleSwitch _chkOpenShareUrl = null!;
    private ToggleSwitch _chkOpenShareAlways = null!;

    private InputField _txtWecom = null!;
    private InputField _txtDingtalk = null!;
    private InputField _txtFeishu = null!;
    private InputField _txtTgBotToken = null!;
    private InputField _txtTgChatId = null!;
    private InputField _txtServerChan = null!;
    private InputField _txtPushPlus = null!;
    private InputField _txtBark = null!;

    // ----- 设置页：数据 -----
    private Label _dataDirValue = null!;
    private Label _saveHint = null!;
    private FlatButton _btnSave = null!;

    // ----- 其它 -----
    private FlatButton _btnRunNow = null!;
    private FlatButton _btnRunNowRunning = null!;
    private Label _sbDataDir = null!;
    private Label _sbState = null!;

    private NotifyIcon _tray = null!;
    private ToolStripMenuItem _trayAutostart = null!;

    // ----- 状态位 -----
    private bool _loadingConfig;
    private bool _reallyExit;
    private bool _trayHintShown;
    private string _configPath = "";

    /// <param name="host">已装配好的服务容器。</param>
    /// <param name="startMinimized">启动后直接缩到托盘（开机自启用）。</param>
    /// <param name="rememberWindowSize">
    /// 是否沿用上次的窗口尺寸（默认是）。只有离屏截图那条路会传 <c>false</c>。
    /// </param>
    public MainForm(AppHost host, bool startMinimized, bool rememberWindowSize = true)
    {
        _host = host;
        _startMinimized = startMinimized;
        _rememberWindowSize = rememberWindowSize;

        BuildUi();
        BuildTray();

        // 把 Console 输出接管到日志框：原项目的 RunLogWriter（文件 + 控制台）
        // 与 ConsoleJobLogger（仅控制台）都会流到这里，零改动获得实时日志。
        _logWriter = new UiLogWriter(EnqueueLog);
        _originalOut = Console.Out;
        Console.SetOut(_logWriter);

        _logTimer.Tick += (_, _) => FlushLogQueue();
        _logTimer.Start();

        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        SizeChanged += (_, _) => TrackNormalSize();

        Load += OnFormLoad;
        FormClosing += OnFormClosing;
    }

    // ==================================================================
    //  界面搭建
    // ==================================================================

    private void BuildUi()
    {
        SuspendLayout();

        Text = Program.AppTitle;
        Icon = LoadAppIcon();
        StartPosition = FormStartPosition.CenterScreen;

        // 构造期的尺寸。配置页卡片较多，早先的 1000x760 一进来底部几张卡就被视口裁掉。
        //
        // 注意：AutoScaleMode.Dpi 并不会缩放这个值（没有设计器基准，实测控件的
        // 单行 TextBox 高度在任何 DPI 下都是 23px）。所以它只是「不读偏好文件」
        // 那条路径（离屏截图）的兜底；真实启动一律由 RestoreWindowState 落地，
        // 那边会按 DPI 换算，保证高 DPI 屏上窗口不会只占屏幕一小块。
        ClientSize = new Size(UiStateStore.DefaultWidth, UiStateStore.DefaultHeight);
        MinimumSize = new Size(UiStateStore.MinWidth, UiStateStore.MinHeight);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Theme.Base;
        BackColor = Theme.PageBg;

        _nav = new NavRail();
        _nav.AddItem(NavIcon.Dashboard, "运行状态");
        _nav.AddItem(NavIcon.Check, "签到进行中");
        _nav.AddItem(NavIcon.Calendar, "签到记录");
        _nav.AddItem(NavIcon.Settings, "设置");
        _nav.SelectedIndexChanged += (_, _) => ShowPage(_nav.SelectedIndex);

        _pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.PageBg };

        _pageStatus = BuildStatusPage();
        _pageRunning = BuildRunningPage();
        _pageHistory = BuildHistoryPage();
        _pageSettings = BuildSettingsPage();

        foreach (var page in new[] { _pageStatus, _pageRunning, _pageHistory, _pageSettings })
        {
            page.Visible = false;
            _pageHost.Controls.Add(page);
        }

        _pageStatus.Visible = true;

        var statusBar = BuildStatusBar();

        // Dock 顺序：先加的填剩余空间，后加的优先占边。
        // 导航栏最后加 → 它先占走整条左边，底部状态栏再从剩下的宽度里取一条。
        Controls.Add(_pageHost);
        Controls.Add(statusBar);
        Controls.Add(_nav);

        ResumeLayout(performLayout: true);
    }

    private void ShowPage(int index)
    {
        var target = index switch
        {
            0 => _pageStatus,
            1 => _pageRunning,
            2 => _pageHistory,
            _ => _pageSettings,
        };

        foreach (var page in new[] { _pageStatus, _pageRunning, _pageHistory, _pageSettings })
        {
            page.Visible = ReferenceEquals(page, target);
        }

        target.BringToFront();

        // 「运行输出」那个 RichEdit 是在父页面还不可见的时候建出来的，
        // 这种控件会把内容攒着不画：页面切过来之后必须主动踢一次重绘，
        // 否则看到的是一整块空的深色面板（内容其实一直在，复制也能复制出来）。
        if (ReferenceEquals(target, _pageRunning) && _runLogBox is not null)
        {
            _runLogBox.SelectionStart = _runLogBox.TextLength;
            _runLogBox.ScrollToCaret();
            _runLogBox.Invalidate();
            _runLogBox.Refresh();
        }

        if (ReferenceEquals(target, _pageHistory)) RefreshHistory();
    }

    /// <summary>供 <c>--screenshot</c> 离屏渲染时切换页面（会触发正常的页面切换逻辑）。</summary>
    internal void SelectTab(int index) => _nav.SelectedIndex = index;

    /// <summary>当前页面序号（0 运行状态 / 1 签到进行中 / 2 签到记录 / 3 设置）。</summary>
    internal int CurrentPage => _nav.SelectedIndex;

    // ---------- 底部状态栏 ----------

    private Control BuildStatusBar()
    {
        var bar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = StatusBarHeight,
            BackColor = Theme.PageBg,
            Padding = new Padding(Theme.PadX, 0, Theme.PadX, 0),
        };

        bar.Paint += (_, e) => Theme.DrawHairline(e.Graphics, 0, 0, bar.Width, Theme.Divider);

        var line = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.PageBg,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _sbDataDir = new Label
        {
            Text = "数据目录：—",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.TextFaint,
            BackColor = Color.Transparent,
            Font = Theme.MonoSmall,
            AutoEllipsis = true,
            Margin = Padding.Empty,
        };

        _sbState = new Label
        {
            Text = "就绪",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Theme.TextFaint,
            BackColor = Color.Transparent,
            Font = Theme.MonoSmall,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(12, 0, 0, 0),
        };

        line.Controls.Add(_sbDataDir, 0, 0);
        line.Controls.Add(_sbState, 1, 0);

        bar.Controls.Add(line);
        return bar;
    }

    // ==================================================================
    //  状态页（运行状态）
    // ==================================================================

    private Panel BuildStatusPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PageBg,
            Padding = new Padding(Theme.PadX, 0, Theme.PadX, 14),
        };

        _hdrStatus = new PageHeader { Title = "运行状态" };

        // 操作区是 RightToLeft 流：先加的在最右边，所以顺序是「立即签到 → 刷新 → 状态胶囊」，
        // 视觉上从左到右正好是「胶囊 / 图标 / 主按钮」。
        _btnRunNow = new FlatButton { Text = "立即签到", Kind = ButtonKind.Primary, Margin = Padding.Empty };
        _btnRunNow.Click += OnRunNowClick;
        _hdrStatus.Actions.Controls.Add(_btnRunNow);

        _btnRefresh = new IconButton { Icon = GlyphIcon.Refresh, Margin = new Padding(8, 0, 8, 0) };
        _btnRefresh.Click += (_, _) => OnRefreshStatusClick();
        _hdrStatus.Actions.Controls.Add(_btnRefresh);

        _pillState = new StatusPill { Text = "空闲 · 等待调度", Margin = Padding.Empty };
        _pillState.DotTone = Theme.Success;
        _hdrStatus.Actions.Controls.Add(_pillState);

        // 运行中时主按钮换成"签到中…"（禁用）；与设计稿第 2 页一致，
        // 但这里保留同一个控件，只切换文字与可用状态 —— 两个按钮换来换去容易漏掉
        // 其中一处的事件绑定。
        _btnRunNowRunning = _btnRunNow;

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.PageBg,
            Margin = Padding.Empty,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, MetricCard.Height1Line + CardGap));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        stack.Controls.Add(BuildMetricRow(), 0, 0);
        stack.Controls.Add(BuildStatusBody(), 0, 1);

        page.Controls.Add(stack);
        page.Controls.Add(_hdrStatus);
        return page;
    }

    private Control BuildMetricRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Theme.PageBg,
            Margin = new Padding(0, 0, 0, CardGap),
        };

        for (var i = 0; i < 4; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));

        _cardToday = new MetricCard
        {
            Caption = "今日签到",
            Value = "—",
            Hint = "数据源 day/info",
            Tint = Theme.SuccessSoft,
            TintBorder = Theme.SuccessBorder,
        };

        _cardStreak = new MetricCard
        {
            Caption = "连续签到",
            Value = "—",
            Unit = "天",
            Hint = "来自最近一次运行",
        };

        _cardCards = new MetricCard
        {
            Caption = "补签卡",
            Value = "—",
            Unit = "张",
            Hint = "可用于补签",
        };

        _cardPoints = new MetricCard
        {
            Caption = "积分余额",
            Value = "—",
            Hint = "累计 — · 成长 —",
            Dark = true,
        };

        MetricCard[] cards = [_cardToday, _cardStreak, _cardCards, _cardPoints];

        for (var i = 0; i < cards.Length; i++)
        {
            cards[i].Dock = DockStyle.Fill;
            cards[i].Margin = new Padding(i == 0 ? 0 : CardGap / 2, 0, i == cards.Length - 1 ? 0 : CardGap / 2, 0);
            row.Controls.Add(cards[i], i, 0);
        }

        return row;
    }

    /// <summary>
    /// 状态页下半部分：左列「运行计划 + 账户」，右列「运行日志」。
    ///
    /// <para>
    /// 左列 42%：计划卡只有三行字，账户卡是深色的数字块，两块叠起来正好与右边那张
    /// 通高的日志卡齐平。日志是这页最该被"持续盯着"的东西，给它最大的面积。
    /// </para>
    /// </summary>
    private Control BuildStatusBody()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.PageBg,
            Margin = Padding.Empty,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.PageBg,
            Margin = new Padding(0, 0, CardGap / 2, 0),
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, PlanCardHeight + CardGap));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var plan = BuildPlanCard();
        plan.Margin = new Padding(0, 0, 0, 0);

        _accountCard = new AccountCard { Dock = DockStyle.Fill, Margin = Padding.Empty };

        left.Controls.Add(plan, 0, 0);
        left.Controls.Add(_accountCard, 0, 1);

        var log = BuildLogCard();
        log.Margin = new Padding(CardGap / 2, 0, 0, 0);

        body.Controls.Add(left, 0, 0);
        body.Controls.Add(log, 1, 0);
        return body;
    }

    /// <summary>
    /// 计划卡的高度：标题带 + 三行"标签 / 值" + 一枚倒计时胶囊 + 上下内边距。
    /// 按字体行高现算，不写死 —— 写死的数字只在编写它的那台机器、那个缩放下成立。
    /// </summary>
    private static int PlanCardHeight =>
        CardPanel.TitleHeight + 18 * 2 + (Theme.Base.Height + 16) * 3 + 10 + 26;

    private Control BuildPlanCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Title = "运行计划",
            Margin = Padding.Empty,
            Padding = new Padding(Theme.CardPadding, 18, Theme.CardPadding, 14),
        };

        card.Note = "cron —";
        _planCard = card;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = Theme.CardBg,
            Margin = Padding.Empty,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));

        _valNext = NewPlanValue(Theme.TextStrong, Theme.Mono);
        _valRefresh = NewPlanValue(Theme.TextBody, Theme.Mono);
        _valLast = NewPlanValue(Theme.TextMuted, Theme.Mono);

        grid.Controls.Add(NewCaptionLabel("下次运行"), 0, 0);
        grid.Controls.Add(_valNext, 1, 0);
        grid.Controls.Add(NewCaptionLabel("状态刷新"), 0, 1);
        grid.Controls.Add(_valRefresh, 1, 1);
        grid.Controls.Add(NewCaptionLabel("最近运行"), 0, 2);
        grid.Controls.Add(_valLast, 1, 2);

        for (var i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Base.Height + 16));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        card.Controls.Add(grid);

        _planCountdown = new ChipLabel { Caption = "距下次签到 —", Dock = DockStyle.Bottom, Margin = Padding.Empty };
        card.Controls.Add(_planCountdown);

        return card;
    }

    private static Label NewPlanValue(Color color, Font font) => new()
    {
        Text = "—",
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleRight,
        Font = font,
        ForeColor = color,
        BackColor = Color.Transparent,
        AutoEllipsis = true,
        Margin = Padding.Empty,
    };

    /// <summary>卡片里的"小标题"：比正文小一号、淡一档，只用来给右边的值贴标签。</summary>
    private static Label NewCaptionLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = Theme.Small,
        ForeColor = Theme.TextMuted,
        BackColor = Color.Transparent,
        Margin = Padding.Empty,
    };

    private Control BuildLogCard()
    {
        // 状态页的日志卡是白卡（设计稿 01）：它和「运行计划」并排，两块得是同一种面，
        // 否则右半边像嵌了个终端窗口，整页重心就歪了。深色终端留给「签到进行中」——
        // 那页本来就是在看实时输出，深底压得住滚动的字。
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Title = "运行日志",
            Note = "实时",
            Fill = Theme.LogBg,
            BorderColor = Theme.LogBorder,
            Padding = new Padding(18, 14, 12, 8),
            Margin = Padding.Empty,
        };

        _logCard = card;

        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.LogBg,
            ForeColor = Theme.LogText,
            Font = Theme.Log,
            DetectUrls = false,
            WordWrap = true,
            // ForcedVertical 而不是 None：滚动能力要留着（滚轮、光标跟随、ScrollToCaret
            // 都依赖它），只是把 RichEdit 自己画的那根滚动条推到宿主之外换掉。
            ScrollBars = RichTextBoxScrollBars.ForcedVertical,
            HideSelection = false,
        };

        var bar = new SlimScrollBar(Theme.ScrollThumb, Theme.ScrollThumbHover)
        {
            BackColor = Theme.LogBg,
        };

        var host = new ScrollHost(_logBox, bar);
        bar.Attach(_logBox);

        // 底部一行：日志文件名 + 两个文字动作。放在卡片里而不是页脚，
        // 因为它们只对"这块日志"有意义，挂到页脚就变成了全局动作。
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Theme.LogBg };

        _logPath = new Label
        {
            Text = "logs/—",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.MonoSmall,
            ForeColor = Theme.LogFaint,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.LogBg,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
        };

        actions.Controls.Add(NewLogLink("打开目录", (_, _) => OpenPath(_host.Logs.Dir)));
        actions.Controls.Add(NewLogLink("复制全部", (_, _) => CopyLog()));

        footer.Controls.Add(_logPath);
        footer.Controls.Add(actions);

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.LogBg,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        stack.Controls.Add(host, 0, 0);
        stack.Controls.Add(footer, 0, 1);

        card.Controls.Add(stack);
        return card;
    }

    private static LinkLabel NewLogLink(string text, LinkLabelLinkClickedEventHandler onClick)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            Font = Theme.Small,
            LinkColor = Theme.TextMuted,
            ActiveLinkColor = Theme.Accent,
            VisitedLinkColor = Theme.TextMuted,
            LinkBehavior = LinkBehavior.HoverUnderline,
            BackColor = Color.Transparent,
            Margin = new Padding(14, 0, 0, 0),
            TabStop = false,
        };

        link.LinkClicked += onClick;
        return link;
    }

    private void CopyLog()
    {
        try
        {
            if (_logBox.TextLength > 0) Clipboard.SetText(_logBox.Text);
            EnqueueLog("[INFO] 日志已复制到剪贴板");
        }
        catch (Exception ex)
        {
            EnqueueLog($"[!] 复制失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 「多行文本 + 右侧细滚动条」的组合容器。
    ///
    /// <para>
    /// 不用 <c>Dock</c> 排这两个子控件：Dock 的处理顺序跟着控件集合的 z-order 走，
    /// 先加谁后加谁在不同 WinForms 版本上表现并不一致（Fill 有时会把 Dock=Right
    /// 的位置一起吞掉）。这里自己算两个矩形，行为完全确定。
    /// </para>
    /// </summary>
    private sealed class ScrollHost : Panel
    {
        /// <summary>文本与滚动条之间留的缝，避免文字贴着滑块。</summary>
        private const int Gap = 2;

        private readonly Control _box;
        private readonly Control _bar;

        public ScrollHost(Control box, Control bar)
        {
            // 必须自己 Dock=Fill：不写的话这个 Panel 保持默认的 200x100，
            // 文本框会被挤成一条 —— 而且布局体检还查不出来（子控件比父容器小，不算越界）。
            Dock = DockStyle.Fill;
            BackColor = box.BackColor;
            Margin = Padding.Empty;

            _box = box;
            _bar = bar;

            // 位置全部由 OnLayout 算，所以要把 TextBox 上可能存在的 Dock 清掉：
            // Dock=Fill 的控件每次都会被子容器的布局引擎重新铺满，把我们算好的边界顶掉。
            box.Dock = DockStyle.None;

            Controls.Add(box);
            Controls.Add(bar);

            // 文本框现在是"加宽到宿主之外"的（见 OnLayout），会盖住右边那 10px。
            // Controls.Add 是往集合末尾追加，而集合下标 0 才是层叠最上面 ——
            // 不显式提到最前的话，细条会被文本框整个压住，界面上就一根条都看不见了。
            bar.BringToFront();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);

            var barWidth = Math.Min(_bar.Width, Width);
            _bar.SetBounds(Width - barWidth, 0, barWidth, Height);

            // 关键：不再去"藏"RichEdit 的原生滚动条（它在滚轮、按键、重排时总会自己装回来，
            // 拦消息永远有漏网，表现就是滚一下冒出第二根条）。改成把它整个推到宿主外面：
            //   1) 先按宿主宽度摆一次，量出原生条实际吃掉多少像素（DPI 变了也准）；
            //   2) 再把窗口加宽整整一根条的宽度 —— 文字区仍然是 Width，
            //      而原生条落在 [Width, Width+pad]，超出宿主可视区，被父窗口裁掉。
            _box.SetBounds(0, 0, Width, Height);

            var pad = _box.IsHandleCreated ? Math.Max(0, _box.Width - _box.ClientSize.Width) : 0;
            if (pad <= 0) pad = SystemInformation.VerticalScrollBarWidth;

            _box.SetBounds(0, 0, Width + pad, Height);

            // 文字区现在铺满到 Width，右边 10px 被自绘细条压着 —— 用右内边距把文字收回来
            if (_box.IsHandleCreated) NativeScroll.SetRightMargin(_box.Handle, barWidth + Gap);
        }
    }

    // ==================================================================
    //  托盘 / 关闭
    // ==================================================================

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("打开主界面");
        openItem.Click += (_, _) => RestoreFromTray();

        var runItem = new ToolStripMenuItem("立即签到");
        runItem.Click += (_, _) => OnRunNowClick(null, EventArgs.Empty);

        _trayAutostart = new ToolStripMenuItem("开机自动启动") { CheckOnClick = false };
        _trayAutostart.Click += OnTrayAutostartClick;

        var dataItem = new ToolStripMenuItem("打开数据目录");
        dataItem.Click += (_, _) => OpenPath(AppPaths.WorkDir);

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            _reallyExit = true;
            Close();
        };

        menu.Items.AddRange([openItem, runItem, new ToolStripSeparator(), _trayAutostart, dataItem, new ToolStripSeparator(), exitItem]);

        _tray = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = Program.AppTitle,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private static Icon? LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            return exe is null ? null : Icon.ExtractAssociatedIcon(exe);
        }
        catch
        {
            return null;
        }
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            try
            {
                _tray.ShowBalloonTip(2500, Program.AppTitle, "程序已缩到托盘继续后台运行，双击图标可重新打开。", ToolTipIcon.Info);
            }
            catch
            {
                // 某些系统禁用了气泡提示，忽略
            }
        }
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // 用户点右上角 × ：缩到托盘继续跑；只有托盘菜单「退出」才真的结束进程
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            // 顺手记一次尺寸：用户可能从此就缩在托盘里，再也不会走到下面那个真退出分支
            SaveWindowState();
            HideToTray();
            return;
        }

        SaveWindowState();
        Shutdown();
    }

    private void Shutdown()
    {
        _statusTimer.Stop();
        _logTimer.Stop();

        try
        {
            _runCts?.Cancel();
            _schedulerCts?.Cancel();
            ((IHostedService)_host.Scheduler)
                .StopAsync(CancellationToken.None)
                .Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // 停不干净也无所谓，进程马上就没了
        }

        _logWriter.FlushPending();

        try
        {
            Console.SetOut(_originalOut);
        }
        catch
        {
            // 恢复失败不影响退出
        }

        _tray.Visible = false;
        _tray.Dispose();
    }

    // ==================================================================
    //  日志
    // ==================================================================

    private void EnqueueLog(string line)
    {
        // 只存裸文本：着色要等到真正写进某个文本框时才算 ——
        // 状态页的日志卡是白底、进行中页的输出是深底，同一行在两处的颜色不一样。
        lock (_logQueue)
        {
            _logQueue.Enqueue(line);
            // 极端情况下（重定向循环等）兜住内存：只保留最近 500 条待刷
            while (_logQueue.Count > 500) _logQueue.Dequeue();
        }

        // 顺手把「签到状态」那一行摘给顶部状态卡片 —— 手动触发和 cron 触发都能覆盖到，
        // 不必再让 SignJob 额外回传结构化结果（那样就得改原项目了）。
        var match = StatusPattern.Match(line);
        if (match.Success)
        {
            ApplySignStatus(match);

            if (match.Groups[1].Value == "已") RecordSignedToday(int.TryParse(match.Groups[2].Value, out var n) ? n : 0);
        }

        // 步骤行（[3/5] ...）驱动「签到进行中」页的进度
        if (StepPattern.Match(line) is { Success: true } step)
        {
            OnStepSeen(int.Parse(step.Groups[1].Value), step.Groups[2].Value.Trim());
        }

        // 账户行同理，喂「账户积分」那块。先用 Contains 粗筛一次：
        // 一次运行能刷出上百行日志，而每行都跑一遍正则纯属浪费。
        if (line.Contains(AccountLine.Prefix, StringComparison.Ordinal)
            && AccountLine.Parse(line) is { } account)
        {
            ApplyAccount(account);
        }
    }

    private void FlushLogQueue()
    {
        List<string> batch;

        lock (_logQueue)
        {
            if (_logQueue.Count == 0) return;
            batch = [.. _logQueue];
            _logQueue.Clear();
        }

        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => AppendBatch(batch));
            }
            catch (InvalidOperationException)
            {
                // 窗口正在销毁
            }
            return;
        }

        AppendBatch(batch);
    }

    private void AppendBatch(List<string> batch)
    {
        if (IsDisposed) return;

        // 两块日志框（状态页 / 进行中页）吃同一批数据：设计上它们看的是同一份输出，
        // 分成两份是为了各自排版，而不是各记一套状态。
        AppendTo(_logBox, batch, dark: false);

        if (_runLogBox is not null)
        {
            AppendTo(_runLogBox, batch, dark: true);

            // 一有真实输出就把空态提示收起来（判断依据是文本框里真的有字，不是"跑过一次"）
            if (_runLogEmpty is not null) _runLogEmpty.Visible = _runLogBox.TextLength == 0;
        }

        if (_logCard is not null)
        {
            _logCard.Note = $"实时 · {_logBox.GetLineFromCharIndex(_logBox.TextLength)} 条";
        }
    }

    private static void AppendTo(RichTextBox box, List<string> lines, bool dark)
    {
        box.SuspendLayout();
        try
        {
            foreach (var text in lines)
            {
                box.SelectionStart = box.TextLength;
                box.SelectionLength = 0;
                box.SelectionColor = LogColor(text, dark);
                box.AppendText(text + Environment.NewLine);
            }

            box.SelectionColor = box.ForeColor;

            // 超长裁剪：按字符区间删除。用 Lines 赋值会把剩余行的着色一并清掉，所以走 Select+删除。
            if (box.GetLineFromCharIndex(box.TextLength) > MaxLogLines)
            {
                var cutAt = box.GetFirstCharIndexFromLine(LogTrimStep);
                if (cutAt > 0)
                {
                    box.Select(0, cutAt);
                    box.SelectedText = string.Empty;
                }
            }

            box.SelectionStart = box.TextLength;
            box.ScrollToCaret();
        }
        finally
        {
            box.ResumeLayout();
        }
    }

    /// <summary>
    /// 日志着色。<paramref name="dark"/> 决定用哪套提示色：
    /// 深底上要提亮（否则红绿全糊成黑），白底上要压暗（否则刺眼）。
    /// </summary>
    private static Color LogColor(string line, bool dark)
    {
        if (line.Contains("[X]", StringComparison.Ordinal)) return dark ? Theme.TermErr : Theme.LogErr;
        if (line.Contains("[!]", StringComparison.Ordinal)) return dark ? Theme.TermWarn : Theme.LogWarn;
        if (line.Contains("[OK]", StringComparison.Ordinal)) return dark ? Theme.TermOk : Theme.LogOk;
        if (line.Contains("[INFO]", StringComparison.Ordinal)) return dark ? Theme.TermInfo : Theme.LogInfo;
        return dark ? Theme.TermText : Theme.LogText;
    }

    private static void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打不开目录：\n{path}\n\n{ex.Message}", Program.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
