using System.Diagnostics;
using System.Drawing.Drawing2D;
using LynkSign.Infrastructure;
using LynkSign.Lynk;
using LynkSign.WinForms.App;
using LynkSign.WinForms.Ui;
using Microsoft.Extensions.Hosting;

namespace LynkSign.WinForms.Forms;

/// <summary>
/// 主窗口：状态卡片 + 配置表单 + 实时日志 + 托盘常驻。
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
/// 所以卡片、按钮、输入框、页签全部重画（仍然零第三方 UI 依赖）。
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

    private const int HeaderHeight = 62;
    private const int PagePadding = 20;
    private const int CardGap = 12;

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
    private readonly Queue<(string Text, Color Color)> _logQueue = new();
    private readonly System.Windows.Forms.Timer _logTimer = new() { Interval = 120 };

    // ----- 定时刷新状态 -----
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 2000 };

    // ----- 调度 -----
    private CancellationTokenSource? _schedulerCts;

    // ----- 骨架 -----
    private TabStrip _tabs = null!;
    private Panel _pageHost = null!;
    private Panel _tabStatus = null!;
    private Panel _tabConfig = null!;

    /// <summary>配置页的滚动内容画布：高度等于整页内容高度，供 <c>--screenshot</c> 整页渲染用。</summary>
    private TableLayoutPanel _configCanvas = null!;

    // ----- 状态页：四张指标卡 -----
    private MetricCard _cardToday = null!;
    private MetricCard _cardStreak = null!;
    private MetricCard _cardCards = null!;
    private MetricCard _cardRuntime = null!;

    // ----- 状态页：运行计划 -----
    private Label _valNext = null!;
    private Label _valRefresh = null!;
    private Label _valLast = null!;

    // ----- 状态页：账户积分（四个值来自日志里的账户行，见 AccountLine） -----
    private Label _valPoint = null!;
    private Label _valIncome = null!;
    private Label _valLevel = null!;
    private Label _valGrowth = null!;

    // ----- 状态页：积分变化（读数据目录的 points-history.log） -----
    private RichTextBox _pointsBox = null!;

    /// <summary>
    /// 上次读到的积分历史文件指纹（时间戳 ^ 长度）。
    /// 状态刷新是每 2 秒一轮，靠它判断「文件没动就别重读、别重绘」—— 否则每 2 秒
    /// 清空重画一次列表，用户滚动到一半会被弹回顶部。
    /// </summary>
    private long _pointsStamp = long.MinValue;

    // ----- 标题栏状态胶囊 -----
    private StatusPill _pillState = null!;

    // ----- 配置页：账号 / 调度 -----
    private InputField _txtRefreshToken = null!;
    private FlatCheckBox _chkShowToken = null!;
    private InputField _txtDeviceId = null!;

    /// <summary>「从抓包导入」按钮：选一个 .har，自动抠出 refreshToken + deviceId。</summary>
    private FlatButton _btnImportHar = null!;

    /// <summary>上一次导入的结果（导的是哪个账号，或失败原因）。</summary>
    private Label _harPreview = null!;

    private InputField _txtCron = null!;
    private Label _cronPreview = null!;

    // ----- 配置页：账号 —— 原生通道（签到写入专用） -----
    private InputField _txtCaNativeKey = null!;
    private InputField _txtCaNativeSecret = null!;
    private FlatCheckBox _chkShowNativeSecret = null!;
    private FlatButton _btnVerifyNative = null!;

    // ----- 配置页：分享任务 -----
    private InputField _txtContentId = null!;
    private FlatCheckBox _chkAutoShare = null!;
    private InputField _txtTokenB = null!;

    /// <summary>是否每次运行都去广场拉最新一篇文章的 id（勾选 = <c>share_content_mode=auto</c>）。</summary>
    private FlatCheckBox _chkAutoArticleId = null!;

    /// <summary>「拉取一次」按钮：当场验证文章流接口通不通、能取到哪一篇。</summary>
    private FlatButton _btnFetchArticle = null!;

    /// <summary>上一次拉取的结果（文章标题 / id，或失败原因）。</summary>
    private Label _articlePreview = null!;

    /// <summary>签到后是否自动用系统默认浏览器打开分享链接。</summary>
    private FlatCheckBox _chkOpenShareUrl = null!;

    /// <summary>上一条的「加强档」：cron 定时触发时也打开（不勾则只在点「立即签到」时打开）。</summary>
    private FlatCheckBox _chkOpenShareAlways = null!;

    // ----- 配置页：推送 -----
    private InputField _txtWecom = null!;
    private InputField _txtDingtalk = null!;
    private InputField _txtFeishu = null!;
    private InputField _txtTgBotToken = null!;
    private InputField _txtTgChatId = null!;
    private InputField _txtServerChan = null!;
    private InputField _txtPushPlus = null!;
    private InputField _txtBark = null!;

    /// <summary>出站代理设置（留空 = 直连并忽略系统代理，语义见 <c>HttpClientSetup</c>）。</summary>
    private InputField _txtHttpProxy = null!;

    // ----- 其它 -----
    private RichTextBox _logBox = null!;
    private FlatButton _btnRunNow = null!;
    private FlatButton _btnSave = null!;
    private FlatCheckBox _chkAutostart = null!;
    private ToolStripStatusLabel _sbDataDir = null!;
    private ToolStripStatusLabel _sbState = null!;

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

        var header = BuildHeader();
        var buttons = BuildButtonBar();
        var status = BuildStatusBar();

        _tabs = new TabStrip { Dock = DockStyle.Top };
        _tabs.AddItem("运行状态");
        _tabs.AddItem("配置");
        _tabs.SelectedIndexChanged += (_, _) => ShowPage(_tabs.SelectedIndex);

        _pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.PageBg };
        _tabStatus = BuildStatusPage();
        _tabConfig = BuildConfigPage();
        _tabConfig.Visible = false;
        _pageHost.Controls.Add(_tabStatus);
        _pageHost.Controls.Add(_tabConfig);

        // Dock 顺序：先加的填剩余空间，后加的优先占边（所以底部/顶部几条要放在后面加）
        Controls.Add(_pageHost);
        Controls.Add(_tabs);
        Controls.Add(header);
        Controls.Add(buttons);
        Controls.Add(status);

        ResumeLayout(performLayout: true);
    }

    private void ShowPage(int index)
    {
        _tabStatus.Visible = index == 0;
        _tabConfig.Visible = index != 0;

        if (index == 0)
        {
            _tabStatus.BringToFront();
        }
        else
        {
            _tabConfig.BringToFront();
        }
    }

    /// <summary>供 <c>--screenshot</c> 离屏渲染时切换页签（会触发正常的页面切换逻辑）。</summary>
    internal void SelectTab(int index) => _tabs.SelectedIndex = index;

    // ---------- 标题栏 ----------

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = HeaderHeight,
            BackColor = Theme.CardBg,
        };

        header.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Divider, 1F);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        var textLeft = 22;

        // 图标尺寸跟着字体走，不写死：150% DPI 下 11pt 标题本身就有 22px 高，
        // 再塞一个 22px 的图标会显得比字还小。
        var iconSize = Math.Clamp(Theme.Title.Height + 6, 22, HeaderHeight - 20);
        var iconBitmap = LoadAppIconBitmap(iconSize);
        if (iconBitmap is not null)
        {
            var picture = new PictureBox
            {
                Image = iconBitmap,
                // 显式给尺寸并拉伸，别用 SizeMode.AutoSize：
                // AutoSize 会把控件撑成位图的真实尺寸，而位图尺寸在不同 DPI 下不一定等于 iconSize。
                Size = new Size(iconSize, iconSize),
                SizeMode = PictureBoxSizeMode.StretchImage,
                BackColor = Theme.CardBg,
                Location = new Point(22, (HeaderHeight - iconSize) / 2),
            };
            header.Controls.Add(picture);
            textLeft = 22 + iconSize + 12;
        }

        header.Controls.Add(new Label
        {
            Text = Program.AppTitle,
            Font = Theme.Title,
            ForeColor = Theme.TextStrong,
            AutoSize = true,
            BackColor = Color.Transparent,
            Location = new Point(textLeft, 13),
        });

        header.Controls.Add(new Label
        {
            Text = "托盘常驻 · cron 定时签到 · 实时日志",
            Font = Theme.Small,
            ForeColor = Theme.TextFaint,
            AutoSize = true,
            BackColor = Color.Transparent,
            Location = new Point(textLeft + 1, 36),
        });

        _pillState = new StatusPill { Text = "就绪" };

        void PlacePill()
        {
            _pillState.Location = new Point(
                Math.Max(textLeft, header.Width - _pillState.Width - 22),
                (HeaderHeight - _pillState.Height) / 2);
        }

        header.Controls.Add(_pillState);
        header.Resize += (_, _) => PlacePill();
        _pillState.SizeChanged += (_, _) => PlacePill();
        PlacePill();

        return header;
    }

    // ---------- 运行状态页 ----------

    private Panel BuildStatusPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PageBg,
            Padding = new Padding(PagePadding, PagePadding - 4, PagePadding, PagePadding),
        };

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.PageBg,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, MetricCard.Height1Line + CardGap));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, PlanRowHeight + CardGap));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        stack.Controls.Add(BuildMetricRow(), 0, 0);
        stack.Controls.Add(BuildPlanAndAccountRow(), 0, 1);
        stack.Controls.Add(BuildPointsAndLogRow(), 0, 2);

        page.Controls.Add(stack);
        return page;
    }

    /// <summary>
    /// 「运行计划 / 账户积分」这一行的高度。
    ///
    /// <para>
    /// 不能拍脑袋给 —— 卡里是 <b>4 行 9pt 中文</b>（账户积分那侧），每行实测要 24 设备像素，
    /// 再加上卡片的标题带（32）与上下留白（16×2），164 是这个组合的下限。
    /// 给少了不会报错，只表现为几行文字互相压在一起（自绘布局的典型静默故障）。
    /// </para>
    /// </summary>
    private const int PlanRowHeight = 164;

    /// <summary>
    /// 「运行计划」与「账户积分」并排一行。
    ///
    /// <para>
    /// 并排而不是各占一行：状态页的纵向空间是刚性的 —— 窗口可以缩到 680 逻辑像素高，
    /// 那点高度要分给四张指标卡、这两块卡和日志区。两块卡各自只有 3~4 行文字，
    /// 横向排开正好，凭空多占一整行只会把日志区压成一条缝。
    /// </para>
    /// </summary>
    private Control BuildPlanAndAccountRow()
    {
        var row = NewCardRow();

        var plan = BuildPlanCard();
        plan.Margin = new Padding(0, 0, CardGap / 2, 0);

        var account = BuildAccountPointsCard();
        account.Margin = new Padding(CardGap / 2, 0, 0, 0);

        row.Controls.Add(plan, 0, 0);
        row.Controls.Add(account, 1, 0);
        return row;
    }

    /// <summary>
    /// 「积分变化」与「运行日志」并排一行。
    ///
    /// 日志卡保住了它的绝对高度（这一行仍占满剩余空间），只是窄了一些；
    /// 代价换来的是一块只增不减的积分轨迹 —— 而它恰好是最该长期盯着的东西。
    /// </summary>
    private Control BuildPointsAndLogRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.PageBg,
            Margin = Padding.Empty,
        };
        // 40 : 60 是量出来的：左边要装下「10-13 09:06  +5 积分 → 123  +5 成长值 → 460  手动」
        // 一行不折（一行折了，这个列表就没法扫读了），右边的日志区又尽量别太窄。
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));

        var points = BuildPointsHistoryCard();
        points.Margin = new Padding(0, 0, CardGap / 2, 0);

        var log = BuildLogCard();
        log.Margin = new Padding(CardGap / 2, 0, 0, 0);

        row.Controls.Add(points, 0, 0);
        row.Controls.Add(log, 1, 0);
        return row;
    }

    /// <summary>两列等宽的卡片行（纵向间距由行本身负责，卡片只管左右留白）。</summary>
    private static TableLayoutPanel NewCardRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.PageBg,
            Margin = new Padding(0, 0, 0, CardGap),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        return row;
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

        for (var i = 0; i < 4; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        }

        _cardToday = NewMetricCard("今日签到", "—", unit: "", hint: "数据源 day/info");
        _cardStreak = NewMetricCard("连续签到", "—", unit: "天", hint: "来自最近一次运行");
        _cardCards = NewMetricCard("补签卡", "—", unit: "张", hint: "可用于补签");
        _cardRuntime = NewMetricCard("调度器", "—", unit: "", hint: "每 20 秒检查一次 cron");

        MetricCard[] cards = [_cardToday, _cardStreak, _cardCards, _cardRuntime];

        for (var i = 0; i < cards.Length; i++)
        {
            cards[i].Dock = DockStyle.Fill;
            cards[i].Margin = new Padding(i == 0 ? 0 : CardGap / 2, 0, i == cards.Length - 1 ? 0 : CardGap / 2, 0);
            row.Controls.Add(cards[i], i, 0);
        }

        return row;
    }

    private static MetricCard NewMetricCard(string caption, string value, string unit, string hint)
        => new()
        {
            Caption = caption,
            Value = value,
            Unit = unit,
            Hint = hint,
        };

    private Control BuildPlanCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Title = "运行计划",
            AccentColor = Theme.Accent,
            // 纵向间距由外层那一行负责（卡片自己再留就成了双份）
            Margin = Padding.Empty,
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            BackColor = Theme.CardBg,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var i = 0; i < 3; i++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
        }

        _valNext = NewValueLabel(Theme.TextBody);
        _valRefresh = NewValueLabel(Theme.TextBody);
        _valLast = NewValueLabel(Theme.TextFaint);

        grid.Controls.Add(NewCaptionLabel("下次运行"), 0, 0);
        grid.Controls.Add(_valNext, 1, 0);
        grid.Controls.Add(NewCaptionLabel("状态刷新"), 0, 1);
        grid.Controls.Add(_valRefresh, 1, 1);
        grid.Controls.Add(NewCaptionLabel("最近运行"), 0, 2);
        grid.Controls.Add(_valLast, 1, 2);

        card.Controls.Add(grid);
        return card;
    }

    /// <summary>
    /// 「账户积分」卡片：积分余额 / 累计获得 / 成长等级 / 成长值。
    ///
    /// <para>
    /// 数据源是日志里的<b>账户行</b>（见 <see cref="AccountLine"/>），由
    /// <c>MainForm.Logic</c> 解析后填进来 —— 与顶部三张状态卡同一个套路：
    /// 手动触发、cron 触发、每天 00:01 的只读刷新都能刷到它，不必给签到流程加回传通道。
    /// 重启后先从 <c>points-history.log</c> 恢复，所以刚开机也能看到上次的积分。
    /// </para>
    /// </summary>
    private Control BuildAccountPointsCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Title = "账户积分",
            Note = "积分 / 成长，每次运行后刷新",
            AccentColor = Theme.Accent,
            Margin = Padding.Empty,
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = Theme.CardBg,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var i = 0; i < 4; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));

        // 首行用强调色：一眼看到的是「我有多少积分」，其余三项是它的注解
        _valPoint = NewValueLabel(Theme.TextStrong);
        _valIncome = NewValueLabel(Theme.TextBody);
        _valLevel = NewValueLabel(Theme.TextBody);
        _valGrowth = NewValueLabel(Theme.TextBody);

        grid.Controls.Add(NewCaptionLabel("积分余额"), 0, 0);
        grid.Controls.Add(_valPoint, 1, 0);
        grid.Controls.Add(NewCaptionLabel("累计获得"), 0, 1);
        grid.Controls.Add(_valIncome, 1, 1);
        grid.Controls.Add(NewCaptionLabel("成长等级"), 0, 2);
        grid.Controls.Add(_valLevel, 1, 2);
        grid.Controls.Add(NewCaptionLabel("成长值"), 0, 3);
        grid.Controls.Add(_valGrowth, 1, 3);

        card.Controls.Add(grid);
        return card;
    }

    /// <summary>
    /// 「积分变化」卡片：逐条列出<b>有增减</b>的积分/成长值变化（数据来自
    /// 数据目录的 <c>points-history.log</c>）。
    ///
    /// <para>
    /// 用只读 RichTextBox 而不是一堆 Label：条数会一直长，而这块卡的高度是固定的，
    /// 需要一个自己会滚动的容器。也顺带能按增减着色（涨绿、跌红）。
    /// </para>
    /// </summary>
    private Control BuildPointsHistoryCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Title = "积分变化",
            Note = "只列有增减的记录",
            AccentColor = Theme.Accent,
            Margin = Padding.Empty,
        };

        _pointsBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.CardBg,
            ForeColor = Theme.TextBody,
            Font = Theme.Small,
            DetectUrls = false,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            HideSelection = false,
            TabStop = false,
        };

        card.Controls.Add(_pointsBox);
        return card;
    }

    private Control BuildLogCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Title = "运行日志",
            Note = "完整日志见 logs 目录",
            AccentColor = Theme.Accent,
            // 必须显式清零：不写的话会套用 WinForms 默认的 3px 外边距，
            // 日志卡就会比上面那张「运行计划」左右各缩 3px，肉眼看得出来没对齐。
            Margin = Padding.Empty,
        };

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
            ScrollBars = RichTextBoxScrollBars.Vertical,
            HideSelection = false,
        };

        // 深色日志块外面套一层同色边框，避免 RichTextBox 的直角直接怼在圆角卡片上
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.LogBg,
            Padding = new Padding(10, 8, 6, 8),
        };
        host.Controls.Add(_logBox);

        card.Controls.Add(host);
        return card;
    }

    private static Label NewCaptionLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = Theme.Base,
        ForeColor = Theme.TextMuted,
        BackColor = Color.Transparent,
        Margin = Padding.Empty,
    };

    private static Label NewValueLabel(Color color) => new()
    {
        Text = "—",
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = Theme.Base,
        ForeColor = color,
        BackColor = Color.Transparent,
        Margin = Padding.Empty,
        AutoEllipsis = true,
    };

    // ---------- 配置页 ----------

    private Panel BuildConfigPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PageBg,
            AutoScroll = true,
            Padding = new Padding(PagePadding, PagePadding - 4, PagePadding, PagePadding),
        };

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.PageBg,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        stack.Controls.Add(BuildAccountCard());
        stack.Controls.Add(BuildNativeChannelCard());
        stack.Controls.Add(BuildScheduleCard());
        stack.Controls.Add(BuildShareCard());
        stack.Controls.Add(BuildPushCard());
        stack.Controls.Add(BuildNetworkCard());
        stack.Controls.Add(BuildSystemCard());

        _configCanvas = stack;
        page.Controls.Add(stack);
        return page;
    }

    private Control BuildAccountCard()
    {
        var grid = NewCardGrid();
        var row = 0;

        _txtRefreshToken = new InputField(password: true)
        {
            PlaceholderText = "抓包得到的 refreshToken",
        };
        _chkShowToken = new FlatCheckBox { Text = "显示" };
        _chkShowToken.CheckedChanged += (_, _) =>
            _txtRefreshToken.UseSystemPasswordChar = !_chkShowToken.Checked;

        AddFieldRow(grid, "refreshToken", InlineField(_txtRefreshToken, _chkShowToken), ref row,
            "领克 APP 抓包得到的 token 字段值（有效期约 30 天，程序会自动轮换写回）");

        _txtDeviceId = new InputField { PlaceholderText = "抓包请求头里的 deviceId" };
        AddFieldRow(grid, "deviceId", _txtDeviceId, ref row, "抓包请求头里的 deviceId");

        // ---- 从抓包导入 ----
        // 这一步本来是全流程里最容易出错的地方：要在几十条请求里认出登录那一条、从 URL 里
        // 抄出 32 位 deviceId、再从响应 JSON 里整串复制 42 个字符的 refreshToken。
        // 而且两个值必须同源配对，配错就是一路 401。既然抓包本身绕不开，就让程序自己读。
        _btnImportHar = new FlatButton { Text = "从抓包导入…", Kind = ButtonKind.Secondary };
        _btnImportHar.Click += OnImportHarClick;

        _harPreview = new Label
        {
            Text = "支持 .har（Stream / Charles / Fiddler 导出）",
            AutoSize = true,
            Font = Theme.Small,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            Margin = new Padding(10, 7, 0, 0),
        };

        var harRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.CardBg,
            Margin = Padding.Empty,
            Padding = new Padding(0, 2, 0, 2),
        };
        harRow.Controls.Add(_btnImportHar);
        harRow.Controls.Add(_harPreview);

        AddFieldRow(grid, "", harRow, ref row,
            "自动认出短信登录那一条请求，把 refreshToken 与 deviceId 一并填好并保存；"
            + "只在本地解析，不会上传任何数据；"
            + "Stream抓包使用方式，Stream打开，开始抓包=》打开领克APP=》退出登录=》输入手机，收取验证码，登录=》Stream停止抓包=》Stream抓包历史搜索mobileCodeLogin=》编辑导出har=》导入自动签到=》自动解析出refreshToken 与 deviceId；");

        // ---- 原生通道：签到写入接口唯一的前置条件 ----
        // 单列一张卡而不是塞进上面两行，是因为它和「账号」不是一回事：
        // 上面两行是账号凭据，这两行是「用哪条签名通道去调网关」。
        return NewSectionCard("账号", grid);
    }

    /// <summary>
    /// 原生签名通道卡片。
    ///
    /// <para>
    /// 签到接口 <c>POST /up/api/v1/user/sign/upgrade</c> 只被授权给原生 iOS SDK 通道
    /// （<c>X-Ca-Key=203760416</c>），H5 通道打过去恒为 <c>403 Unauthorized Consumer</c>。
    /// 而这条通道的 secret 抓包拿不到、也无法从签名反推，只能手填。
    /// </para>
    /// </summary>
    private Control BuildNativeChannelCard()
    {
        var grid = NewCardGrid();
        var row = 0;

        _txtCaNativeKey = new InputField { PlaceholderText = LynkConstants.NativeCaKey };
        AddFieldRow(grid, "原生通道 Key", _txtCaNativeKey, ref row,
            $"留空即用抓包实测值 {LynkConstants.NativeCaKey}");

        _txtCaNativeSecret = new InputField(password: true)
        {
            PlaceholderText = "原生 iOS SDK 通道的 app secret",
        };
        _chkShowNativeSecret = new FlatCheckBox { Text = "显示" };
        _chkShowNativeSecret.CheckedChanged += (_, _) =>
            _txtCaNativeSecret.UseSystemPasswordChar = !_chkShowNativeSecret.Checked;

        AddFieldRow(grid, "原生通道 Secret", InlineField(_txtCaNativeSecret, _chkShowNativeSecret), ref row,
            "签到写入的唯一前置条件。它不出现在任何请求/响应里，抓包只能看到 Key，"
            + "也无法由签名反推，需要自行提供；留空则跳过签到动作，其余功能不受影响");

        // ---- 验证按钮 ----
        // 这一串 secret 只能靠人工去 App 安装包里挖，挖错了却没有任何即时反馈 ——
        // 要等到某天早上签到跑挂才知道，而那意味着已经白丢一天。所以给一个当场能验的入口。
        _btnVerifyNative = new FlatButton { Text = "验证密钥", Kind = ButtonKind.Secondary };
        _btnVerifyNative.Click += OnVerifyNativeSecretClick;

        var verifyRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.CardBg,
            Margin = Padding.Empty,
            Padding = new Padding(0, 2, 0, 2),
        };
        verifyRow.Controls.Add(_btnVerifyNative);

        AddFieldRow(grid, "", verifyRow, ref row,
            "用假 token 向签到端点发一次探测请求，靠网关的回应判断密钥对不对（不会真的签到）；"
            + "结果写进日志框，也落盘到数据目录的 verify-native-secret.log");

        return NewSectionCard("签到通道（原生）", grid,
            "只有签到写入需要它，其余接口走 H5 通道");
    }

    private Control BuildScheduleCard()
    {
        var grid = NewCardGrid();
        var row = 0;

        _txtCron = new InputField { PlaceholderText = "分 时 日 月 周，例如 0 9 * * *" };
        _txtCron.Inner.TextChanged += (_, _) => UpdateCronPreview();
        AddFieldRow(grid, "cron 表达式", _txtCron, ref row,
            "5 段式：分 时 日 月 周，例如 0 9 * * * 表示每天 09:00");

        _cronPreview = new Label
        {
            Text = "—",
            AutoSize = true,
            Font = Theme.Small,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 4),
        };
        AddFieldRow(grid, "", _cronPreview, ref row, null);

        return NewSectionCard("调度", grid);
    }

    private Control BuildShareCard()
    {
        var grid = NewCardGrid();
        var row = 0;

        // ---- 文章 ID 从哪来 ----
        // 文章会被下架，而「id 失效」在分享环节是完全静默的：接口照样返回成功、能量体不涨。
        // 所以默认每次运行现拉一篇最新的，让人不用再去照看这个 id。
        _chkAutoArticleId = new FlatCheckBox { Text = "自动拉取最新文章（不用手动维护文章 ID）" };
        AddFieldRow(grid, "", _chkAutoArticleId, ref row,
            "每次运行都去探索广场取最新一篇「文章」的 id（走 H5 通道，不依赖原生密钥）；"
            + "取消勾选则固定用下面填的那个 ID");

        _txtContentId = new InputField { PlaceholderText = "自动拉取失败时用它兜底" };
        AddFieldRow(grid, "分享文章 ID", _txtContentId, ref row,
            "自动拉取失败时用它兜底；取消勾选「自动拉取」后则以它为准");

        // ---- 拉取一次 ----
        // 与「验证密钥」同一个理由：这个功能依赖一个第三方接口的返回结构，
        // 结构一变就取不到文章、只能静默回退。给一个当场能看见结果、又不会真签到的入口。
        _btnFetchArticle = new FlatButton { Text = "拉取一次", Kind = ButtonKind.Secondary };
        _btnFetchArticle.Click += OnFetchArticleClick;

        _articlePreview = new Label
        {
            Text = "尚未拉取",
            AutoSize = true,
            Font = Theme.Small,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            Margin = new Padding(10, 7, 0, 0),
        };

        var fetchRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.CardBg,
            Margin = Padding.Empty,
            Padding = new Padding(0, 2, 0, 2),
        };
        fetchRow.Controls.Add(_btnFetchArticle);
        fetchRow.Controls.Add(_articlePreview);

        AddFieldRow(grid, "", fetchRow, ref row,
            "用当前配置里的 refreshToken 实拉一次文章流，确认接口通、能命中「文章」类型；"
            + "不会改动配置、也不会执行签到");

        _chkAutoShare = new FlatCheckBox { Text = "启用自动分享（B 账号给你的分享链接点击刷积分）" };
        AddFieldRow(grid, "", _chkAutoShare, ref row, null);

        _txtTokenB = new InputField(lines: 4) { PlaceholderText = "每行一个 refreshToken" };
        AddFieldRow(grid, "B 账号 token", _txtTokenB, ref row, "每行一个 refreshToken；留空则不执行 B 账号点击");

        // ---- 签到后自动打开分享链接 ----
        // 分成两级而不是一个开关：定时任务大多在早上无人值守时跑，
        // 那时候弹浏览器窗口是纯打扰；而「立即签到」是人主动点的，弹出来正合意。
        // 做成两档后，「平时不弹、手动点了才弹」能一句话说清，也不用去解释 cron 是什么。
        _chkOpenShareUrl = new FlatCheckBox { Text = "签到后自动用浏览器打开分享链接" };
        _chkOpenShareUrl.CheckedChanged += OnOpenShareUrlChanged;
        AddFieldRow(grid, "", _chkOpenShareUrl, ref row,
            "勾选后，拿到分享链接就顺手用系统默认浏览器打开；"
            + "默认只在点「立即签到」时打开，定时任务不弹窗口");

        _chkOpenShareAlways = new FlatCheckBox { Text = "定时任务也打开（不勾则只在「立即签到」时打开）" };
        AddFieldRow(grid, "", _chkOpenShareAlways, ref row,
            "定时任务多在无人值守时运行，打开会直接弹出浏览器窗口，请确认自己真的需要");

        return NewSectionCard("分享任务", grid);
    }

    private Control BuildPushCard()
    {
        var grid = NewCardGrid();
        var row = 0;

        _txtWecom = AddPushField(grid, "企业微信 webhook", ref row, "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=…");
        _txtDingtalk = AddPushField(grid, "钉钉 webhook", ref row, "https://oapi.dingtalk.com/robot/send?access_token=…");
        _txtFeishu = AddPushField(grid, "飞书 webhook", ref row, "https://open.feishu.cn/open-apis/bot/v2/hook/…");
        _txtTgBotToken = AddPushField(grid, "Telegram Bot Token", ref row, null);
        _txtTgChatId = AddPushField(grid, "Telegram Chat ID", ref row, null);
        _txtServerChan = AddPushField(grid, "Server 酱 SendKey", ref row, null);
        _txtPushPlus = AddPushField(grid, "PushPlus Token", ref row, null);
        _txtBark = AddPushField(grid, "Bark URL", ref row, "https://api.day.app/your-key");

        return NewSectionCard("推送渠道", grid, "全部留空则只写日志不推送");
    }

    private static InputField AddPushField(TableLayoutPanel grid, string caption, ref int row, string? placeholder)
    {
        var field = new InputField();
        if (placeholder is not null) field.PlaceholderText = placeholder;

        AddFieldRow(grid, caption, field, ref row, null);
        return field;
    }

    private Control BuildNetworkCard()
    {
        var grid = NewCardGrid();
        var row = 0;

        _txtHttpProxy = new InputField { PlaceholderText = "留空 = 直连（推荐）" };
        AddFieldRow(grid, "HTTP 代理", _txtHttpProxy, ref row,
            "留空 = 直连（推荐，自动忽略系统代理，避免代理软件退出后连不上）；"
            + "填 system = 沿用系统代理；也可填 http://127.0.0.1:7897");

        return NewSectionCard("网络", grid);
    }

    private Control BuildSystemCard()
    {
        var grid = NewCardGrid();
        var row = 0;

        _chkAutostart = new FlatCheckBox { Text = "开机自动启动（登录后静默缩到托盘）" };
        _chkAutostart.CheckedChanged += OnAutostartChanged;
        AddFieldRow(grid, "", _chkAutostart, ref row, "写 HKCU 注册表，不需要管理员权限");

        return NewSectionCard("系统", grid);
    }

    private static CardPanel NewSectionCard(string title, Control content, string? note = null)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Title = title,
            Note = note,
            AccentColor = Theme.Accent,
            Margin = new Padding(0, 0, 0, CardGap),
        };

        card.Controls.Add(content);
        return card;
    }

    private static TableLayoutPanel NewCardGrid()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            BackColor = Theme.CardBg,
            Margin = Padding.Empty,
        };
        // 标题列宽度按最长的一条："Telegram Bot Token" 约 182px + 右侧 10px 间距。
        // 这个值不能拍脑袋给 —— 给窄了 WinForms 的 Label 是「硬切」而不是加省略号，
        // 界面上就会出现「Telegram B」这种半截字（--screenshot 的布局体检会直接报窄多少）。
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 196F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        return grid;
    }

    /// <summary>表单的一行：左边标题、右边编辑控件，编辑控件下方可选一行说明。</summary>
    private static void AddFieldRow(TableLayoutPanel grid, string caption, Control editor, ref int row, string? hint)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 多行输入框与说明标签自带高度，用 Dock=Fill 会被单元格拉伸变形
        editor.Dock = editor is InputField { Multiline: true } or Label
            ? DockStyle.Top
            : DockStyle.Fill;

        var captionLabel = new Label
        {
            Text = caption,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Base,
            ForeColor = Theme.TextBody,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 10, 4),
            // 万一以后有人把标题列又改窄了，最差也是「Telegram Bot…」，
            // 而不是把字切一半 —— 硬切看起来像渲染坏了。
            AutoEllipsis = true,
        };

        if (string.IsNullOrEmpty(hint))
        {
            grid.Controls.Add(captionLabel, 0, row);
            grid.Controls.Add(editor, 1, row);
        }
        else
        {
            var stack = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Theme.CardBg,
                Margin = Padding.Empty,
            };

            // 这一列必须是「占满」，不能让它按内容自适应。
            // 不给 ColumnStyle 时 TableLayoutPanel 按 AutoSize 处理，于是：
            //   1) 输入框只拿到自己的首选宽度，比「没有说明文字的行」窄一截，
            //      同一个卡片里上下两个输入框右边缘对不齐；
            //   2) 说明文字一长（"留空 = 直连…" 这段），列被撑到 747px，
            //      而容器只有 732px，输入框就直接画到卡片外面去了。
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            editor.Margin = new Padding(0, 3, 0, 0);
            stack.Controls.Add(editor, 0, 0);
            stack.Controls.Add(new Label
            {
                Text = hint,
                AutoSize = true,
                MaximumSize = new Size(760, 0),
                Font = Theme.Small,
                ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent,
                Margin = new Padding(1, 1, 0, 6),
            }, 0, 1);

            grid.Controls.Add(captionLabel, 0, row);
            grid.Controls.Add(stack, 1, row);
        }

        row++;
    }

    /// <summary>把输入框和紧跟其后的一个小控件（如「显示」勾选）排成一行。</summary>
    private static Control InlineField(Control main, Control trailing)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.CardBg,
            Margin = Padding.Empty,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        main.Dock = DockStyle.Fill;
        trailing.Anchor = AnchorStyles.Left;
        trailing.Margin = new Padding(8, 0, 0, 0);

        panel.Controls.Add(main, 0, 0);
        panel.Controls.Add(trailing, 1, 0);
        return panel;
    }

    // ---------- 底部 ----------

    private Control BuildButtonBar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 60,
            Padding = new Padding(PagePadding, 13, PagePadding, 13),
            BackColor = Theme.PageBg,
            FlowDirection = FlowDirection.LeftToRight,
        };

        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Divider, 1F);
            e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0);
        };

        _btnRunNow = new FlatButton { Text = "立即签到", Kind = ButtonKind.Primary, Margin = new Padding(0, 0, 10, 0) };
        _btnRunNow.Click += OnRunNowClick;

        _btnSave = new FlatButton { Text = "保存配置", Margin = new Padding(0, 0, 10, 0) };
        _btnSave.Click += (_, _) => SaveConfigFromForm(showFeedback: true);

        var btnReload = new FlatButton { Text = "重新载入", Kind = ButtonKind.Ghost };
        btnReload.Click += (_, _) =>
        {
            LoadConfigIntoForm();
            EnqueueLog("[INFO] 已从 config.json 重新载入配置");
        };

        var btnData = new FlatButton { Text = "打开数据目录", Kind = ButtonKind.Ghost };
        btnData.Click += (_, _) => OpenPath(AppPaths.WorkDir);

        var btnLogs = new FlatButton { Text = "打开日志目录", Kind = ButtonKind.Ghost };
        btnLogs.Click += (_, _) => OpenPath(_host.Logs.Dir);

        var btnClearCache = new FlatButton { Text = "清空 token 缓存", Kind = ButtonKind.Ghost };
        btnClearCache.Click += OnClearCacheClick;

        bar.Controls.AddRange([_btnRunNow, _btnSave, btnReload, btnData, btnLogs, btnClearCache]);
        return bar;
    }

    private Control BuildStatusBar()
    {
        _sbDataDir = new ToolStripStatusLabel
        {
            Text = "数据目录：—",
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.TextFaint,
            Font = Theme.Small,
        };

        _sbState = new ToolStripStatusLabel
        {
            Text = "就绪",
            ForeColor = Theme.TextFaint,
            Font = Theme.Small,
        };

        var strip = new StatusStrip
        {
            Dock = DockStyle.Bottom,
            SizingGrip = false,
            BackColor = Theme.PageBg,
            ForeColor = Theme.TextFaint,
            Padding = new Padding(PagePadding - 6, 0, PagePadding - 6, 0),
        };
        strip.Items.Add(_sbDataDir);
        strip.Items.Add(_sbState);
        return strip;
    }

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

    /// <summary>
    /// 把程序图标等比画进一张固定尺寸的位图。
    ///
    /// <para>
    /// 不要用 <c>new Icon(icon, new Size(n, n)).ToBitmap()</c> ——
    /// 非 96 DPI 下 <see cref="Icon"/> 报出来的 <c>Width/Height</c> 与实际位图尺寸会对不上：
    /// 本机 150% 时想要 22px，拿到的却是 48×48，于是图标直接压到标题上。
    /// 自己 <c>DrawImage</c> 到目标尺寸最稳。
    /// </para>
    /// </summary>
    private static Bitmap? LoadAppIconBitmap(int size)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return null;

            using var source = Icon.ExtractAssociatedIcon(exe);
            if (source is null) return null;

            using var raw = source.ToBitmap();
            var bitmap = new Bitmap(size, size);

            using (var g = Graphics.FromImage(bitmap))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(raw, new Rectangle(0, 0, size, size));
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    // ==================================================================
    //  托盘 / 关闭
    // ==================================================================

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
        var entry = (line, LogColor(line));

        lock (_logQueue)
        {
            _logQueue.Enqueue(entry);
            // 极端情况下（重定向循环等）兜住内存：只保留最近 500 条待刷
            while (_logQueue.Count > 500) _logQueue.Dequeue();
        }

        // 顺手把「签到状态」那一行摘给顶部状态卡片 —— 手动触发和 cron 触发都能覆盖到，
        // 不必再让 SignJob 额外回传结构化结果（那样就得改原项目了）。
        var match = StatusPattern.Match(line);
        if (match.Success) ApplySignStatus(match);

        // 账户行同理，喂「账户积分」那张卡。先用 Contains 粗筛一次：
        // 一次运行能刷出上百行日志，而每行都跑一遍正则纯属浪费。
        if (line.Contains(AccountLine.Prefix, StringComparison.Ordinal)
            && AccountLine.Parse(line) is { } account)
        {
            ApplyAccount(account);
        }
    }

    private void FlushLogQueue()
    {
        List<(string Text, Color Color)> batch;

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

    private void AppendBatch(List<(string Text, Color Color)> batch)
    {
        if (IsDisposed) return;

        _logBox.SuspendLayout();
        try
        {
            foreach (var (text, color) in batch)
            {
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.SelectionLength = 0;
                _logBox.SelectionColor = color;
                _logBox.AppendText(text + Environment.NewLine);
            }

            _logBox.SelectionColor = _logBox.ForeColor;

            // 超长裁剪：按字符区间删除。用 Lines 赋值会把剩余行的着色一并清掉，所以走 Select+删除。
            if (_logBox.GetLineFromCharIndex(_logBox.TextLength) > MaxLogLines)
            {
                var cutAt = _logBox.GetFirstCharIndexFromLine(LogTrimStep);
                if (cutAt > 0)
                {
                    _logBox.Select(0, cutAt);
                    _logBox.SelectedText = string.Empty;
                }
            }

            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }
        finally
        {
            _logBox.ResumeLayout();
        }
    }

    /// <summary>
    /// 日志着色。日志区是深色底，所以用的是"暗色主题"那一套柔和的提示色，
    /// 直接套亮色的红/绿会在深底上刺眼。
    /// </summary>
    private static Color LogColor(string line)
    {
        if (line.Contains("[X]", StringComparison.Ordinal)) return Theme.LogErr;
        if (line.Contains("[!]", StringComparison.Ordinal)) return Theme.LogWarn;
        if (line.Contains("[OK]", StringComparison.Ordinal)) return Theme.LogOk;
        if (line.Contains("[INFO]", StringComparison.Ordinal)) return Theme.LogInfo;
        return Theme.LogText;
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
