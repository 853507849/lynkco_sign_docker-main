using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LynkSign.Infrastructure;

/// <summary>
/// 窗口尺寸偏好，落在数据目录的 <c>ui-state.json</c>。
///
/// <para>
/// 只记<b>尺寸</b>与<b>是否最大化</b>，<b>刻意不记窗口位置</b>。
/// 位置一旦记住，用户拔掉外接显示器后窗口会落在已经不存在的屏幕上 ——
/// 表现为「程序明明启动了，但看不见窗口、任务栏也没有」，只能靠删配置文件救回来。
/// 尺寸没有这个问题：位置永远是屏幕居中。
/// </para>
///
/// <para>
/// 也刻意<b>不放进 config.json</b>：那份文件是签到配置（含凭据），会被 Python 版读、
/// 被打包时的凭据闸门扫。把纯 UI 偏好混进去只会让两边都变脏，
/// 而且 <c>ConfigStore</c> 反序列化失败时会整份回落默认值 —— 不值得为窗口尺寸冒这个险。
/// </para>
/// </summary>
public sealed class UiState
{
    /// <summary>窗口客户区宽度（像素）。≤0 表示没记过。</summary>
    [JsonPropertyName("window_width")] public int WindowWidth { get; set; }

    /// <summary>窗口客户区高度（像素）。≤0 表示没记过。</summary>
    [JsonPropertyName("window_height")] public int WindowHeight { get; set; }

    /// <summary>上次退出时是否处于最大化。</summary>
    [JsonPropertyName("window_maximized")] public bool WindowMaximized { get; set; }
}

/// <summary>
/// <see cref="UiState"/> 的读写与合法化。
///
/// <para>
/// 所有 IO 都<b>不抛异常</b>：窗口尺寸记不住是小事，绝不能因此让程序起不来。
/// 同理，读到的值一律经过夹取（见 <see cref="Clamp"/>）——
/// 手改这个文件写了个 99999，或者换了块小屏幕，都不能让窗口大到拖不动。
/// </para>
/// </summary>
public static class UiStateStore
{
    // ---- 尺寸边界（单一来源：MainForm 的默认 / 最小尺寸都从这里取）----

    /// <summary>首次启动的默认窗口宽度（逻辑像素，96 DPI 基准）。</summary>
    public const int DefaultWidth = 1280;

    /// <summary>首次启动的默认窗口高度（逻辑像素，96 DPI 基准）。</summary>
    public const int DefaultHeight = 900;

    /// <summary>允许的最小宽度（窗口再小配置页就挤成一条了）。</summary>
    public const int MinWidth = 1000;

    /// <summary>允许的最小高度。</summary>
    public const int MinHeight = 680;

    /// <summary>单边硬上限，纯粹防手填出天文数字（真正的上限是屏幕工作区）。</summary>
    public const int MaxSide = 8000;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 手改时写成 "1180" 也认（默认行为会直接抛 JsonException）
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = true,
    };

    /// <summary>状态文件路径（数据目录下）。</summary>
    public static string FilePath => Path.Combine(AppPaths.WorkDir, "ui-state.json");

    /// <summary>读取；文件不存在 / 损坏 / 任何异常 → 返回空状态（调用方据此用默认尺寸）。</summary>
    public static UiState Load(string? path = null)
    {
        var file = path ?? FilePath;
        try
        {
            if (!File.Exists(file)) return new UiState();

            var json = File.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(json)) return new UiState();

            return JsonSerializer.Deserialize<UiState>(json, Options) ?? new UiState();
        }
        catch
        {
            // 损坏的 UI 偏好没有修复价值，直接当没记过
            return new UiState();
        }
    }

    /// <summary>写入（临时文件 + 原子替换）。返回是否成功；失败只记日志，不影响退出。</summary>
    public static bool Save(UiState state, string? path = null)
    {
        var file = path ?? FilePath;
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
            File.Move(tmp, file, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把记忆的尺寸夹到 [最小, 屏幕工作区] 区间。
    /// 任一维 ≤0 视为「没记过」，返回 <see cref="Size.Empty"/> 交给调用方用默认值。
    /// </summary>
    public static Size Clamp(int width, int height, Size max)
    {
        if (width <= 0 || height <= 0) return Size.Empty;

        // 屏幕比最小尺寸还小时（少见），宁可略超屏也要保住最小尺寸 ——
        // 否则窗口会小到按钮都点不着，用户反而更没法自救。
        var maxW = Math.Max(MinWidth, Math.Min(MaxSide, max.Width));
        var maxH = Math.Max(MinHeight, Math.Min(MaxSide, max.Height));

        return new Size(Math.Clamp(width, MinWidth, maxW), Math.Clamp(height, MinHeight, maxH));
    }

    /// <summary>按主屏工作区夹取（取不到屏幕信息时只做硬上限夹取）。</summary>
    public static Size Clamp(int width, int height)
    {
        Size work;
        try
        {
            work = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea.Size ?? Size.Empty;
        }
        catch
        {
            work = Size.Empty;
        }

        // WorkingArea 为 0（无显示环境）时用硬上限兜底
        if (work.Width <= 0 || work.Height <= 0) work = new Size(MaxSide, MaxSide);
        return Clamp(width, height, work);
    }

    // ==================================================================
    //  DPI 归一化
    //
    //  构造期设的 ClientSize 是「逻辑像素」（随后被 AutoScaleMode.Dpi 缩放），
    //  Load 之后读到的 ClientSize 已经是「设备像素」。两者混用就会出现
    //  「窗口每启动一次就膨胀一圈」—— 存盘前除以缩放比、恢复时乘回去，单位才一致。
    // ==================================================================

    /// <summary>DPI → 缩放比（96 DPI 为 1.0）。取不到 DPI 时按 1.0 处理。</summary>
    public static double NormalizeScale(double dpi) => dpi <= 0 ? 1.0 : dpi / 96.0;

    /// <summary>逻辑像素 → 设备像素。</summary>
    public static Size ToDevice(Size logical, double scale)
        => new(
            (int)Math.Round(logical.Width * Math.Max(0.1, scale)),
            (int)Math.Round(logical.Height * Math.Max(0.1, scale)));

    /// <summary>设备像素 → 逻辑像素。</summary>
    public static Size ToLogical(Size device, double scale)
        => new(
            (int)Math.Round(device.Width / Math.Max(0.1, scale)),
            (int)Math.Round(device.Height / Math.Max(0.1, scale)));

    /// <summary>
    /// 离线自检（不碰真实数据目录：往返测试用的是调用方给的临时文件）。
    /// 接进 <c>--selftest</c>。
    /// </summary>
    public static (bool Ok, List<string> Lines) SelfCheck(string? tempFile = null)
    {
        var lines = new List<string>();
        var ok = true;

        void Check(string name, bool pass, string detail)
        {
            ok &= pass;
            lines.Add($"  [{(pass ? "OK" : "X ")}] {name} —— {detail}");
        }

        var work = new Size(1920, 1040);

        // ---- 1) 没记过（0 / 负数）→ Empty，调用方才知道该用默认值 ----
        Check("0 尺寸 → 视为没记过", Clamp(0, 0, work) == Size.Empty, "0x0 → Empty");
        Check("负尺寸 → 视为没记过", Clamp(-100, -50, work) == Size.Empty, "-100x-50 → Empty");

        // ---- 2) 太小 → 抬到最小尺寸（否则配置页会挤成一条）----
        var small = Clamp(200, 100, work);
        Check("过小尺寸 → 抬到最小", small.Width == MinWidth && small.Height == MinHeight, $"200x100 → {small.Width}x{small.Height}");

        // ---- 3) 正常值 → 原样保留（不能顺手改大改小）----
        var normal = Clamp(1400, 900, work);
        Check("正常尺寸 → 原样", normal.Width == 1400 && normal.Height == 900, $"1400x900 → {normal.Width}x{normal.Height}");

        // ---- 4) 超出工作区 → 夹到屏幕（关键：换小屏后不能大到拖不动）----
        var huge = Clamp(4000, 3000, new Size(1366, 728));
        Check("超屏尺寸 → 夹到工作区", huge.Width == 1366 && huge.Height == 728, $"4000x3000 @1366x728 → {huge.Width}x{huge.Height}");

        // ---- 5) 手填天文数字 → 硬上限兜底 ----
        var absurd = Clamp(999999, 999999, new Size(999999, 999999));
        Check("天文数字 → 硬上限", absurd.Width == MaxSide && absurd.Height == MaxSide, $"999999 → {absurd.Width}");

        // ---- 6) 屏幕比最小尺寸还小 → 保最小尺寸（能自救优先于不超屏）----
        var tiny = Clamp(1200, 800, new Size(640, 480));
        Check("屏幕过小时保最小尺寸", tiny.Width == MinWidth && tiny.Height == MinHeight, $"@640x480 → {tiny.Width}x{tiny.Height}");

        // ---- 6.5) DPI 往返：逻辑 → 设备 → 逻辑必须回到原值 ----
        //      这条错了不会报任何异常，只会让窗口尺寸每启动一次就变大一点，
        //      攒几周后才发现"窗口怎么越开越大"。
        var logicalStart = new Size(DefaultWidth, DefaultHeight);
        var roundTripOk = true;
        var roundTripDetail = "";
        foreach (var dpi in new[] { 96, 120, 144, 192 })
        {
            var scale = NormalizeScale(dpi);
            var device = ToDevice(logicalStart, scale);
            var back = ToLogical(device, scale);
            var drift = Math.Abs(back.Width - logicalStart.Width) + Math.Abs(back.Height - logicalStart.Height);
            if (drift > 2) roundTripOk = false;   // 允许四舍五入带来的 1px 级误差
            roundTripDetail += $"{dpi}dpi:{device.Width}x{device.Height}→{back.Width}x{back.Height} ";
        }
        Check("DPI 往返不漂移", roundTripOk, roundTripDetail.Trim());
        Check("96 DPI 不缩放", ToDevice(logicalStart, NormalizeScale(96)) == logicalStart, "96dpi → 原值");
        Check("DPI 取不到时按 1.0", NormalizeScale(0) == 1.0, "dpi=0 → 1.0");

        // ---- 7) 坏 JSON / 缺字段 / 字符串数字 ----
        if (tempFile is not null)
        {
            var f = tempFile + ".json";
            try
            {
                // 目标目录可能还不存在（--selftest 首次运行时数据目录是新建的）
                var dir = Path.GetDirectoryName(f);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                File.WriteAllText(f, "{ this is not json ");
                Check("坏 JSON → 空状态且不抛", Load(f).WindowWidth == 0, "解析失败已吞掉");

                File.WriteAllText(f, "{\"window_width\":\"1234\",\"window_height\":\"876\"}");
                var strNum = Load(f);
                Check("字符串型数字可读", strNum.WindowWidth == 1234 && strNum.WindowHeight == 876, $"{strNum.WindowWidth}x{strNum.WindowHeight}");

                File.WriteAllText(f, "{\"window_maximized\":true}");
                var partial = Load(f);
                Check("缺字段 → 宽度为 0", partial.WindowWidth == 0 && partial.WindowMaximized, "宽度未记过 / 最大化保留");

                // 往返：写进去的必须原样读回来
                var round = new UiState { WindowWidth = 1500, WindowHeight = 960, WindowMaximized = true };
                var saved = Save(round, f);
                var back = Load(f);
                Check("Save → Load 往返一致",
                    saved && back.WindowWidth == 1500 && back.WindowHeight == 960 && back.WindowMaximized,
                    $"{(saved ? "写盘 OK" : "写盘失败")} → {back.WindowWidth}x{back.WindowHeight}, max={back.WindowMaximized}");

                // 不存在的文件 → 空状态（首次运行路径）
                File.Delete(f);
                Check("文件不存在 → 空状态", Load(f).WindowWidth == 0, "无文件不报错");
            }
            catch (Exception ex)
            {
                Check("临时文件往返测试", false, $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                // 不留残渣（自检是在用户真实数据目录里跑的）
                try { File.Delete(f); } catch { /* 删不掉也不影响 */ }
            }
        }

        var head = $"  默认尺寸 {DefaultWidth}x{DefaultHeight}，最小 {MinWidth}x{MinHeight}";
        lines.Insert(0, ok ? $"  [OK] 窗口尺寸偏好（{head.Trim()}）" : $"  [X ] 窗口尺寸偏好 有问题");
        return (ok, lines);
    }
}
