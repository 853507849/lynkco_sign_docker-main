using System.Text.RegularExpressions;
using LynkSign.WinForms.Ui;

namespace LynkSign.WinForms.Forms;

/// <summary>
/// 「签到进行中」页：五步进度 + 实时输出。
///
/// <para>
/// 这一页的数据<b>全部来自日志</b>：签到流程已经在打 <c>[n/5] ...</c> 这样的行，
/// 界面只是照着点亮对应步骤。这样不需要给 <c>SignJob</c> 加回传通道，
/// cron 触发与手动触发也会自动都覆盖到。
/// </para>
///
/// <para>
/// 步骤标题写死在 <see cref="StepTitles"/> 里，与 <c>SignJob</c> 的实际阶段一一对应；
/// 具体的接口路径不写标题，而是把那一行日志原文放在标题下面 ——
/// 流程里改端点时界面不用跟着改。
/// </para>
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>五步的标题（顺序与 SignJob 的 [n/5] 一致）。</summary>
    private static readonly string[] StepTitles =
    [
        "获取 accessToken",
        "查询账户信息",
        "执行签到",
        "准备分享",
        "自动分享",
    ];

    /// <summary>匹配 <c>[3/5] 执行签到...</c>，捕获步骤序号与那一行的说明。</summary>
    private static readonly Regex StepPattern = new(@"\[(\d)\s*/\s*5\]\s*(.*)$", RegexOptions.Compiled);

    private readonly string[] _stepDetails = new string[StepTitles.Length];

    private Panel BuildRunningPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PageBg,
            Padding = new Padding(Theme.PadX, 0, Theme.PadX, 14),
        };

        _hdrRunning = new PageHeader { Title = "签到进行中", Subtitle = "尚未运行 · 点「立即签到」开始" };

        // RightToLeft：先加的在最右边 → 视觉顺序为 胶囊 / 取消 / 签到中
        _btnRunBusy = new FlatButton
        {
            Text = "签到中",
            Kind = ButtonKind.Primary,
            Enabled = false,
            Margin = Padding.Empty,
        };
        _hdrRunning.Actions.Controls.Add(_btnRunBusy);

        _btnCancel = new FlatButton
        {
            Text = "取消",
            Kind = ButtonKind.Secondary,
            Margin = new Padding(8, 0, 8, 0),
            Enabled = false,
        };
        _btnCancel.Click += (_, _) => CancelRunning();
        _hdrRunning.Actions.Controls.Add(_btnCancel);

        _pillRun = new StatusPill { Text = "空闲", Margin = Padding.Empty };
        _hdrRunning.Actions.Controls.Add(_pillRun);

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.PageBg,
            Margin = Padding.Empty,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, RunningCardHeight + CardGap));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        stack.Controls.Add(BuildTaskCard(), 0, 0);
        stack.Controls.Add(BuildRunLogCard(), 0, 1);

        page.Controls.Add(stack);
        page.Controls.Add(_hdrRunning);
        return page;
    }

    /// <summary>
    /// 任务卡的高度：标题带 + 进度条 + 两行说明 + 五步清单（含当前步的接口路径）。
    /// 现算而不是写死 —— 字号或 DPI 一变，写死的数字就会把最后一步裁掉。
    /// </summary>
    private static int RunningCardHeight =>
        CardPanel.TitleHeight
        + 18 * 2
        + 6 + 8 + Theme.Small.Height + 14
        + StepListHeight;

    /// <summary>步骤圆点的直径（与 <see cref="StepList"/> 内部保持一致）。</summary>
    private const int DotRow = 22;

    private static int StepListHeight
    {
        get
        {
            var rowBase = Math.Max(DotRow, Theme.Base.Height + 8);
            var gap = 10;

            // 5 行，其中一行要额外容纳"当前步骤的接口路径"那一行说明
            return (rowBase + gap) * StepTitles.Length + Theme.MonoSmall.Height + 6;
        }
    }

    private Control BuildTaskCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Title = "当前任务 · 领克 APP 签到",
            Note = "手动触发 / MANUAL",
            Margin = Padding.Empty,
            Padding = new Padding(Theme.CardPadding, 18, Theme.CardPadding, 16),
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.CardBg,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
        // 这一行装的是「3 / 5 步骤 · 60%」：高度必须按 Small 字体现算，
        // 写死 20 在 150% 缩放下连字都装不下，只会露出一条被裁掉一半的横线。
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Small.Height + 9));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        // Margin 清零：TableLayoutPanel 默认给每个控件留 3px 边距，
        // 8px 的行高被扣掉上下各 3px，进度条就只剩 2px —— 看起来像一条发丝，而不是"进度"。
        _runProgress = new ThinProgress { Dock = DockStyle.Fill, Margin = Padding.Empty };
        grid.Controls.Add(_runProgress, 0, 0);

        var labels = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardBg,
            // Margin 不清零的话 TableLayoutPanel 会扣掉默认的 3px 上下留白，
            // 20px 的行只剩 14px，那行「3 / 5 步骤」就被裁掉一半。
            Margin = Padding.Empty,
        };

        _runStepText = new Label
        {
            Text = "0 / 5 步骤 · 0%",
            AutoSize = true,
            Font = Theme.Small,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            Location = new Point(0, 3),
        };

        _runEta = new Label
        {
            Text = "尚未开始",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Font = Theme.Small,
            ForeColor = Theme.TextFaint,
            BackColor = Color.Transparent,
            Dock = DockStyle.Right,
            Width = 150,
        };

        labels.Controls.Add(_runStepText);
        labels.Controls.Add(_runEta);
        grid.Controls.Add(labels, 0, 1);

        _steps = new StepList { Dock = DockStyle.Fill };
        _steps.SetSteps(StepTitles, _stepDetails);
        _steps.CurrentIndex = 0;
        grid.Controls.Add(_steps, 0, 3);

        card.Controls.Add(grid);
        return card;
    }

    private Control BuildRunLogCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Title = "运行输出",
            Note = "自动滚动",
            // 这一块是深色终端（设计稿 02）：它是"正在跑的实时输出"，
            // 和状态页那张白底日志卡不是同一个东西，所以用 Term* 而不是 Log*。
            Fill = Theme.TermBg,
            BorderColor = Theme.TermBorder,
            Padding = new Padding(16, 14, 10, 10),
            Margin = Padding.Empty,
        };

        _runLogBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.TermBg,
            ForeColor = Theme.TermText,
            Font = Theme.Log,
            DetectUrls = false,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.ForcedVertical,
            HideSelection = false,
        };

        var bar = new SlimScrollBar(Theme.ScrollThumbDark, Theme.ScrollThumbDarkHover)
        {
            BackColor = Theme.TermBg,
        };

        var host = new ScrollHost(_runLogBox, bar);
        bar.Attach(_runLogBox);

        card.Controls.Add(host);

        // 空态：刚打开这一页、还没跑过时，面板里一整块纯黑会让人以为界面坏了。
        // 这里叠一层提示文字，而不是往日志框里塞一行假日志 ——
        // 日志框里的每一行都必须是真的输出，否则「复制全部」就把提示也带走了。
        _runLogEmpty = new Label
        {
            Text = "本次会话还没有输出 · 点右上角「立即签到」后这里会实时滚动",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = Theme.Small,
            ForeColor = Theme.TermFaint,
            // 必须显式给底色：Label 的"透明"画的是父容器的底色，
            // 而这层深色是卡片 OnPaint 里刷的，父容器的 BackColor 并不是它。
            BackColor = Theme.TermBg,
        };

        card.Controls.Add(_runLogEmpty);
        _runLogEmpty.BringToFront();

        return card;
    }

    // ==================================================================
    //  运行期状态
    // ==================================================================

    /// <summary>日志里出现 <c>[n/5]</c> 时点亮对应步骤（可跨线程调用）。</summary>
    private void OnStepSeen(int index, string detail)
    {
        void Apply()
        {
            if (IsDisposed || _steps is null) return;

            var i = Math.Clamp(index - 1, 0, StepTitles.Length - 1);
            _stepDetails[i] = detail;
            _runStepsSeen.Add(index);
            _runStep = Math.Max(_runStep, index);

            _steps.SetSteps(StepTitles, _stepDetails);
            _steps.CurrentIndex = Math.Min(_runStep, StepTitles.Length);
            _runProgress.Value = (double)_runStep / StepTitles.Length;
            _runStepText.Text = $"{_runStep} / {StepTitles.Length} 步骤 · {(int)Math.Round(100.0 * _runStep / StepTitles.Length)}%";

            UpdateRunningChrome();
        }

        if (IsHandleCreated && InvokeRequired)
        {
            try
            {
                BeginInvoke(Apply);
            }
            catch (InvalidOperationException)
            {
                // 窗口正在销毁
            }

            return;
        }

        Apply();
    }

    /// <summary>开始一次运行：清空上一轮的步骤与输出，切到「签到进行中」页。</summary>
    private void BeginRunning(string trigger)
    {
        _runStartedAt = DateTime.Now;
        _runStep = 0;
        _runStepsSeen.Clear();
        Array.Clear(_stepDetails);

        if (_steps is not null)
        {
            _steps.SetSteps(StepTitles, _stepDetails);
            _steps.CurrentIndex = 0;
            _steps.Invalidate();
        }

        if (_runProgress is not null) _runProgress.Value = 0;
        if (_runStepText is not null) _runStepText.Text = $"0 / {StepTitles.Length} 步骤 · 0%";
        if (_runLogBox is not null) _runLogBox.Clear();

        _hdrRunning.Subtitle = $"{trigger} · {_runStartedAt:HH:mm:ss} 开始";

        _pillRun.SetState("运行中 · 步骤 0 / 5", Theme.Accent, Theme.AccentSoft);
        _btnCancel.Enabled = true;
        _btnRunBusy.Text = "签到中";

        _nav.SelectedIndex = 1;
    }

    /// <summary>定时器每 2 秒调一次：把「已用时」与胶囊上的步骤号刷新一下。</summary>
    private void UpdateRunningChrome()
    {
        if (_runStepText is null) return;

        var running = _host.State.Running;

        if (running)
        {
            var elapsed = DateTime.Now - _runStartedAt;
            _runEta.Text = $"已用时 {elapsed.TotalSeconds:0.0} 秒";
            _pillRun.SetState($"运行中 · 步骤 {_runStep} / {StepTitles.Length}", Theme.Accent, Theme.AccentSoft);
        }
        else if (_runStartedAt != default)
        {
            var elapsed = DateTime.Now - _runStartedAt;
            _runEta.Text = $"用时 {elapsed.TotalSeconds:0.0} 秒";

            var rc = _host.State.LastRc;
            if (rc is null) return;

            if (rc == 0)
            {
                _pillRun.SetState("已完成", Theme.Success, Theme.SuccessSoft);
                _hdrRunning.Subtitle = $"已完成 · {_host.State.LastFinish} · 用时 {elapsed.TotalSeconds:0.0} 秒";
            }
            else
            {
                _pillRun.SetState($"失败 · 退出码 {rc}", Theme.Danger, Theme.DangerSoft);
                _hdrRunning.Subtitle = $"失败 · {_host.State.LastFinish} · 退出码 {rc}";
            }
        }
    }

    private void CancelRunning()
    {
        try
        {
            _runCts?.Cancel();
            _btnCancel.Enabled = false;
            EnqueueLog("[!] 已请求取消，等当前请求返回后停止");
        }
        catch (ObjectDisposedException)
        {
            // 已经跑完了，点一下取消不会有副作用
        }
    }
}
