using System.Globalization;
using System.Text;
using LynkSign.Services;
using LynkSign.WinForms.Ui;

namespace LynkSign.WinForms.Forms;

/// <summary>
/// 「签到记录」页：月历 + 本月统计 + 最近积分变化。
///
/// <para>
/// <b>数据从哪儿来</b>（这一点很关键，不要凭空造数据）：
/// </para>
/// <list type="number">
///   <item>今天的签到状态与连续天数来自日志里的状态行（<c>SignStatusLine</c>）；
///         连续 N 天意味着今天往前 N-1 天都签过，据此回填历史格子。</item>
///   <item>积分变化的日期来自 <c>points-history.log</c>：某天积分/成长值确实涨了，
///         那天必然签到了（签到是唯一稳定加分的动作）。</item>
///   <item>两者都没有的日子画成空心圈 = 「无记录」，而不是「未签到」——
///         程序没开过的那天我们并不知道签没签，不替用户下结论。</item>
/// </list>
///
/// <para>
/// 补签次数没有接口可查（只有「补签卡有几张」），所以统计里给的是卡数，不是使用次数。
/// </para>
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>已知签到的日期（连续天数回填 + 积分确实变化的日期）。</summary>
    private readonly HashSet<DateOnly> _signedDays = [];

    private bool _signedToday;
    private int _streakDays;
    private string _cardsText = "—";

    private Panel BuildHistoryPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PageBg,
            Padding = new Padding(Theme.PadX, 0, Theme.PadX, 14),
        };

        _hdrHistory = new PageHeader { Title = "签到记录", Subtitle = "还没有签到数据" };

        var prev = new IconButton { Icon = GlyphIcon.ChevronLeft, Margin = new Padding(0, 0, 6, 0) };
        prev.Click += (_, _) => ShiftMonth(-1);

        var monthChip = new ChipLabel
        {
            Caption = "—",
            DotPrefix = false,
            Tone = Theme.TextBody,
            Height = 38,
            Margin = new Padding(0, 0, 6, 0),
        };
        _monthChip = monthChip;
        var next = new IconButton { Icon = GlyphIcon.ChevronRight, Margin = new Padding(0, 0, 10, 0) };
        next.Click += (_, _) => ShiftMonth(1);

        var export = new FlatButton { Text = "导出 CSV", Kind = ButtonKind.Secondary, Margin = Padding.Empty };
        export.Click += (_, _) => ExportMonthCsv();

        _hdrHistory.Actions.Controls.Add(export);
        _hdrHistory.Actions.Controls.Add(next);
        _hdrHistory.Actions.Controls.Add(monthChip);
        _hdrHistory.Actions.Controls.Add(prev);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.PageBg,
            Margin = Padding.Empty,
        };
        // 64:36 —— 月历每格要落得下"两位数字 + 一个点"，右栏只要放得下两行统计
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62F));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38F));

        var left = BuildCalendarCard();
        left.Margin = new Padding(0, 0, CardGap / 2, 0);

        var right = BuildHistorySideBar();
        right.Margin = new Padding(CardGap / 2, 0, 0, 0);

        body.Controls.Add(left, 0, 0);
        body.Controls.Add(right, 1, 0);

        page.Controls.Add(body);
        page.Controls.Add(_hdrHistory);

        UpdateMonthLabels();
        return page;
    }

    private ChipLabel _monthChip = null!;

    private Control BuildCalendarCard()
    {
        _calendarCard = new CardPanel
        {
            Dock = DockStyle.Fill,
            Title = "—",
            Note = "● 已签到    ○ 无记录",
            Margin = Padding.Empty,
            Padding = new Padding(Theme.CardPadding, 16, Theme.CardPadding, 14),
        };

        _calendar = new MonthGrid { Dock = DockStyle.Fill, Margin = Padding.Empty };

        _calSummary = new Label
        {
            Text = "—",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Small,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
        };

        _calMakeup = new Label
        {
            Text = "",
            AutoSize = false,
            Width = 170,
            Dock = DockStyle.Right,
            TextAlign = ContentAlignment.MiddleRight,
            Font = Theme.Small,
            ForeColor = Theme.TextFaint,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
        };

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Theme.CardBg };
        footer.Controls.Add(_calSummary);
        footer.Controls.Add(_calMakeup);

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.CardBg,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        stack.Controls.Add(_calendar, 0, 0);
        stack.Controls.Add(footer, 0, 1);

        _calendarCard.Controls.Add(stack);
        return _calendarCard;
    }

    private Control BuildHistorySideBar()
    {
        var col = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.PageBg,
            Margin = Padding.Empty,
        };
        col.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        col.RowStyles.Add(new RowStyle(SizeType.Absolute, StatsCardHeight + CardGap));
        col.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        col.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

        col.Controls.Add(BuildStatsCard(), 0, 0);

        var recent = new CardPanel
        {
            Dock = DockStyle.Fill,
            Title = "最近记录",
            Note = "积分变化",
            Margin = new Padding(0, 0, 0, CardGap),
            Padding = new Padding(Theme.CardPadding, 16, Theme.CardPadding, 12),
        };

        _recentList = new RecordList { Dock = DockStyle.Fill };
        recent.Controls.Add(_recentList);

        col.Controls.Add(recent, 0, 1);

        // 高度按按钮自己的首选高度：所在行是 Percent 行，Dock.Fill 会被压到 32，
        // 比首选矮 6px，字就被垂直切掉一点。
        var all = new FlatButton
        {
            Text = "查看全部记录",
            Kind = ButtonKind.Secondary,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(0, 38),
        };
        all.Click += (_, _) => OpenHistoryFile();
        col.Controls.Add(all, 0, 2);

        return col;
    }

    private static int StatsCardHeight =>
        CardPanel.TitleHeight + 16 * 2 + Theme.Display.Height + 10 + 6 + 12 + (Theme.Base.Height + 14) * 2;

    private Control BuildStatsCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Title = "本月统计",
            Margin = Padding.Empty,
            Padding = new Padding(Theme.CardPadding, 16, Theme.CardPadding, 14),
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = Theme.CardBg,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Display.Height + 4));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 6 + 10));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Base.Height + 14));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _statBig = new Label
        {
            Text = "—",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Font = Theme.Display,
            ForeColor = Theme.TextStrong,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
        };

        _statRange = new Label
        {
            Text = "—",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomRight,
            Font = Theme.MonoSmall,
            ForeColor = Theme.TextFaint,
            BackColor = Color.Transparent,
        };

        _statProgress = new ThinProgress { Dock = DockStyle.Fill, Value = 0, Margin = new Padding(0, 4, 0, 4) };
        _statProgress.TrackColor = Theme.Mix(Theme.Accent, Color.White, 0.86);

        grid.Controls.Add(_statBig, 0, 0);
        grid.Controls.Add(_statRange, 1, 0);
        grid.SetColumnSpan(_statProgress, 2);
        grid.Controls.Add(_statProgress, 0, 1);

        var rateCaption = new Label
        {
            Text = "出勤率",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Small,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
        };

        _statRate = new Label
        {
            Text = "—",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Font = Theme.Mono,
            ForeColor = Theme.TextBody,
            BackColor = Color.Transparent,
        };

        var cardsCaption = new Label
        {
            Text = "补签卡",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Small,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
        };

        _statCards = new Label
        {
            Text = "—",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Font = Theme.Mono,
            ForeColor = Theme.TextBody,
            BackColor = Color.Transparent,
        };

        grid.Controls.Add(rateCaption, 0, 2);
        grid.Controls.Add(_statRate, 1, 2);
        grid.Controls.Add(cardsCaption, 0, 3);
        grid.Controls.Add(_statCards, 1, 3);

        card.Controls.Add(grid);
        return card;
    }

    // ==================================================================
    //  刷新
    // ==================================================================

    private void ShiftMonth(int delta)
    {
        _monthCursor = _monthCursor.AddMonths(delta);
        UpdateMonthLabels();
        RefreshHistory();
    }

    private void UpdateMonthLabels()
    {
        var text = $"{_monthCursor:yyyy 年 M 月}";
        _monthChip.Caption = text;
        _monthChip.Width = _monthChip.PreferredSize.Width;
        _calendarCard.Title = text;
        _calendar.Month = DateOnly.FromDateTime(_monthCursor);
    }

    /// <summary>把已知签到日期、本月统计、最近记录一起刷一遍（页面可见时才做）。</summary>
    private void RefreshHistory()
    {
        if (IsDisposed || _calendar is null) return;

        try
        {
            RebuildSignedDays();

            var first = DateOnly.FromDateTime(_monthCursor);
            var days = DateTime.DaysInMonth(first.Year, first.Month);
            var last = new DateOnly(first.Year, first.Month, days);
            var today = DateOnly.FromDateTime(DateTime.Today);

            _calendar.SetMarks(_signedDays);

            var signedInMonth = _signedDays.Count(d => d >= first && d <= last);
            var elapsed = today < first ? 0 : today > last ? days : today.Day;

            _statBig.Text = $"{signedInMonth}/{days}";
            _statRange.Text = $"{first:MM-dd} ~ {last:MM-dd}";
            _statProgress.Value = days == 0 ? 0 : (double)signedInMonth / days;
            _statRate.Text = elapsed == 0 ? "—" : $"{100.0 * signedInMonth / elapsed:0.#}%";
            _statCards.Text = _cardsText;

            _calSummary.Text = $"{first:M 月}共 {days} 天 · 已记录 {signedInMonth} 天"
                + (_streakDays > 0 ? $" · 连续 {_streakDays} 天" : "");
            _calMakeup.Text = "";

            _hdrHistory.Subtitle = _streakDays > 0 || _signedToday
                ? $"连续 {_streakDays} 天 · 补签卡 {_cardsText} · 本月已记录 {signedInMonth} 天"
                : "还没有签到数据 · 跑一次签到后这里会有记录";

            RenderRecentRecords();
        }
        catch
        {
            // 记录页是只读展示，读文件失败不该影响别的任何功能
        }
    }

    /// <summary>
    /// 重建「哪些天签过」的集合。
    ///
    /// <para>
    /// 只从<b>已有证据</b>推：今天的签到状态 + 连续天数（回填），以及积分确实涨过的日期。
    /// 故意不写任何"造数据"的逻辑 —— 界面上多画一个绿点，就是在替用户编造他签过到。
    /// </para>
    /// </summary>
    private void RebuildSignedDays()
    {
        _signedDays.Clear();

        if (_signedToday)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            _signedDays.Add(today);

            for (var i = 1; i < _streakDays; i++) _signedDays.Add(today.AddDays(-i));
        }

        foreach (var record in _host.Points.Read(0))
        {
            if ((record.DeltaPoint ?? 0) <= 0 && (record.DeltaGrowth ?? 0) <= 0) continue;

            if (DateTime.TryParse(record.Time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                _signedDays.Add(DateOnly.FromDateTime(time));
            }
        }
    }

    /// <summary>从日志状态行拿到「今天签了 + 连续 N 天」时调用。</summary>
    private void RecordSignedToday(int streak)
    {
        _signedToday = true;
        if (streak > 0) _streakDays = streak;

        if (_pageHistory is { Visible: true }) RefreshHistory();
    }

    private void RenderRecentRecords()
    {
        var records = _host.Points.Read(60)
            .Where(r => r.HasChange)
            .Take(8)
            .ToList();

        var rows = new List<(string Left, string Right, Color Tone)>();

        foreach (var record in records)
        {
            var delta = record.DeltaPoint ?? record.DeltaGrowth ?? 0;
            var label = record.DeltaPoint is { } dp && dp != 0
                ? $"{dp:+#;-#;0} 积分"
                : $"{(record.DeltaGrowth is { } dg ? dg : 0):+#;-#;0} 成长值";

            rows.Add((record.ShortTime, label, delta >= 0 ? Theme.Accent : Theme.Danger));
        }

        _recentList.EmptyText = records.Count == 0
            ? "还没有积分变化记录 —— 跑一次签到后就有了"
            : "";
        _recentList.SetRows(rows);
    }

    private void OpenHistoryFile()
    {
        try
        {
            var path = _host.Points.FilePath;
            if (!File.Exists(path))
            {
                EnqueueLog("[!] 还没有积分历史文件，先跑一次签到");
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            EnqueueLog($"[!] 打不开记录文件: {ex.Message}");
        }
    }

    /// <summary>
    /// 把当前月的签到情况导成 CSV。
    ///
    /// <para>
    /// 用 GB18030 写盘：Excel 打开 UTF-8 的中文 CSV 常常是乱码，
    /// 而这份文件的第一用途就是"拖进 Excel 给老板看"。
    /// </para>
    /// </summary>
    private void ExportMonthCsv()
    {
        var first = DateOnly.FromDateTime(_monthCursor);
        var days = DateTime.DaysInMonth(first.Year, first.Month);

        using var dialog = new SaveFileDialog
        {
            Title = "导出签到记录",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"lynk-sign-{first:yyyy-MM}.csv",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("日期,星期,状态");

            for (var day = 1; day <= days; day++)
            {
                var date = new DateOnly(first.Year, first.Month, day);
                var state = _signedDays.Contains(date) ? "已签到" : "无记录";

                sb.AppendLine($"{date:yyyy-MM-dd},{WeekdayName(date.DayOfWeek)},{state}");
            }

            File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.GetEncoding("GB18030"));
            EnqueueLog($"[OK] 已导出 {first:yyyy-MM} 签到记录: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            EnqueueLog($"[X] 导出失败: {ex.Message}");
            MessageBox.Show($"导出失败：\n{ex.Message}", Program.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string WeekdayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };
}
