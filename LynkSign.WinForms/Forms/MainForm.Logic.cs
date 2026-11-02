using System.Text;
using System.Text.RegularExpressions;
using LynkSign.Configuration;
using LynkSign.Infrastructure;
using LynkSign.Logging;
using LynkSign.Lynk;
using LynkSign.Scheduling;
using LynkSign.Services;
using LynkSign.WinForms.App;
using LynkSign.WinForms.Ui;
using Microsoft.Extensions.Hosting;

namespace LynkSign.WinForms.Forms;

/// <summary>
/// MainForm 的行为部分：配置读写、手动运行、调度器生命周期、状态卡片刷新。
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>
    /// 从日志里摘「签到状态」那一行，用来填顶部状态卡片。
    ///
    /// <para>
    /// 格式与正则都住在 <see cref="SignStatusLine"/> 里，这里只引用不重写 ——
    /// 两边各留一份的话，改了其中一头就会变成「签到成功但卡片不动」，
    /// 而且那种错不报异常、只能靠人眼在某天发现。
    /// </para>
    /// </summary>
    private static Regex StatusPattern => SignStatusLine.Pattern;

    /// <summary>最近一次账户快照（喂「账户」卡与积分余额卡）。</summary>
    private AccountSnapshot? _account;

    // ==================================================================
    //  启动
    // ==================================================================

    private void OnFormLoad(object? sender, EventArgs e)
    {
        var (created, configPath, dataDir) = _host.PrepareStorage();
        _configPath = configPath;

        _sbDataDir.Text = $"数据目录：{dataDir}";
        if (_dataDirValue is not null) _dataDirValue.Text = dataDir;

        EnqueueLog($"[INFO] 数据目录: {dataDir}");
        EnqueueLog($"[INFO] 配置文件: {configPath}{(created ? "  (首次运行, 已生成默认配置)" : "")}");
        EnqueueLog($"[INFO] 日志目录: {_host.Logs.Dir}  (保留 {RunLogStore.RetentionDays} 天)");
        EnqueueLog($"[INFO] token 缓存: {_host.TokenCache.Path}");
        EnqueueLog($"[INFO] 时区: {TimeUtil.Tz.Id}");

        RestoreWindowState();

        LoadConfigIntoForm();
        EnqueueLog($"[INFO] 出站网络: {_host.NetworkMode}");
        RestoreStatusFromLatestLog();
        RefreshPoints(force: true);
        RefreshStatus();
        RefreshHistory();
        StartScheduler();

        EnqueueLog("[INFO] 就绪。填好 refreshToken 后点「立即签到」试跑一次，之后按 cron 自动执行。");

        if (_startMinimized) HideToTray();
    }

    // ==================================================================
    //  窗口尺寸偏好（ui-state.json）
    // ==================================================================

    /// <summary>
    /// 当前 DPI 相对 96 的缩放比（换算与回归都在 <see cref="UiStateStore"/> 里，
    /// 这里只是把当前窗体的 DPI 喂进去）。
    /// </summary>
    private double UiScale => UiStateStore.NormalizeScale(DeviceDpi);

    /// <summary>
    /// 应用上次记住的窗口尺寸；没记过就用默认尺寸。
    ///
    /// <para>
    /// 必须放在 <c>Load</c> 里：此时 DPI 自动缩放已经跑完，此刻设的就是最终设备像素；
    /// 放在构造函数里会被 <c>PerformAutoScale</c> 再放大一次。
    /// </para>
    ///
    /// <para>
    /// <b>默认值和记忆值走同一条换算路径</b>，这点很关键：构造期设的 <c>ClientSize</c>
    /// 不会被 <c>AutoScaleMode.Dpi</c> 缩放（没有设计器基准），所以它是"设备像素"；
    /// 而这里统一按"逻辑像素 × 缩放比"落地。
    /// </para>
    /// </summary>
    private void RestoreWindowState()
    {
        if (!_rememberWindowSize) return;

        var state = UiStateStore.Load();
        var remembered = state.WindowWidth > 0 && state.WindowHeight > 0;

        var work = Screen.PrimaryScreen?.WorkingArea.Size ?? Size.Empty;
        var logicalWork = work.IsEmpty
            ? new Size(UiStateStore.MaxSide, UiStateStore.MaxSide)
            : UiStateStore.ToLogical(work, UiScale);

        var wanted = remembered
            ? new Size(state.WindowWidth, state.WindowHeight)
            : new Size(UiStateStore.DefaultWidth, UiStateStore.DefaultHeight);

        var logical = UiStateStore.Clamp(wanted.Width, wanted.Height, logicalWork);
        if (logical.IsEmpty) logical = wanted;

        ClientSize = UiStateStore.ToDevice(logical, UiScale);

        if (state.WindowMaximized) WindowState = FormWindowState.Maximized;

        var clamped = logical != wanted;
        EnqueueLog($"[INFO] 窗口尺寸: {ClientSize.Width}x{ClientSize.Height} "
                   + (remembered ? "沿用上次" : "默认值")
                   + (state.WindowMaximized ? "，最大化" : "")
                   + (clamped ? $"，已从 {wanted.Width}x{wanted.Height} 夹到屏幕内" : "")
                   + (UiScale > 1.001 ? $"，缩放 {UiScale:0.##}x" : ""));
    }

    /// <summary>把当前尺寸记进 <c>ui-state.json</c>（退出/缩托盘时调）。</summary>
    private void SaveWindowState()
    {
        if (!_rememberWindowSize) return;

        var normal = _lastNormalClientSize.IsEmpty ? ClientSize : _lastNormalClientSize;
        if (normal.IsEmpty) return;

        var logical = UiStateStore.ToLogical(normal, UiScale);
        var state = new UiState
        {
            WindowWidth = logical.Width,
            WindowHeight = logical.Height,
            WindowMaximized = WindowState == FormWindowState.Maximized,
        };

        if (!UiStateStore.Save(state))
        {
            Console.WriteLine("[!] 窗口尺寸没能写进 ui-state.json（不影响使用）");
        }
    }

    /// <summary>记录「非最大化」状态的尺寸：最大化/最小化时报的尺寸不是用户调的，不能记。</summary>
    private void TrackNormalSize()
    {
        if (WindowState == FormWindowState.Normal && !ClientSize.IsEmpty)
        {
            _lastNormalClientSize = ClientSize;
        }
    }

    // ==================================================================
    //  配置读写
    // ==================================================================

    private void LoadConfigIntoForm()
    {
        _loadingConfig = true;
        try
        {
            FillForm(_host.Config.Read());
        }
        finally
        {
            _loadingConfig = false;
        }
    }

    /// <summary>把一份配置填进表单（不落盘）。</summary>
    private void FillForm(AppConfig cfg)
    {
        var push = cfg.Push ?? new PushConfig();

        _txtRefreshToken.Text = cfg.RefreshToken;
        _txtDeviceId.Text = cfg.DeviceId;
        _txtCaNativeKey.Text = cfg.CaNativeKey;
        _txtCaNativeSecret.Text = cfg.CaNativeSecret;
        _txtCron.Text = cfg.Cron;
        _txtContentId.Text = cfg.ShareContentId;

        // 一个两态配置值 ↔ 一个开关
        _chkAutoArticleId.Checked = ShareContentModes.Parse(cfg.ShareContentMode) == ShareContentMode.Auto;

        _chkAutoShare.Checked = cfg.AutoShare;

        // 一个三态配置值 ↔ 两个开关，换算集中在一处
        LoadOpenShareUrl(cfg.OpenShareUrl);

        _txtTokenB.Text = string.Join(Environment.NewLine, cfg.TokenB ?? []);

        _txtWecom.Text = push.WecomWebhook;
        _txtDingtalk.Text = push.DingtalkWebhook;
        _txtFeishu.Text = push.FeishuWebhook;
        _txtTgBotToken.Text = push.TgBotToken;
        _txtTgChatId.Text = push.TgChatId;
        _txtServerChan.Text = push.ServerChanKey;
        _txtPushPlus.Text = push.PushPlusToken;
        _txtBark.Text = push.BarkUrl;

        LoadProxyMode(cfg.HttpProxy);

        var autostart = WinAutostart.IsEnabled;
        _chkAutostart.Checked = autostart;
        _trayAutostart.Checked = autostart;

        UpdateCronPreview();
    }

    private static string Tail(string token) => token.Length <= 4 ? token : token[^4..];

    private static string Shorten(string? text, int max)
        => string.IsNullOrEmpty(text) ? "—" : text.Length <= max ? text : text[..max] + "…";

    /// <summary>把界面上的值写回 config.json。返回是否成功。</summary>
    private bool SaveConfigFromForm(bool showFeedback)
    {
        try
        {
            var cfg = new AppConfig
            {
                RefreshToken = _txtRefreshToken.Text.Trim(),
                DeviceId = _txtDeviceId.Text.Trim(),
                CaNativeKey = _txtCaNativeKey.Text.Trim(),
                CaNativeSecret = _txtCaNativeSecret.Text.Trim(),
                Cron = _txtCron.Text.Trim(),
                AutoShare = _chkAutoShare.Checked,
                OpenShareUrl = CurrentOpenShareUrl().ToConfigValue(),
                TokenB = [.. _txtTokenB.Lines.Select(l => l.Trim()).Where(l => l.Length > 0)],
                ShareContentId = _txtContentId.Text.Trim(),
                ShareContentMode = (_chkAutoArticleId.Checked ? ShareContentMode.Auto : ShareContentMode.Fixed).ToConfigValue(),
                HttpProxy = CurrentProxyValue(),
                Push = new PushConfig
                {
                    WecomWebhook = _txtWecom.Text.Trim(),
                    DingtalkWebhook = _txtDingtalk.Text.Trim(),
                    FeishuWebhook = _txtFeishu.Text.Trim(),
                    TgBotToken = _txtTgBotToken.Text.Trim(),
                    TgChatId = _txtTgChatId.Text.Trim(),
                    ServerChanKey = _txtServerChan.Text.Trim(),
                    PushPlusToken = _txtPushPlus.Text.Trim(),
                    BarkUrl = _txtBark.Text.Trim(),
                },
            };

            _host.Config.Write(cfg);

            if (showFeedback)
            {
                var saved = _host.Config.Read();
                EnqueueLog($"[OK] 配置已保存: {_configPath}");
                EnqueueLog($"[INFO] 下次运行: {CronSchedule.NextText(saved.Cron) ?? "cron 表达式无效"}");

                if (_saveHint is not null)
                {
                    _saveHint.Text = $"已保存 · {DateTime.Now:HH:mm:ss}";
                    _saveHint.ForeColor = Theme.Success;
                }

                RefreshStatus();
            }

            return true;
        }
        catch (Exception ex)
        {
            EnqueueLog($"[X] 保存配置失败: {ex.Message}");

            if (_saveHint is not null)
            {
                _saveHint.Text = $"保存失败 · {ex.Message}";
                _saveHint.ForeColor = Theme.Danger;
            }

            if (showFeedback)
            {
                MessageBox.Show($"保存配置失败：\n{ex.Message}", Program.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return false;
        }
    }

    // ==================================================================
    //  签到后自动打开分享链接（一个三态配置值 ↔ 两个开关）
    // ==================================================================

    /// <summary>
    /// 两个开关当前的组合换算成三态配置值。
    ///
    /// <para>
    /// 第二档只在第一档开着时才有意义，所以「第二档开着但第一档没开」= 不打开 ——
    /// 这样用户临时关掉自动打开、过两天再打开时，不用把「定时任务也打开」重新开一遍。
    /// </para>
    /// </summary>
    private ShareUrlOpenMode CurrentOpenShareUrl()
    {
        if (!_chkOpenShareUrl.Checked) return ShareUrlOpenMode.Off;
        return _chkOpenShareAlways.Checked ? ShareUrlOpenMode.Always : ShareUrlOpenMode.Manual;
    }

    /// <summary>把配置里的三态值回填到两个开关（含第二档的可用状态）。</summary>
    private void LoadOpenShareUrl(string? raw)
    {
        var mode = ShareUrlOpenModes.Parse(raw);

        _chkOpenShareUrl.Checked = mode != ShareUrlOpenMode.Off;
        _chkOpenShareAlways.Checked = mode == ShareUrlOpenMode.Always;
        _chkOpenShareAlways.Enabled = _chkOpenShareUrl.Checked;
    }

    /// <summary>第一档变化时联动第二档的可用状态（值本身保留，理由见 <see cref="CurrentOpenShareUrl"/>）。</summary>
    private void OnOpenShareUrlChanged(object? sender, EventArgs e)
    {
        if (_loadingConfig) return;
        _chkOpenShareAlways.Enabled = _chkOpenShareUrl.Checked;
    }

    private void UpdateCronPreview()
    {
        if (_cronPreview is null) return;

        var expr = _txtCron.Text.Trim();

        if (CronExpression.TryParse(expr, out _))
        {
            _cronPreview.ForeColor = Theme.TextMuted;
            _cronPreview.Text = $"下次运行  {CronSchedule.NextText(expr) ?? "—"}";
        }
        else
        {
            _cronPreview.ForeColor = Theme.Danger;
            _cronPreview.Text = "cron 表达式无效（需要 5 段：分 时 日 月 周）";
        }
    }

    // ==================================================================
    //  调度
    // ==================================================================

    private void StartScheduler()
    {
        _schedulerCts = new CancellationTokenSource();

        try
        {
            // BackgroundService.StartAsync 内部把 ExecuteAsync 丢到后台后就返回，
            // 所以这里不会阻塞 UI 线程。
            _ = ((IHostedService)_host.Scheduler).StartAsync(CancellationToken.None);
            EnqueueLog("[INFO] cron 调度器已启动 (每 20 秒检查一次, 改完 cron 立即生效)");
            EnqueueLog($"[INFO] 每天 {SignScheduler.DailyRefreshTime:HH\\:mm} 自动刷新一次签到状态 "
                + "(只读: 不签到 / 不推送; 启动时若已过该时间会补刷一次)");
        }
        catch (Exception ex)
        {
            EnqueueLog($"[X] 调度器启动失败: {ex.Message}");
        }
    }

    // ==================================================================
    //  按钮 / 开关
    // ==================================================================

    private async void OnRunNowClick(object? sender, EventArgs e)
    {
        if (_host.State.Running)
        {
            MessageBox.Show("已有任务在运行中，请等它跑完。", Program.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 先把界面上的最新配置落盘，保证「改完直接点」用的是新值
        if (!SaveConfigFromForm(showFeedback: false)) return;

        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();

        BeginRunning("手动触发");

        EnqueueLog(new string('=', 52));
        EnqueueLog($"[INFO] 手动触发签到 · {TimeUtil.Stamp()}");

        try
        {
            var options = SettingsResolver.ResolveFromConfig(_host.Config.Read());
            var outcome = await _host.Runner
                .RunAsync("manual", options, writeLogFile: true, _runCts.Token)
                .ConfigureAwait(true);

            if (outcome.SkippedReason is not null)
            {
                EnqueueLog($"[!] {outcome.SkippedReason}");
                _pillRun.SetState("已跳过", Theme.Warning, Theme.WarningSoft);
            }
            else
            {
                EnqueueLog($"[INFO] 本次运行结束, 退出码 {outcome.ExitCode}, 日志 {outcome.LogName}");

                // 正常跑完 = 五步都走过了，把最后一步也点亮（第 5 步常常是"未启用"）
                if (outcome.ExitCode == 0)
                {
                    _runStep = StepTitles.Length;
                    _steps.CurrentIndex = StepTitles.Length;
                    _steps.Invalidate();
                    _runProgress.Value = 1;
                    _runStepText.Text = $"{StepTitles.Length} / {StepTitles.Length} 步骤 · 100%";
                }
            }
        }
        catch (OperationCanceledException)
        {
            EnqueueLog("[!] 已取消本次签到");
        }
        catch (Exception ex)
        {
            CrashReport.Show(ex, "手动签到");
        }
        finally
        {
            _btnCancel.Enabled = false;
            RefreshStatus();
            RefreshPoints(force: true);
            RefreshHistory();
            UpdateRunningChrome();
        }
    }

    /// <summary>
    /// 「刷新状态」图标按钮：只读地拉一次 <c>day/info</c> 与账户信息。
    ///
    /// <para>
    /// 与「立即签到」分开，是因为它们的目的完全不同 —— 这个动作<b>不会真的签到</b>，
    /// 只是把界面上的数字（今天签没签、连续几天、积分多少）对齐到服务端。
    /// </para>
    /// </summary>
    private async void OnRefreshStatusClick()
    {
        if (_host.State.Running)
        {
            EnqueueLog("[!] 任务正在运行，等它跑完再刷新状态");
            return;
        }

        _btnRefresh.Enabled = false;
        try
        {
            EnqueueLog(new string('=', 52));
            EnqueueLog("[INFO] 手动刷新签到状态（只读：不签到、不推送）...");

            var outcome = await _host.Runner
                .RunStatusRefreshAsync("manual-refresh", writeLogFile: false)
                .ConfigureAwait(true);

            if (outcome.SkippedReason is { } reason) EnqueueLog($"[!] {reason}");
            else EnqueueLog($"[OK] 状态刷新完成, 退出码 {outcome.ExitCode}");
        }
        catch (Exception ex)
        {
            CrashReport.Show(ex, "刷新签到状态");
        }
        finally
        {
            _btnRefresh.Enabled = true;
            RefreshStatus();
            RefreshPoints(force: true);
            RefreshHistory();
        }
    }

    private void OnClearCacheClick(object? sender, EventArgs e)
    {
        try
        {
            var removed = _host.TokenCache.Clear();
            EnqueueLog($"[OK] accessToken 缓存已清空: {removed}");
            EnqueueLog("[INFO] 下次签到会强制走一次 refresh 换取新 token。");

            if (_saveHint is not null)
            {
                _saveHint.Text = $"token 缓存已清空（{removed}）";
                _saveHint.ForeColor = Theme.TextMuted;
            }
        }
        catch (Exception ex)
        {
            EnqueueLog($"[X] 清空缓存失败: {ex.Message}");
        }
    }

    /// <summary>「从抓包导入」：选一个抓包文件，把 refreshToken + deviceId 抠出来填好并保存。</summary>
    private void OnImportHarClick(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择领克 APP 的抓包文件",
            Filter = "抓包文件 (*.har)|*.har|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK) ApplyHarImport(dialog.FileName);
    }

    /// <summary>
    /// 解析抓包文件并把凭据写回配置。返回是否成功。
    ///
    /// <para>
    /// 独立成方法而不是写在按钮回调里，是为了让它<b>不依赖文件对话框</b> ——
    /// 命令行入口（<c>--import-har</c>）走的是同一套解析，测试时也不必真的去点界面。
    /// </para>
    /// </summary>
    private bool ApplyHarImport(string harPath)
    {
        EnqueueLog(new string('=', 52));
        EnqueueLog($"[INFO] 从抓包导入凭据: {System.IO.Path.GetFileName(harPath)}");

        var scan = HarCredentialExtractor.ScanFile(harPath);

        if (!scan.Ok)
        {
            SetHarPreview("导入失败", Theme.Danger);
            EnqueueLog($"[X] 导入失败: {scan.Error}");
            if (scan.MatchedEntries > 0)
                EnqueueLog($"[!] 抓包里找到 {scan.MatchedEntries} 条登录请求, 但都没能取出完整凭据");
            return false;
        }

        var credential = scan.Credential!;

        // 只改这两个字段，而不是把整个表单写回：导入是个精准动作，
        // 不该顺手覆盖掉用户在界面上改了一半的其他设置。
        var (saved, info) = _host.Config.UpdateField(cfg =>
        {
            cfg.RefreshToken = credential.RefreshToken;
            cfg.DeviceId = credential.DeviceId;
        });

        _loadingConfig = true;
        try
        {
            _txtRefreshToken.Text = credential.RefreshToken;
            _txtDeviceId.Text = credential.DeviceId;
        }
        finally
        {
            _loadingConfig = false;
        }

        if (!saved)
        {
            SetHarPreview("已填好但保存失败", Theme.Danger);
            EnqueueLog($"[X] 写入 config.json 失败: {info}");
            return false;
        }

        SetHarPreview($"已导入 {credential.MaskedMobile}", Theme.Success);
        EnqueueLog($"[OK] 已导入并保存: {credential.Describe()}");
        EnqueueLog($"     deviceId={credential.DeviceId}");

        if (scan.MatchedEntries > 1)
            EnqueueLog($"[!] 抓包里有 {scan.MatchedEntries} 条登录记录, 已采用最后一条（最新那次登录）");

        EnqueueLog($"[INFO] 已写入 {_configPath}");
        EnqueueLog("[!] 抓包文件里含明文 token, 导入完成后建议把它删掉");

        RefreshStatus();
        return true;
    }

    /// <summary>
    /// 导入结果的即时回显：写在「导入文件」按钮左边，几秒后自己清空。
    /// 不弹 MessageBox —— 导入是高频动作，每次都点「确定」很烦。
    /// </summary>
    private void SetHarPreview(string text, Color color)
    {
        if (IsDisposed || _importEcho is null) return;

        _importEcho.Text = text;
        _importEcho.ForeColor = color;

        _echoTimer ??= new System.Windows.Forms.Timer { Interval = 8000 };
        _echoTimer.Stop();
        _echoTimer.Tick -= OnEchoTimeout;
        _echoTimer.Tick += OnEchoTimeout;
        _echoTimer.Start();
    }

    private void OnEchoTimeout(object? sender, EventArgs e)
    {
        _echoTimer?.Stop();

        if (IsDisposed || _importEcho is null || _importEcho.IsDisposed) return;

        _importEcho.Text = "";
    }

    /// <summary>
    /// 「拉取一次」：用当前配置里的 refreshToken 实拉一次广场文章流，把结果摆在按钮旁边。
    ///
    /// <para>
    /// 纯诊断动作：<b>不改配置（除了必要的 token 轮换写回）、不执行签到</b>。
    /// 值得单独做个按钮的理由和「验证密钥」一样 —— 这个功能依赖第三方接口的返回结构，
    /// 结构一变就取不到文章；而「取不到」在签到流程里只会静默回退到配置里的旧 id。
    /// </para>
    /// </summary>
    private async void OnFetchArticleClick(object? sender, EventArgs e)
    {
        // 先把界面上的最新值落盘：用户多半是「填完 token → 直接点拉取」。
        if (!SaveConfigFromForm(showFeedback: false)) return;

        _btnFetchArticle.Enabled = false;
        SetArticlePreview("拉取中…", Theme.TextMuted);

        try
        {
            EnqueueLog(new string('=', 52));
            EnqueueLog("[INFO] 拉取广场最新文章（只读：不签到、不改配置）...");

            var options = SettingsResolver.ResolveFromConfig(_host.Config.Read());

            if (string.IsNullOrWhiteSpace(options.RefreshToken)
                || options.RefreshToken == SettingsResolver.PlaceholderToken)
            {
                SetArticlePreview("请先填 refreshToken", Theme.Danger);
                EnqueueLog("[X] 拉取失败: 还没填 refreshToken");
                return;
            }

            using var log = new ConsoleJobLogger();
            var token = await _host.Tokens
                .GetAccessTokenAsync(options.RefreshToken, options.DeviceId, log, force: false, CancellationToken.None)
                .ConfigureAwait(true);

            if (!token.Ok)
            {
                SetArticlePreview("拿不到 accessToken", Theme.Danger);
                EnqueueLog($"[X] 拉取失败: 拿不到 accessToken ({token.Source})");
                EnqueueLog("[!] 先点一次「立即签到」把 token 链路跑通, 再回来拉取");
                return;
            }

            WriteBackRotatedToken(options.RefreshToken, token);

            var (article, error) = await _host.Api
                .GetLatestArticleAsync(token.AccessToken!, CancellationToken.None)
                .ConfigureAwait(true);

            if (article is null)
            {
                SetArticlePreview("没取到文章", Theme.Danger);
                EnqueueLog($"[X] 拉取失败: {error}");
                EnqueueLog("[!] 接口若通但取不到文章, 说明返回结构变了 —— "
                    + $"需要更新 {nameof(ShareArticleFinder)} 里的判定字段");
                return;
            }

            SetArticlePreview(article.Display, Theme.Success);
            EnqueueLog($"[OK] 最新文章: {article.Display}");
            EnqueueLog($"     id={article.ArticleId}  (本次分享会用这一篇)");
        }
        catch (Exception ex)
        {
            SetArticlePreview("拉取出错", Theme.Danger);
            CrashReport.Show(ex, "拉取最新文章");
        }
        finally
        {
            _btnFetchArticle.Enabled = true;
        }
    }

    /// <summary>
    /// refresh 轮换后的新 refreshToken 写回 config.json + 界面输入框。
    ///
    /// <para>
    /// <see cref="TokenManager"/> 只把轮换结果写进 <b>缓存</b>，写回配置文件这一步一直是
    /// <see cref="SignJob"/> 负责的。「拉取一次」也走了 refresh，所以必须照做一遍。
    /// </para>
    /// </summary>
    private void WriteBackRotatedToken(string currentRefreshToken, TokenResult token)
    {
        var (rotated, ok, info) = TokenRotationWriteBack.Apply(_host.Config, currentRefreshToken, token);
        if (!rotated) return;

        EnqueueLog(ok
            ? "[OK] refreshToken 已轮换并写回 config.json"
            : $"[!] refreshToken 已轮换但写回失败: {info}");

        if (!ok) return;

        _loadingConfig = true;
        try
        {
            _txtRefreshToken.Text = token.NewRefreshToken!;
        }
        finally
        {
            _loadingConfig = false;
        }
    }

    /// <summary>更新「拉取一次」按钮旁边那行结果文本（不弹 MessageBox，免得每次都要点确定）。</summary>
    private void SetArticlePreview(string text, Color color)
    {
        if (IsDisposed) return;

        _articlePreview.Text = text;
        _articlePreview.ForeColor = color;
    }

    /// <summary>
    /// 「验证密钥」：用假 token 向签到端点发一次探测请求，判断原生通道密钥对不对。
    ///
    /// <para>纯诊断动作，<b>不会真的签到</b> —— 探测用的 token 是假的，后端拿不到用户身份。</para>
    /// </summary>
    private async void OnVerifyNativeSecretClick(object? sender, EventArgs e)
    {
        if (!SaveConfigFromForm(showFeedback: false)) return;

        _btnVerifyNative.Enabled = false;

        try
        {
            EnqueueLog(new string('=', 52));
            EnqueueLog("[INFO] 验证原生通道密钥（探测请求, 假 token, 不会真的签到）...");

            var options = SettingsResolver.ResolveFromConfig(_host.Config.Read());
            var profile = options.NativeProfile();
            var result = await NativeChannelProbe.RunAsync(_host.Api, profile).ConfigureAwait(true);

            foreach (var line in NativeChannelProbe.Format(result, profile)
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                EnqueueLog(line.TrimEnd('\r'));
            }

            EnqueueLog(result.SecretAccepted
                ? "[OK] 密钥验证通过, 可以保存并依赖自动签到"
                : "[X] 密钥验证未通过, 请按上面的建议处理");
        }
        catch (Exception ex)
        {
            CrashReport.Show(ex, "验证原生通道密钥");
        }
        finally
        {
            _btnVerifyNative.Enabled = true;
        }
    }

    private void OnAutostartChanged(object? sender, EventArgs e)
    {
        if (_loadingConfig) return;

        var want = _chkAutostart.Checked;
        var (ok, info) = want ? WinAutostart.Install() : WinAutostart.Uninstall();

        EnqueueLog(ok
            ? $"[OK] 开机自启{(want ? "已开启" : "已关闭")}: {info}"
            : $"[!] 开机自启设置失败: {info}");

        // 无论成败都以注册表的真实状态为准回填界面，避免「开了但没生效」
        var actual = WinAutostart.IsEnabled;
        _loadingConfig = true;
        try
        {
            _chkAutostart.Checked = actual;
            _trayAutostart.Checked = actual;
        }
        finally
        {
            _loadingConfig = false;
        }
    }

    /// <summary>托盘菜单里的「开机自动启动」：翻转开关状态，由 CheckedChanged 统一处理。</summary>
    private void OnTrayAutostartClick(object? sender, EventArgs e)
        => _chkAutostart.Checked = !WinAutostart.IsEnabled;

    // ==================================================================
    //  状态刷新
    // ==================================================================

    private void RefreshStatus()
    {
        if (IsDisposed) return;

        try
        {
            var cfg = _host.Config.Read();
            var state = _host.State;
            var now = TimeUtil.Now();

            // ---- 运行计划 ----
            var next = CronSchedule.Next(cfg.Cron, now);

            _valNext.Text = next?.ToString("yyyy-MM-dd HH:mm:ss") ?? "cron 表达式无效";
            _valNext.ForeColor = next is null ? Theme.Danger : Theme.TextStrong;

            _valRefresh.Text = $"每天 {SignScheduler.DailyRefreshTime:HH\\:mm} · 只读";
            _valRefresh.ForeColor = Theme.TextBody;

            if (state.LastFinish is null)
            {
                _valLast.Text = "本次会话尚未运行";
                _valLast.ForeColor = Theme.TextFaint;
            }
            else
            {
                var rc = state.LastRc;
                _valLast.Text = state.LastFinish;
                _valLast.ForeColor = rc == 0 ? Theme.Success : Theme.Danger;
            }

            if (_planCard is not null) _planCard.Note = $"cron {cfg.Cron}";

            // 页头副标题：日期 + 上次运行。设计稿里标题下面那行小字就是它 ——
            // 打开界面第一眼就能知道"今天几号、上一次跑得怎么样"，不用再去点日志。
            if (_hdrStatus is not null)
            {
                var today = TimeUtil.Now();
                var week = "日一二三四五六"[(int)today.DayOfWeek];
                var last = state.LastFinish is null
                    ? "本次会话尚未运行"
                    : state.LastRc switch
                    {
                        null => $"上次运行 {state.LastFinish}",
                        0 => $"上次运行 {state.LastFinish} 成功",
                        var rc => $"上次运行 {state.LastFinish} 失败 · {rc}",
                    };

                _hdrStatus.Subtitle = $"{today:yyyy-MM-dd} 周{week} · {last}";
            }

            // 日志卡底部那行：当前最新的一份日志。放在这里跟着刷新走，
            // 是因为它会随着每次运行、每天零点换文件而变，写死在启动时刻就过期了。
            if (_logPath is not null)
            {
                var newest = _host.Logs.NewestName();
                var label = newest is null ? "logs" : "logs/" + newest;
                if (_logPath.Text != label) _logPath.Text = label;
            }

            if (next is { } due)
            {
                var left = due - now;
                _planCountdown.Caption = left > TimeSpan.Zero
                    ? $"距下次签到 {left:hh\\:mm\\:ss}"
                    : "即将开始";
                _planCountdown.Tone = Theme.TextBody;
            }
            else
            {
                _planCountdown.Caption = "没有下一次 · 请检查 cron 表达式";
                _planCountdown.Tone = Theme.Danger;
            }

            // ---- 运行状态 ----
            if (state.Running)
            {
                _pillState.SetState($"运行中 · 步骤 {Math.Max(1, _runStep)} / {StepTitles.Length}",
                    Theme.Accent, Theme.AccentSoft);
                _pillState.DotTone = Theme.Accent;
            }
            else
            {
                _pillState.SetState("空闲 · 等待调度", Theme.TextMuted, Theme.ChipBg);
                _pillState.DotTone = Theme.Success;
            }

            _btnRunNow.Text = state.Running ? "签到中…" : "立即签到";
            _btnRunNow.Enabled = !state.Running;

            _btnRunBusy.Text = state.Running ? "签到中" : "签到完成";
            _btnRunBusy.Enabled = false;
            if (!state.Running) _btnCancel.Enabled = false;

            _sbState.Text = BuildStatusText(state, next);

            // 积分历史是文件驱动的（签到流程与每日刷新都会往里追加），
            // 靠文件指纹判断要不要重读 —— 没变就一个字节都不碰。
            RefreshPoints();
            UpdateRunningChrome();
        }
        catch
        {
            // 配置正在被写入等瞬时状态，下一轮定时刷新会补上
        }
    }

    /// <summary>右下角那行：版本 / 运行时 / 状态 / 下次时间，全部用等宽字体。</summary>
    private string BuildStatusText(JobState state, DateTime? next)
    {
        var version = typeof(MainForm).Assembly.GetName().Version;
        var versionText = version is null ? "v?" : $"v{version.Major}.{version.Minor}.{version.Build}";

        var mode = state.Running ? "运行中" : "就绪";
        var nextText = next is { } n ? $"下次 {n:HH:mm:ss}" : "cron 无效";

        return $"{versionText} · .NET {Environment.Version.Major} · {mode} · {nextText}";
    }

    /// <summary>
    /// 启动时从最近的日志里恢复「今天签没签」与账户数值，这样重启程序也能看到上次结果。
    ///
    /// <para>
    /// 从最新一份往回找，<b>找到第一份含状态行的就用</b>，而不是只看最新那一份 ——
    /// 一次失败的运行（比如 token 过期）也会落一份日志，里面只有「拿不到 accessToken」。
    /// </para>
    /// </summary>
    private void RestoreStatusFromLatestLog()
    {
        const int scanLimit = 10;

        try
        {
            foreach (var item in _host.Logs.List().Take(scanLimit))
            {
                var path = Path.Combine(_host.Logs.Dir, item.Name);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var text = reader.ReadToEnd();

                // 一次运行里「签到前 / 签到后复核」可能各打一条，后打的才反映签到后的真实状态
                var matches = StatusPattern.Matches(text);
                if (matches.Count == 0) continue;

                ApplySignStatus(matches[^1]);

                var accountMatches = AccountLine.Pattern.Matches(text);
                if (accountMatches.Count > 0 && AccountLine.Parse(accountMatches[^1].Value) is { } account)
                {
                    ApplyAccount(account);
                }

                EnqueueLog($"[INFO] 已从 {item.Name} 恢复上次签到状态");
                return;
            }
        }
        catch
        {
            // 日志读不出来不影响使用，状态卡片留空即可
        }
    }

    /// <summary>把「签到状态」写进指标卡（可跨线程调用）。</summary>
    private void ApplySignStatus(Match match)
    {
        var signed = match.Groups[1].Value == "已";
        var streak = match.Groups[2].Value;
        var cards = match.Groups[3].Value;

        void Apply()
        {
            if (IsDisposed) return;

            _cardToday.Value = signed ? "已签到" : "未签到";
            _cardToday.ForeColor = signed ? Theme.Accent : Theme.Danger;
            // 整张卡染成状态色：四张卡里它是唯一会变的那张，也是最该被一眼看到的那张
            _cardToday.Tint = signed ? Theme.SuccessSoft : Theme.DangerSoft;
            _cardToday.TintBorder = signed ? Theme.SuccessBorder : Theme.DangerBorder;
            _cardToday.Hint = signed ? "今天已完成，无需再签" : "还没签到，等下次运行";
            _cardToday.Corner = _host.State.LastFinish is { Length: >= 16 } finish ? finish[11..16] : "";

            _cardStreak.Value = streak;
            _cardCards.Value = cards;

            var count = int.TryParse(cards, out var n) ? n : 0;
            _cardCards.Badge = count >= 3 ? "充足" : count > 0 ? "紧张" : "无";
            _cardCards.BadgeTone = count >= 3 ? Theme.Warning : Theme.Danger;

            _cardsText = cards;
            if (int.TryParse(streak, out var streakDays)) _streakDays = streakDays;
            _signedToday = signed;

            if (_accountCard is not null) _accountCard.Invalidate();

            if (_pageHistory is { Visible: true }) RefreshHistory();
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

    // ==================================================================
    //  账户积分
    // ==================================================================

    /// <summary>变化列表最多读出的条数（更早的记录仍在 <c>points-history.log</c> 里）。</summary>
    private const int MaxPointsListed = 60;

    /// <summary>把账户行解析出的四个值填进「账户」卡与积分余额指标卡（可跨线程调用）。</summary>
    private void ApplyAccount(AccountSnapshot account)
    {
        void Apply()
        {
            if (IsDisposed || _accountCard is null) return;

            _account = account;

            _accountCard.Point = ShowOrDash(account.Point);
            _accountCard.Income = ShowOrDash(account.Income);
            _accountCard.Level = ShowOrDash(account.Level);
            _accountCard.Growth = ShowOrDash(account.Growth);

            var token = _host.Config.Read().RefreshToken?.Trim() ?? "";
            _accountCard.TokenTail = token.Length >= 4 ? Tail(token) : "";
            _accountCard.DeviceId = _host.Config.Read().DeviceId ?? "";

            _cardPoints.Value = ShowOrDash(account.Point);
            _cardPoints.Hint = $"累计 {ShowOrDash(account.Income)} · 成长 {ShowOrDash(account.Growth)} · {ShowOrDash(account.Level)}";

            if (_pageHistory is { Visible: true }) RefreshHistory();
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

    private long _pointsStamp = long.MinValue;

    /// <summary>
    /// 读积分历史并刷新账户卡 / 积分余额卡的走势。
    ///
    /// <para>
    /// 每 2 秒被状态定时器叫一次，所以先比文件指纹：没动就一个字节都不读、一次重绘都不做。
    /// </para>
    /// </summary>
    /// <param name="force">忽略指纹缓存强制读一次（启动时用）。</param>
    private void RefreshPoints(bool force = false)
    {
        try
        {
            var store = _host.Points;
            var stamp = store.Stamp();
            if (!force && stamp == _pointsStamp) return;
            _pointsStamp = stamp;

            var records = store.Read(MaxPointsListed);

            // 重启后第一眼就该看到上次的积分，而不是一排「—」等下次运行。
            var latest = records.Count > 0 ? records[0] : null;
            if (latest is not null && (latest.Point is not null || latest.Income is not null || latest.Growth is not null))
            {
                ApplyAccount(new AccountSnapshot
                {
                    Point = latest.Point?.ToString() ?? "-",
                    Income = latest.Income?.ToString() ?? "-",
                    Level = string.IsNullOrWhiteSpace(latest.Level) ? "-" : latest.Level,
                    Growth = latest.Growth?.ToString() ?? "-",
                    Origin = latest.Source,
                });
            }

            // 积分余额卡右下角的迷你柱：最近 12 次观察到的积分值（时间正序）
            var spark = records
                .Where(r => r.Point is not null)
                .Take(12)
                .Select(r => r.Point!.Value)
                .Reverse()
                .ToArray();

            _cardPoints.Spark = spark.Length > 1 ? spark : null;

            if (_pageHistory is { Visible: true }) RefreshHistory();
        }
        catch
        {
            // 读历史失败不该影响别的任何功能
        }
    }

    /// <summary>接口没返回时显示「—」；那个占位符「-」看起来像「填了个横杠」而不是缺数据。</summary>
    private static string ShowOrDash(string? value)
        => string.IsNullOrWhiteSpace(value) || value == "-" ? "—" : value;

    // ==================================================================
    //  自检（--selftest）
    // ==================================================================

    /// <summary>
    /// 不显示窗口的前提下跑一遍关键链路自检，用于「装完打不开」这类问题的现场诊断。
    ///
    /// <para>覆盖：界面能否构建 / Console 重定向能否进日志框 / 配置能否读写 / cron 能否解析。</para>
    /// </summary>
    internal (bool Ok, List<string> Lines) SelfTest()
    {
        var ok = true;
        var lines = new List<string>();

        // 自检不显示窗口，Load 事件不会触发，所以这里手动跑一遍数据准备
        var (created, configPath, dataDir) = _host.PrepareStorage();
        lines.Add($"数据准备: 数据目录={dataDir}");
        lines.Add($"          配置={configPath}{(created ? " (本次新建)" : "")}, 日志={_host.Logs.Dir}");

        // 强制建句柄：RichTextBox 的文本操作需要有窗口句柄，
        // 未显示的窗体访问 Handle 只会 CreateHandle，不会把窗口弹出来。
        _ = Handle;

        lines.Add($"界面构建: 导航项={_nav.Items.Count}, 页面={_pageHost.Controls.Count}, 日志框句柄={(_logBox.IsHandleCreated ? "已建" : "未建")}");
        ok &= _nav.Items.Count == 4
            && _pageHost.Controls.Contains(_pageStatus)
            && _pageHost.Controls.Contains(_pageRunning)
            && _pageHost.Controls.Contains(_pageHistory)
            && _pageHost.Controls.Contains(_pageSettings);

        Control?[] inputs =
        [
            _txtRefreshToken, _txtDeviceId, _txtCron, _txtContentId, _txtTokenB, _txtHttpProxy,
            _txtWecom, _txtDingtalk, _txtFeishu, _txtTgBotToken, _txtTgChatId,
            _txtServerChan, _txtPushPlus, _txtBark, _chkAutoShare, _chkAutostart,
            _txtCaNativeKey, _txtCaNativeSecret, _chkShowNativeSecret, _btnVerifyNative,
            _chkOpenShareUrl, _chkOpenShareAlways, _segProxy,
            _chkAutoArticleId, _btnFetchArticle, _articlePreview,
            _btnImportHar, _importEcho, _dataDirValue,
        ];
        var bound = inputs.Count(c => c is not null);
        lines.Add($"控件绑定: 配置控件 {bound}/{inputs.Length}, 托盘菜单 {_tray.ContextMenuStrip?.Items.Count ?? 0} 项");
        ok &= bound == inputs.Length && _tray.ContextMenuStrip is not null;

        // 自绘外观是否真的建起来了（缺一个就是界面会缺一块）
        MetricCard?[] metrics = [_cardToday, _cardStreak, _cardCards, _cardPoints];
        var metricCount = metrics.Count(c => c is not null);
        lines.Add($"外观: 指标卡 {metricCount}/{metrics.Length}, 状态胶囊={(_pillState is null ? "缺失" : "有")}, "
            + $"主按钮={(_btnRunNow is null ? "缺失" : "有")}, 导航={_nav.GetType().Name}, "
            + $"月历={(_calendar is null ? "缺失" : "有")}, 步骤={_steps?.StepCount ?? 0}");
        ok &= metricCount == metrics.Length && _pillState is not null && _btnRunNow is not null
              && _calendar is not null && _steps is not null && _accountCard is not null;

        // 1) Console 重定向 → 日志队列 → RichTextBox
        const string probe = "selftest: console redirect probe";
        Console.WriteLine($"[INFO] {probe}");
        FlushLogQueue();

        var hit = _logBox.Text.Contains(probe, StringComparison.Ordinal);
        lines.Add($"Console 重定向链路: {(hit ? "OK" : "失败")}（RichTextBox 当前 {_logBox.TextLength} 字符）");
        ok &= hit;

        // 2) 细滚动条：Attach 会走一遍 GetScrollInfo + ShowScrollBar（隐藏原生滚动条）。
        try
        {
            using (var bar = new SlimScrollBar(Theme.ScrollThumb, Theme.ScrollThumbHover))
            {
                bar.Attach(_logBox);
                bar.Sync();
                bar.Detach();
            }

            // 原生滚动条现在不是"藏起来"，而是"整个推到宿主可视区外面被裁掉"：
            // 文本框比承载框宽出整整一根原生条，所以文字区（客户区）必须正好等于承载框宽度。
            var host = _logBox.Parent;
            var pad = _logBox.Width - _logBox.ClientSize.Width;
            var pushedOut = pad > 0 && _logBox.Width > (host?.ClientSize.Width ?? 0);
            var textArea = host is not null && _logBox.ClientSize.Width == host.ClientSize.Width;

            var pos = pushedOut ? "已推出宿主" : "仍在宿主内";
            var fit = textArea ? "对齐" : "未对齐";
            lines.Add($"细滚动条: Attach/Sync 正常；原生条宽={pad}px（{pos}／文字区{fit}）");
            ok &= pushedOut && textArea;
        }
        catch (Exception ex)
        {
            ok = false;
            lines.Add($"[X] 细滚动条: {ex.GetType().Name}: {ex.Message}");
        }

        // 2b) 步骤行解析（「签到进行中」页靠它点亮步骤）
        var stepMatch = StepPattern.Match("[08:00:15] [INFO] [3/5] 执行签到...  POST /up/api/v1/user/sign/upgrade");
        var stepOk = stepMatch.Success && stepMatch.Groups[1].Value == "3"
                     && stepMatch.Groups[2].Value.Contains("执行签到", StringComparison.Ordinal);
        lines.Add($"步骤行解析: [3/5] → {(stepMatch.Success ? stepMatch.Groups[1].Value : "失败")} {(stepOk ? "OK" : "不符")}");
        ok &= stepOk;

        // 3) 配置读写
        try
        {
            var before = _host.Config.Read();
            var after = _host.Config.Write(before);
            lines.Add($"配置读写: cron={after.Cron}, refresh_token 长度={after.RefreshToken.Length}, push 渠道数={PushChannelCount(after)}");
            ok &= !string.IsNullOrEmpty(after.Cron);
        }
        catch (Exception ex)
        {
            lines.Add($"配置读写: 失败 {ex.GetType().Name}: {ex.Message}");
            ok = false;
        }

        // 4) cron 解析
        var cron = _host.Config.Read().Cron;
        var next = CronSchedule.NextText(cron);
        lines.Add($"cron 解析: \"{cron}\" → 下次 {next ?? "解析失败"}");
        ok &= next is not null;

        // 5) 签名算法回归 + 原生通道结构不变式
        var (signOk, signLines) = SignatureSelfCheck.Run();
        lines.AddRange(signLines);
        ok &= signOk;

        var (nativeOk, nativeLines) = SignatureSelfCheck.CheckNativeProfile();
        lines.AddRange(nativeLines);
        ok &= nativeOk;

        // 5b) 密钥判别法回归（离线）
        var (probeOk, probeLines) = NativeChannelProbe.SelfCheck();
        lines.AddRange(probeLines);
        ok &= probeOk;

        // 5c) 「签到后自动打开分享链接」的取值映射回归（离线）
        lines.Add("分享链接自动打开 (open_share_url): "
            + $"{ShareUrlOpenModes.Parse(_host.Config.Read().OpenShareUrl).Describe()}");
        var (openUrlOk, openUrlLines) = ShareUrlOpenModes.SelfCheck();
        lines.AddRange(openUrlLines);
        ok &= openUrlOk;

        // 5d) 「分享文章 ID 从哪来」的取值映射回归（离线）
        lines.Add("分享文章 ID 来源 (share_content_mode): "
            + $"{ShareContentModes.Parse(_host.Config.Read().ShareContentMode).Describe()}");
        var (contentModeOk, contentModeLines) = ShareContentModes.SelfCheck();
        lines.AddRange(contentModeLines);
        ok &= contentModeOk;

        // 5e) 广场响应 → 文章 id 的解析回归（离线）
        var (articleOk, articleLines) = ShareArticleFinder.SelfCheck();
        lines.AddRange(articleLines);
        ok &= articleOk;

        // 5f) 抓包 → 登录凭据的解析回归（离线）
        var (harOk, harLines) = HarCredentialExtractor.SelfCheck();
        lines.AddRange(harLines);
        ok &= harOk;

        // 5g) 窗口尺寸偏好的夹取回归（离线）
        var (uiOk, uiLines) = UiStateStore.SelfCheck(Path.Combine(AppPaths.WorkDir, "_selftest_uistate"));
        lines.AddRange(uiLines);
        ok &= uiOk;

        // 5h) 「签到状态」日志行的生成 ↔ 解析回归（离线）
        var (statusLineOk, statusLineLines) = SignStatusLine.SelfCheck();
        lines.AddRange(statusLineLines);
        ok &= statusLineOk;

        // 5i) 「每天 00:01 该不该刷状态」的判定回归（离线）
        var (dueOk, dueLines) = SignScheduler.SelfCheck();
        lines.AddRange(dueLines);
        ok &= dueOk;

        // 5j) 「账户行」生成 ↔ 解析回归（离线）
        var (accountOk, accountLines) = AccountLine.SelfCheck();
        lines.AddRange(accountLines);
        ok &= accountOk;

        // 5k) 积分变化历史的比对回归（离线）
        var (pointsOk, pointsLines) = PointsHistoryStore.SelfCheck(
            Path.Combine(AppPaths.WorkDir, "_selftest_points"));
        lines.AddRange(pointsLines);
        ok &= pointsOk;

        var signCfg = _host.Config.Read();
        lines.Add($"签到通道: 端点={LynkConstants.EpSign}, "
            + $"原生通道 secret={(string.IsNullOrWhiteSpace(signCfg.CaNativeSecret) ? "未配置 (签到动作会被跳过, 其余功能正常)" : "已配置")}");
        lines.Add($"状态刷新: 每天 {SignScheduler.DailyRefreshTime:HH\\:mm} 自动拉一次最新签到状态 "
            + "(只读: 不签到 / 不推送), 命令行入口 --refresh-status");
        lines.Add($"积分变化: 记录文件={_host.Points.FilePath} "
            + $"(现有 {_host.Points.Read(0).Count} 条), 每次运行后自动与上一次比对并追加");
        lines.Add($"分享文章 ID: {ShareContentModes.Parse(signCfg.ShareContentMode).Describe()}, "
            + $"兜底={signCfg.ShareContentId}, 文章流端点={LynkConstants.EpExploreSquare}");
        lines.Add($"窗口尺寸: 默认={UiStateStore.DefaultWidth}x{UiStateStore.DefaultHeight} "
            + $"最小={UiStateStore.MinWidth}x{UiStateStore.MinHeight} (逻辑像素), "
            + $"偏好文件={UiStateStore.FilePath}");

        // 6) 目录
        lines.Add($"日志目录: {_host.Logs.Dir}（存在={Directory.Exists(_host.Logs.Dir)}）");
        lines.Add($"token 缓存: {_host.TokenCache.Path}");
        ok &= Directory.Exists(_host.Logs.Dir);

        // 收尾：把 Console 还给真正的 stdout，并把残留半行刷出去
        _logWriter.FlushPending();
        Console.SetOut(_originalOut);

        return (ok, lines);
    }

    private static int PushChannelCount(AppConfig cfg)
    {
        var p = cfg.Push ?? new PushConfig();
        return new[]
        {
            p.WecomWebhook, p.DingtalkWebhook, p.FeishuWebhook,
            p.TgBotToken, p.TgChatId, p.ServerChanKey, p.PushPlusToken, p.BarkUrl,
        }.Count(v => !string.IsNullOrWhiteSpace(v));
    }
}
