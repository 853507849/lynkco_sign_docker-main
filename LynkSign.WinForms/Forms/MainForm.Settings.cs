using LynkSign.Configuration;
using LynkSign.Infrastructure;
using LynkSign.Lynk;
using LynkSign.Services;
using LynkSign.WinForms.Ui;

namespace LynkSign.WinForms.Forms;

/// <summary>
/// 「设置」页：把原来那张大配置表拆成两列卡片。
///
/// <para>
/// 拆列的依据是"改动的频率"：左列是<b>凭据与网络</b>（一般设一次就不动，改了要重新验证），
/// 右列是<b>策略与通知</b>（cron、分享、推送、维护动作，会时不时调）。
/// 这样最常见的"改个时间点"不用在一屏 20 个字段里找。
/// </para>
///
/// <para>
/// 所有字段与原来一一对应，功能一个不少 —— 只是换了排列与外观：
/// 开关从勾选框换成 Switch、代理从一行文本换成三档分段控件（语义仍是
/// <c>http_proxy</c> 的 空 / system / 具体地址）。
/// </para>
/// </summary>
internal sealed partial class MainForm
{
    private Panel _settingsScroll = null!;

    private Panel BuildSettingsPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PageBg,
            Padding = new Padding(Theme.PadX, 0, Theme.PadX, 0),
        };

        var header = new PageHeader
        {
            Title = "设置",
            Subtitle = "改动即时写入 config.json · 仅本机保存，不上传",
        };

        var reset = new FlatButton { Text = "恢复默认", Kind = ButtonKind.Secondary, Margin = new Padding(0, 0, 8, 0) };
        reset.Click += (_, _) => ResetToDefaults();
        header.Actions.Controls.Add(reset);

        _btnSave = new FlatButton { Text = "保存配置", Kind = ButtonKind.Primary, Margin = Padding.Empty };
        _btnSave.Click += (_, _) => SaveConfigFromForm(showFeedback: true);
        header.Actions.Controls.Add(_btnSave);

        _settingsScroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.PageBg,
            Padding = new Padding(0, 0, 0, 16),
        };

        _settingsCanvas = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            BackColor = Theme.PageBg,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _settingsCanvas.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        _settingsCanvas.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

        var left = NewColumn();
        left.Controls.Add(BuildAccountCard());
        left.Controls.Add(BuildNetworkCard());
        left.Controls.Add(BuildNativeChannelCard());

        var right = NewColumn();
        right.Controls.Add(BuildScheduleCard());
        right.Controls.Add(BuildShareCard());
        right.Controls.Add(BuildPushCard());
        right.Controls.Add(BuildDataCard());

        left.Margin = new Padding(0, 0, CardGap / 2, 0);
        right.Margin = new Padding(CardGap / 2, 0, 0, 0);

        _settingsCanvas.Controls.Add(left, 0, 0);
        _settingsCanvas.Controls.Add(right, 1, 0);

        _settingsScroll.Controls.Add(_settingsCanvas);

        page.Controls.Add(_settingsScroll);
        page.Controls.Add(header);
        return page;
    }

    private static TableLayoutPanel NewColumn()
    {
        var col = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.PageBg,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        col.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        return col;
    }

    // ---------- 左列 ----------

    private Control BuildAccountCard()
    {
        var grid = NewCardGrid();
        var row = 0;

        _txtRefreshToken = new InputField(password: true) { PlaceholderText = "抓包得到的 refreshToken" };
        _chkShowToken = new FlatCheckBox { Text = "显示" };
        _chkShowToken.CheckedChanged += (_, _) =>
            _txtRefreshToken.UseSystemPasswordChar = !_chkShowToken.Checked;

        AddFieldRow(grid, "refreshToken", InlineField(_txtRefreshToken, _chkShowToken), ref row,
            "有效期约 30 天，程序会自动轮换写回");

        _txtDeviceId = new InputField { PlaceholderText = "抓包请求头里的 deviceId" };
        AddFieldRow(grid, "deviceId", _txtDeviceId, ref row, null);

        // ---- 抓包导入 ----
        // 不再单列「绑定状态」：上面两个输入框本身就是状态（有没有 token 一眼就看得出来），
        // 再挂一个「已绑定 · 尾号 xxxx」胶囊属于同一条信息说两遍，还占掉一整行。
        // 这里只留动作按钮 + 「怎么弄？」的说明，整块横跨两列（说明文字从卡片左边距起排）。
        var importHost = new Panel
        {
            Dock = DockStyle.Fill,
            Height = 44,
            // 顶部留 6px：按钮紧贴 deviceId 输入框会看起来像那一行的附属控件，
            // 而它其实是一个独立的动作。
            Padding = new Padding(0, 6, 0, 0),
            BackColor = Theme.CardBg,
            Margin = Padding.Empty,
        };

        // 导入结果的即时回显（"已导入 138xxxx"）：默认空，显示几秒后自己消失。
        // 它不是常驻状态，只是这次操作到底成没成的反馈；
        // 一点回显都不给的话，导入成功与否只能去翻日志。
        _importEcho = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Font = Theme.Small,
            ForeColor = Theme.TextMuted,
            BackColor = Theme.CardBg,
            AutoEllipsis = true,
            Padding = new Padding(0, 0, 12, 0),
        };

        _btnImportHar = new FlatButton
        {
            Text = "导入文件",
            Kind = ButtonKind.Secondary,
            Height = 38,
            Width = 92,
            Dock = DockStyle.Right,
        };
        _btnImportHar.Click += OnImportHarClick;

        // Dock 按 Controls 索引逆序处理：后加的按钮先停靠到右边，
        // 回显文字再 Fill 剩下的宽度，两者不会互相覆盖。
        importHost.Controls.Add(_importEcho);
        importHost.Controls.Add(_btnImportHar);

        AddFieldRow(grid, "", importHost, ref row,
            "支持从 HAR 文件（Stream / Charles / Fiddler 导出）一键导入。从抓包文件自动认出短信登录那条，把 refreshToken 与 deviceId 一并填好；只在本地解析；Stream抓包使用方式，Stream打开，开始抓包=》打开领克APP=》退出登录=》输入手机，收取验证码，登录=》Stream停止抓包=》Stream抓包历史搜索mobileCodeLogin=》编辑导出har=》导入自动签到=》自动解析出refreshToken 与 deviceId；",
            spanAll: true);

        return NewSectionCard("账号与凭证", grid, "加密存储");
    }

    private Control BuildNetworkCard()
    {
        var grid = NewCardGrid();
        var row = 0;

        _segProxy = new SegmentedControl { Dock = DockStyle.Fill, Height = 34 };
        _segProxy.AddItem("直连");
        _segProxy.AddItem("系统代理");
        _segProxy.AddItem("自定义");
        _segProxy.SelectedIndexChanged += (_, _) => OnProxyModeChanged();

        AddFieldRow(grid, "网络模式", _segProxy, ref row, null);

        _txtHttpProxy = new InputField { PlaceholderText = "socks5://127.0.0.1:7890" };
        _txtHttpProxy.Inner.TextChanged += (_, _) => UpdateProxyState();
        AddFieldRow(grid, "代理地址", _txtHttpProxy, ref row, null);

        _proxyState = new ChipLabel
        {
            Caption = "出站：—",
            Dock = DockStyle.Fill,
            Tone = Theme.TextMuted,
            Fill = Theme.ChipBg,
            Border = Theme.ChipBorder,
            DotPrefix = true,
            Margin = new Padding(0, 2, 0, 0),
        };

        AddFieldRow(grid, "", _proxyState, ref row, null);

        return NewSectionCard("网络与代理", grid, "签到请求走此通道");
    }

    private Control BuildNativeChannelCard()
    {
        var grid = NewCardGrid();
        var row = 0;

        _txtCaNativeKey = new InputField { PlaceholderText = LynkConstants.NativeCaKey };
        AddFieldRow(grid, "通道 Key", _txtCaNativeKey, ref row, $"留空即用实测值 {LynkConstants.NativeCaKey}");

        _txtCaNativeSecret = new InputField(password: true) { PlaceholderText = "原生 iOS SDK 通道的 app secret" };
        _chkShowNativeSecret = new FlatCheckBox { Text = "显示" };
        _chkShowNativeSecret.CheckedChanged += (_, _) =>
            _txtCaNativeSecret.UseSystemPasswordChar = !_chkShowNativeSecret.Checked;

        AddFieldRow(grid, "通道 Secret", InlineField(_txtCaNativeSecret, _chkShowNativeSecret), ref row,
            "签到写入的唯一前置条件；抓包拿不到，也无法从签名反推。留空则跳过签到动作");

        _btnVerifyNative = new FlatButton { Text = "验证密钥", Kind = ButtonKind.Secondary };
        _btnVerifyNative.Click += OnVerifyNativeSecretClick;

        var verifyRow = NewInlineRow(_btnVerifyNative);
        AddFieldRow(grid, "", verifyRow, ref row,
            "用假 token 探测一次签到端点，靠网关回应判断密钥对不对（不会真的签到）");

        return NewSectionCard("签到通道（原生）", grid, "仅签到写入需要");
    }

    // ---------- 右列 ----------

    private Control BuildScheduleCard()
    {
        var grid = NewCardGrid();
        var row = 0;

        _txtCron = new InputField { PlaceholderText = "分 时 日 月 周，例如 0 9 * * *" };
        _txtCron.Inner.TextChanged += (_, _) => UpdateCronPreview();
        AddFieldRow(grid, "执行时间", _txtCron, ref row, null);

        _cronPreview = new Label
        {
            Text = "—",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.MonoSmall,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            Height = 20,
        };

        AddFieldRow(grid, "", _cronPreview, ref row, null);

        _chkAutostart = new ToggleSwitch { Anchor = AnchorStyles.Right };
        _chkAutostart.CheckedChanged += OnAutostartChanged;
        AddSwitchRow(grid, "开机自启", _chkAutostart, ref row, "登录后静默缩到托盘，写 HKCU 注册表");

        var refresh = NewReadOnlyValue($"每天 {Scheduling.SignScheduler.DailyRefreshTime:HH\\:mm} · 只读");
        AddFieldRow(grid, "状态刷新", refresh, ref row, "每天自动拉一次签到状态，不签到、不推送");

        return NewSectionCard("签到策略", grid, "cron 表达式");
    }

    private Control BuildShareCard()
    {
        var grid = NewCardGrid();
        var row = 0;

        _chkAutoArticleId = new ToggleSwitch { Anchor = AnchorStyles.Right };
        AddSwitchRow(grid, "自动拉取文章", _chkAutoArticleId, ref row,
            "每次运行去广场取最新一篇，不用手动维护文章 ID");

        _txtContentId = new InputField { PlaceholderText = "自动拉取失败时用它兜底" };
        AddFieldRow(grid, "文章 ID", _txtContentId, ref row, null);

        _btnFetchArticle = new FlatButton { Text = "拉取一次", Kind = ButtonKind.Secondary };
        _btnFetchArticle.Click += OnFetchArticleClick;

        _articlePreview = new Label
        {
            Text = "尚未拉取",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Small,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            Margin = new Padding(10, 0, 0, 0),
        };

        var fetchRow = NewInlineRow(_btnFetchArticle, _articlePreview);
        AddFieldRow(grid, "", fetchRow, ref row, "用当前 refreshToken 实拉一次文章流，不签到、不改配置");

        _chkAutoShare = new ToggleSwitch { Anchor = AnchorStyles.Right };
        AddSwitchRow(grid, "自动分享", _chkAutoShare, ref row, "B 账号点击你的分享链接刷积分");

        _txtTokenB = new InputField(lines: 3) { PlaceholderText = "每行一个 refreshToken" };
        AddFieldRow(grid, "B 账号 token", _txtTokenB, ref row, "留空则不执行 B 账号点击");

        _chkOpenShareUrl = new ToggleSwitch { Anchor = AnchorStyles.Right };
        _chkOpenShareUrl.CheckedChanged += OnOpenShareUrlChanged;
        AddSwitchRow(grid, "签到后打开链接", _chkOpenShareUrl, ref row, "默认只在点「立即签到」时打开");

        _chkOpenShareAlways = new ToggleSwitch { Anchor = AnchorStyles.Right };
        AddSwitchRow(grid, "定时也打开", _chkOpenShareAlways, ref row, "无人值守时会直接弹出浏览器窗口");

        return NewSectionCard("分享任务", grid, "分享得分");
    }

    private Control BuildPushCard()
    {
        var grid = NewCardGrid();
        var row = 0;

        _txtWecom = AddPushField(grid, "企业微信", ref row, "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=…");
        _txtDingtalk = AddPushField(grid, "钉钉", ref row, "https://oapi.dingtalk.com/robot/send?access_token=…");
        _txtFeishu = AddPushField(grid, "飞书", ref row, "https://open.feishu.cn/open-apis/bot/v2/hook/…");
        _txtTgBotToken = AddPushField(grid, "Telegram Token", ref row, null);
        _txtTgChatId = AddPushField(grid, "Telegram Chat", ref row, null);
        _txtServerChan = AddPushField(grid, "Server 酱", ref row, null);
        _txtPushPlus = AddPushField(grid, "PushPlus", ref row, null);
        _txtBark = AddPushField(grid, "Bark", ref row, "https://api.day.app/your-key");

        return NewSectionCard("通知渠道", grid, "仅失败时推送");
    }

    private static InputField AddPushField(TableLayoutPanel grid, string caption, ref int row, string? placeholder)
    {
        var field = new InputField();
        if (placeholder is not null) field.PlaceholderText = placeholder;

        AddFieldRow(grid, caption, field, ref row, null);
        return field;
    }

    private Control BuildDataCard()
    {
        var grid = NewCardGrid();
        var row = 0;

        _dataDirValue = new Label
        {
            Text = "—",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.MonoSmall,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            Height = 24,
        };

        var open = new FlatButton { Text = "打开", Kind = ButtonKind.Ghost, Height = 30 };
        open.Click += (_, _) => OpenPath(AppPaths.WorkDir);

        var dirRow = NewInlineRow(_dataDirValue, open);
        AddFieldRow(grid, "数据目录", dirRow, ref row, null);

        var retention = NewReadOnlyValue($"{RunLogStore.RetentionDays} 天");
        AddFieldRow(grid, "日志保留", retention, ref row, "超出天数的运行日志自动清理");

        var clear = new FlatButton
        {
            Text = "清空 token 缓存",
            Kind = ButtonKind.Danger,
            Height = 32,
            Margin = new Padding(0, 0, 8, 0),
        };
        clear.Click += OnClearCacheClick;

        var reload = new FlatButton { Text = "重新载入配置", Kind = ButtonKind.Secondary, Height = 32 };
        reload.Click += (_, _) =>
        {
            LoadConfigIntoForm();
            EnqueueLog("[INFO] 已从 config.json 重新载入配置");
        };

        var actions = NewInlineRow(clear, reload);
        AddFieldRow(grid, "", actions, ref row, "清空后下次签到会强制走一次 refresh 换取新 token（不影响绑定）");

        _saveHint = new Label
        {
            Text = "",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Small,
            ForeColor = Theme.Success,
            BackColor = Color.Transparent,
            Height = 22,
            AutoEllipsis = true,
        };
        AddFieldRow(grid, "", _saveHint, ref row, null);

        return NewSectionCard("数据与维护", grid, "本机");
    }

    // ---------- 表单布局 ----------

    /// <summary>
    /// 标题列宽度：够放「refreshToken」「B 账号 token」这类最长的标题。
    /// <para>
    /// 给窄了不会"加省略号了事"——Label 对单个英文单词是按字符折行的，
    /// 于是界面上出现「refreshTo / ken」这种半截字；中文没有折行点，
    /// 又会变成「自动拉…」。两种表现都说明这一列窄了。
    /// </para>
    /// </summary>
    private const int CaptionWidth = 152;

    /// <summary>标题相对行顶的下移量，用来和输入框里"第一行字"对齐。</summary>
    private const int CaptionTopOffset = 11;

    /// <summary>说明文字超过这个长度就收进「怎么弄？」里（两列布局下只有一行半的宽度）。</summary>
    private const int HintFoldLength = 34;

    /// <summary>编辑区的宽度上限：卡片再宽，一行字也不该横跨半个屏幕。</summary>
    private const int FieldMaxWidth = 620;

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
            Padding = Padding.Empty,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, CaptionWidth));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        return grid;
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
            Margin = new Padding(0, 0, 0, CardGap),
            Padding = new Padding(Theme.CardPadding, 16, Theme.CardPadding, 12),
        };

        card.Controls.Add(content);
        return card;
    }

    /// <summary>
    /// 表单的一行：左边标题、右边编辑控件，编辑控件下方可选一行说明。
    ///
    /// <para>
    /// 长说明收进「怎么弄？」而不直接铺开：设置页两个并排的列，一段两百字的教程
    /// 会把整列撑得又高又空，而它只在第一次配置时才有用。折叠靠
    /// <c>Label.Visible</c> + TableLayoutPanel 的 AutoSize 行 —— 不可见控件不占行高，
    /// 不用自己量高度。
    /// </para>
    /// <para>
    /// <paramref name="spanAll"/> 用于「只有动作、没有字段」的行（比如抓包导入）：
    /// 整块横跨两列，不带标题列。否则左边会空出 152px 的标题位，
    /// 说明文字还被缩进着，看着像控件没加载出来。
    /// </para>
    /// </summary>
    private static void AddFieldRow(
        TableLayoutPanel grid,
        string caption,
        Control editor,
        ref int row,
        string? hint,
        bool spanAll = false)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var field = editor is InputField ? new FieldHost(editor) : editor;
        field.Dock = field is FieldHost or Label or ChipLabel ? DockStyle.Top : DockStyle.Fill;

        var captionLabel = new Label
        {
            Text = caption,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Font = Theme.Base,
            ForeColor = Theme.TextBody,
            BackColor = Color.Transparent,
            Margin = new Padding(0, CaptionTopOffset, 10, 4),
            AutoEllipsis = true,
        };

        if (string.IsNullOrEmpty(hint))
        {
            if (spanAll)
            {
                grid.Controls.Add(field, 0, row);
                grid.SetColumnSpan(field, 2);
            }
            else
            {
                grid.Controls.Add(captionLabel, 0, row);
                grid.Controls.Add(field, 1, row);
            }

            row++;
            return;
        }

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.CardBg,
            Margin = Padding.Empty,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var i = 0; i < 3; i++) stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        field.Margin = new Padding(0, 0, 0, 0);
        stack.Controls.Add(field, 0, 0);

        var fold = hint.Length > HintFoldLength;

        var hintLabel = new Label
        {
            Text = hint,
            AutoSize = true,
            MaximumSize = new Size(FieldMaxWidth, 0),
            Font = Theme.Small,
            ForeColor = Theme.TextFaint,
            BackColor = Color.Transparent,
            Margin = new Padding(1, 4, 0, 6),
            Visible = !fold,
        };

        void FitHint()
        {
            var available = stack.ClientSize.Width;
            if (available > 0 && hintLabel.MaximumSize.Width != available)
            {
                hintLabel.MaximumSize = new Size(available, 0);
            }
        }

        stack.SizeChanged += (_, _) => FitHint();
        stack.Controls.Add(hintLabel, 0, fold ? 2 : 1);

        if (fold)
        {
            var toggle = new LinkLabel
            {
                Text = "怎么弄？",
                AutoSize = true,
                Font = Theme.Small,
                LinkColor = Theme.Accent,
                ActiveLinkColor = Theme.AccentDown,
                VisitedLinkColor = Theme.Accent,
                LinkBehavior = LinkBehavior.HoverUnderline,
                BackColor = Color.Transparent,
                Margin = new Padding(1, 2, 0, 4),
                TabStop = false,
            };

            toggle.LinkClicked += (_, _) =>
            {
                if (!hintLabel.Visible) FitHint();

                hintLabel.Visible = !hintLabel.Visible;
                toggle.Text = hintLabel.Visible ? "收起" : "怎么弄？";

                // 展开/收起改的是这一行的高度，外层卡片与滚动画布都要跟着重算
                for (Control? c = stack; c is not null; c = c.Parent) c.PerformLayout();
            };

            stack.Controls.Add(toggle, 0, 1);
        }

        if (spanAll)
        {
            grid.Controls.Add(stack, 0, row);
            grid.SetColumnSpan(stack, 2);
            row++;
            return;
        }

        grid.Controls.Add(captionLabel, 0, row);
        grid.Controls.Add(stack, 1, row);
        row++;
    }

    /// <summary>开关行：标题在左、开关贴右。说明文字与普通行共用同一套折叠逻辑。</summary>
    private static void AddSwitchRow(TableLayoutPanel grid, string caption, ToggleSwitch toggle, ref int row, string? hint)
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            Height = 26,
            BackColor = Theme.CardBg,
            Margin = Padding.Empty,
        };
        host.Controls.Add(toggle);
        toggle.Dock = DockStyle.Right;
        toggle.Width = 42;

        AddFieldRow(grid, caption, host, ref row, hint);
        host.Height = 26;
    }

    /// <summary>只读值（状态刷新时间、日志保留天数这类固定文本）。</summary>
    private static Label NewReadOnlyValue(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = Theme.Mono,
        ForeColor = Theme.TextBody,
        BackColor = Color.Transparent,
        Height = 26,
        AutoEllipsis = true,
    };

    /// <summary>把若干个控件排成一行（按钮 + 结果文字）。</summary>
    private static FlowLayoutPanel NewInlineRow(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.CardBg,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        foreach (var control in controls)
        {
            control.Margin = new Padding(0, 0, 8, 0);
            row.Controls.Add(control);
        }

        return row;
    }

    /// <summary>把输入框和紧跟其后的一个小控件（如「显示」勾选）排成一行。</summary>
    private static Control InlineField(Control main, Control trailing) => new FieldHost(main, trailing);

    /// <summary>
    /// 表单编辑区的"限宽壳"：编辑控件最宽 <see cref="FieldMaxWidth"/>，可选地在右边挂一个小组件。
    ///
    /// <para>
    /// 不用 TableLayoutPanel 是因为其中的 Percent 列会把"显示"推到卡片最右边，
    /// 与输入框隔着半米远，看不出它是在说这个输入框；它的 AutoSize 行还会把输入框撑高。
    /// 这里自己量、自己摆：高度恒等于控件自身高度。
    /// </para>
    /// </summary>
    private sealed class FieldHost : Panel
    {
        private const int Gap = 8;

        private readonly Control _main;
        private readonly Control? _trailing;

        public FieldHost(Control main, Control? trailing = null)
        {
            BackColor = Theme.CardBg;
            Margin = Padding.Empty;

            _main = main;
            _trailing = trailing;
            _main.Dock = DockStyle.None;

            Controls.Add(main);
            if (trailing is not null) Controls.Add(trailing);

            Height = main.Height;
        }

        public override Size GetPreferredSize(Size proposedSize) => new(0, _main.Height);

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);

            var trailingWidth = _trailing?.Width ?? 0;
            var gap = _trailing is null ? 0 : Gap;

            var available = Math.Max(0, Width - trailingWidth - gap);
            var width = Math.Min(FieldMaxWidth - trailingWidth - gap, available);

            _main.SetBounds(0, 0, width, _main.Height);

            if (_trailing is not null)
            {
                _trailing.Location = new Point(width + Gap, Math.Max(0, (_main.Height - _trailing.Height) / 2));
            }
        }
    }

    // ==================================================================
    //  设置页的动作
    // ==================================================================

    /// <summary>代理三档 ↔ <c>http_proxy</c> 的取值（空 / system / 具体地址）。</summary>
    private void LoadProxyMode(string? raw)
    {
        var value = (raw ?? "").Trim();

        if (value.Length == 0)
        {
            _segProxy.SelectedIndex = 0;
            _txtHttpProxy.Text = "";
        }
        else if (string.Equals(value, "system", StringComparison.OrdinalIgnoreCase))
        {
            _segProxy.SelectedIndex = 1;
            _txtHttpProxy.Text = "";
        }
        else
        {
            _segProxy.SelectedIndex = 2;
            _txtHttpProxy.Text = value;
        }

        OnProxyModeChanged();
    }

    private string CurrentProxyValue() => _segProxy.SelectedIndex switch
    {
        0 => "",
        1 => "system",
        _ => _txtHttpProxy.Text.Trim(),
    };

    private void OnProxyModeChanged()
    {
        _txtHttpProxy.Enabled = _segProxy.SelectedIndex == 2;
        UpdateProxyState();
    }

    private void UpdateProxyState()
    {
        if (_proxyState is null) return;

        var value = CurrentProxyValue();
        var describe = HttpClientSetup.Describe(value);

        _proxyState.Caption = $"出站：{describe}";
        _proxyState.Tone = _segProxy.SelectedIndex == 2 && _txtHttpProxy.Text.Trim().Length == 0
            ? Theme.Danger
            : Theme.TextMuted;
    }

    /// <summary>把默认配置填进表单（不落盘）——落盘仍然要用户点「保存配置」。</summary>
    private void ResetToDefaults()
    {
        var answer = MessageBox.Show(
            "把表单恢复成默认值？\n\n只会改动界面上的内容，点「保存配置」之后才会写入 config.json。\n"
            + "refreshToken / deviceId / 原生通道密钥也会被清空。",
            Program.AppTitle,
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);

        if (answer != DialogResult.OK) return;

        var defaults = AppConfig.CreateDefault();

        _loadingConfig = true;
        try
        {
            FillForm(defaults);
        }
        finally
        {
            _loadingConfig = false;
        }

        _saveHint.Text = "已恢复默认值，点「保存配置」后生效";
        _saveHint.ForeColor = Theme.Warning;
    }
}
