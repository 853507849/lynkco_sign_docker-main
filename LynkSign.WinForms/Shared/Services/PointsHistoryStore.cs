using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LynkSign.Infrastructure;
using LynkSign.Lynk;

namespace LynkSign.Services;

/// <summary>
/// 一次「账户积分观察」的记录：当时的四项值 + 相对上一条的变化量。
///
/// <para>
/// <c>Delta*</c> 为 <c>null</c> 有两种含义，都<b>不是</b>「没变」：
/// 一是这是第一条（没有基线可减），二是这次接口没拿到那个字段。
/// 只有明确算出来的 0 才表示「和上次一样」——界面据此决定这条要不要显示在变化列表里。
/// </para>
/// </summary>
public sealed class PointsRecord
{
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("trigger")] public string Trigger { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";

    [JsonPropertyName("point")] public int? Point { get; set; }
    [JsonPropertyName("income")] public int? Income { get; set; }
    [JsonPropertyName("growth")] public int? Growth { get; set; }
    [JsonPropertyName("level")] public string Level { get; set; } = "";

    [JsonPropertyName("d_point")] public int? DeltaPoint { get; set; }
    [JsonPropertyName("d_income")] public int? DeltaIncome { get; set; }
    [JsonPropertyName("d_growth")] public int? DeltaGrowth { get; set; }

    /// <summary>三项里有任一项真的变了（等于 0 不算，null 也不算）。</summary>
    [JsonIgnore]
    public bool HasChange =>
        (DeltaPoint ?? 0) != 0 || (DeltaIncome ?? 0) != 0 || (DeltaGrowth ?? 0) != 0;

    /// <summary>至少算出了一项 delta（即这条有基线）。</summary>
    [JsonIgnore]
    public bool HasDelta =>
        DeltaPoint is not null || DeltaIncome is not null || DeltaGrowth is not null;

    /// <summary>时间戳 → <c>MM-dd HH:mm</c>（列表里显示用）；解析不出来就原样返回。</summary>
    [JsonIgnore]
    public string ShortTime
        => DateTime.TryParse(Time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : Time;
}

/// <summary>
/// 积分变化历史，落在数据目录的 <c>points-history.log</c>（JSON Lines，一行一条）。
///
/// <para>
/// 用 JSONL 而不是一个 JSON 数组：这个文件是<b>追加写</b>的，数组形态每追加一条都要重写全文，
/// 一旦写到一半断电就整份报废；JSONL 坏了最多坏那一行，其余照常可读（<see cref="Read"/> 就是按行容错的）。
/// </para>
///
/// <para>
/// 记录时机是「每次真的去看了账户」（签到流程末尾、每天 00:01 的状态刷新），
/// 而不是只在变化时记 —— 这样文件里同时具备两种信息：当前值（最后一条）与变化历史（delta 非 0 的条目）。
/// 界面上的「账户积分」卡读前者，「积分变化」卡读后者，一个文件喂两块。
/// </para>
///
/// <para>
/// 并发：签到（cron 线程 / 手动线程）与每日刷新可能同时来，所以读写都走同一把锁；
/// 跨进程不考虑 —— 这个程序有单实例锁（见 <c>SingleInstance</c>）。
/// </para>
/// </summary>
public sealed class PointsHistoryStore
{
    /// <summary>保留的最大条数（超出后从头丢弃）。每天 1~3 条，500 条约等于一年。</summary>
    public const int MaxRecords = 500;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = false,
        // 中文（成长等级名）不转义，文件直接用记事本打开也能读
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();
    private readonly string _file;

    public PointsHistoryStore(string? filePath = null)
        => _file = filePath ?? Path.Combine(AppPaths.WorkDir, "points-history.log");

    /// <summary>历史文件路径（界面「打开数据目录」里能看到它）。</summary>
    public string FilePath => _file;

    /// <summary>
    /// 文件指纹（最后写入时间 + 长度）。界面每 2 秒刷一次状态，靠它判断「文件没动就别重读」；
    /// 文件不存在返回 -1。
    /// </summary>
    public long Stamp()
    {
        try
        {
            var info = new FileInfo(_file);
            return info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : -1L;
        }
        catch
        {
            return -1L;
        }
    }

    /// <summary>最后一条记录（即「当前值」）；没有历史返回 null。</summary>
    public PointsRecord? Last()
    {
        lock (_lock) return LastLocked();
    }

    /// <summary>读最近的记录，<b>最新的在前</b>。<paramref name="max"/> ≤ 0 表示不限。</summary>
    public List<PointsRecord> Read(int max = 0)
    {
        lock (_lock) return ReadLocked(max);
    }

    /// <summary>
    /// 观察一次账户信息：与上一条比对、算出 delta、追加一条记录并返回它。
    /// 调用方负责把返回结果打进日志（见 <see cref="DescribeChange"/>）。
    /// </summary>
    public PointsRecord Observe(string trigger, AccountSnapshot account)
    {
        lock (_lock)
        {
            var last = LastLocked();

            var record = new PointsRecord
            {
                Time = TimeUtil.Stamp(),
                Trigger = trigger,
                Source = account.Origin.Length > 0 ? account.Origin : "-",
                Point = AccountLine.ParseNumber(account.Point),
                Income = AccountLine.ParseNumber(account.Income),
                Growth = AccountLine.ParseNumber(account.Growth),
                Level = Normalize(account.Level),
            };

            record.DeltaPoint = Diff(last?.Point, record.Point);
            record.DeltaIncome = Diff(last?.Income, record.Income);
            record.DeltaGrowth = Diff(last?.Growth, record.Growth);

            AppendLocked(record);
            return record;
        }
    }

    /// <summary>
    /// 一行中文摘要，给日志框用（与界面卡片同源，避免「日志里说 +5、卡片上没变化」）。
    /// </summary>
    public static string DescribeChange(PointsRecord record)
    {
        if (!record.HasDelta)
        {
            return $"积分变化: 首次记录基线 (积分余额={Show(record.Point)} 成长值={Show(record.Growth)})";
        }

        if (!record.HasChange)
        {
            return $"积分变化: 与上次一致 (积分余额={Show(record.Point)} 成长值={Show(record.Growth)})";
        }

        var parts = new List<string>();
        AddPart(parts, "积分余额", record.DeltaPoint, record.Point);
        AddPart(parts, "累计获得", record.DeltaIncome, record.Income);
        AddPart(parts, "成长值", record.DeltaGrowth, record.Growth);

        return $"积分变化: {string.Join("  ", parts)}  [{record.Trigger}]";
    }

    // ==================================================================
    //  内部实现
    // ==================================================================

    /// <summary>
    /// 本次值 - 上次值。
    /// 本次没拿到（null）→ null（无法比较，不等于「没变」）；上次没拿到 → null（没有基线）。
    /// </summary>
    private static int? Diff(int? previous, int? current)
        => previous is null || current is null ? null : current - previous;

    private static void AddPart(List<string> parts, string name, int? delta, int? current)
    {
        if (delta is not { } d || d == 0) return;
        parts.Add($"{name} {current - d}→{current} ({d:+#;-#;0})");
    }

    private static string Show(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Normalize(string? level)
        => string.IsNullOrWhiteSpace(level) || level == "-" ? "" : level.Trim();

    private PointsRecord? LastLocked()
    {
        var all = ReadLocked(0);
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>按文件顺序读，然后反转成「最新在前」。单行解析失败只跳过那一行。</summary>
    private List<PointsRecord> ReadLocked(int max)
    {
        var records = new List<PointsRecord>();

        try
        {
            if (!File.Exists(_file)) return records;

            // 共享读：签到线程正在追加时界面也能读
            using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    if (JsonSerializer.Deserialize<PointsRecord>(line, Options) is { } record)
                    {
                        records.Add(record);
                    }
                }
                catch
                {
                    // 半行 / 手改坏了 —— 跳过这行，不能让整份历史读不出来
                }
            }
        }
        catch
        {
            return records;
        }

        records.Reverse();   // 最新的在前
        if (max > 0 && records.Count > max) records.RemoveRange(max, records.Count - max);
        return records;
    }

    private void AppendLocked(PointsRecord record)
    {
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.AppendAllText(_file, JsonSerializer.Serialize(record, Options) + "\n", Encoding.UTF8);
            PruneLocked();
        }
        catch
        {
            // 写不进去只影响「积分变化」这块展示，绝不能让签到流程因此失败
        }
    }

    /// <summary>超过 <see cref="MaxRecords"/> 条时，只保留最新的那些（重写整份文件）。</summary>
    private void PruneLocked()
    {
        try
        {
            var records = ReadLocked(0);
            if (records.Count <= MaxRecords) return;

            var keep = records.Take(MaxRecords).Reverse().ToList();   // 还原成时间正序
            var text = string.Join("\n", keep.Select(r => JsonSerializer.Serialize(r, Options))) + "\n";

            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, text, Encoding.UTF8);
            File.Move(tmp, _file, overwrite: true);
        }
        catch
        {
            // 裁剪失败就让它继续长，不影响功能
        }
    }

    // ==================================================================
    //  自检
    // ==================================================================

    /// <summary>
    /// 离线回归（接进 <c>--selftest</c>）：首次基线 / 无变化 / 增加 / 减少 / 缺值这五条路径
    /// 必须各自得出正确的 delta。
    ///
    /// <para>
    /// 这段逻辑写错不会抛异常，只会让「积分变化」列表<b>静默地少几条或多几条</b> ——
    /// 而它恰恰是用户用来判断「签到到底有没有加分」的唯一依据，所以必须在离线钉住。
    /// 往返测试用调用方给的临时文件，不碰真实数据目录。
    /// </para>
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

        if (tempFile is null)
        {
            lines.Insert(0, "  [X ] 积分变化历史 未提供临时文件，跳过");
            return (false, lines);
        }

        var file = tempFile + ".log";

        try
        {
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            try { File.Delete(file); } catch { /* 不存在也无所谓 */ }

            var store = new PointsHistoryStore(file);

            static AccountSnapshot Snap(string p, string i, string l, string g, string origin = "selftest")
                => new() { Point = p, Income = i, Level = l, Growth = g, Origin = origin };

            // 1) 首次 → 有值但无 delta（没有基线可减）
            var first = store.Observe("manual", Snap("118", "306", "LV.3", "455"));
            Check("首次记录 → 无基线",
                first.Point == 118 && first.Growth == 455 && !first.HasDelta && !first.HasChange,
                $"point={Show(first.Point)} d={Show(first.DeltaPoint)} (期望 118 / null)");

            // 2) 一模一样 → delta 全 0，即「没有变化」
            var same = store.Observe("cron", Snap("118", "306", "LV.3", "455"));
            Check("数值未变 → delta=0 且不算变化",
                same.DeltaPoint == 0 && same.DeltaGrowth == 0 && !same.HasChange,
                $"d_point={Show(same.DeltaPoint)} d_growth={Show(same.DeltaGrowth)} (期望 0)");

            // 3) 签到加了分 → 正 delta
            var up = store.Observe("manual", Snap("123", "306", "LV.3", "460", "签到后复核"));
            Check("签到加分 → 正 delta",
                up.DeltaPoint == 5 && up.DeltaGrowth == 5 && up.HasChange,
                $"积分 +{Show(up.DeltaPoint)} / 成长 +{Show(up.DeltaGrowth)} (期望 +5)");

            // 4) 积分被消耗 → 负 delta 也要认（不能只处理增加）
            var down = store.Observe("manual", Snap("100", "306", "LV.3", "460"));
            Check("积分减少 → 负 delta",
                down.DeltaPoint == -23 && down.HasChange,
                $"积分 {Show(down.DeltaPoint)} (期望 -23)");

            // 5) 接口抖动（没拿到）→ null，绝不记成「掉到 0」
            var missing = store.Observe("status", Snap("-", "-", "-", "-"));
            Check("接口没返回 → null 而非 0",
                missing.Point is null && missing.DeltaPoint is null && !missing.HasChange,
                $"point={Show(missing.Point)} d={Show(missing.DeltaPoint)} (期望 null/null)");

            // 6) 读回：最新在前、条数正确
            // 写入顺序是 1首次 → 2无变化 → 3加分 → 4减分 → 5缺值，
            // 所以倒序读出来应当是：缺值 / 减分 / 加分
            var recent = store.Read(3);
            Check("读取倒序 + 条数",
                recent.Count == 3 && recent[0].Point is null
                && recent[1].DeltaPoint == -23 && recent[2].DeltaPoint == 5,
                $"取 3 条 → 第一条 point={(recent.Count > 0 ? Show(recent[0].Point) : "?")}，"
                + $"第二条 d_point={(recent.Count > 1 ? Show(recent[1].DeltaPoint) : "?")}，"
                + $"第三条 d_point={(recent.Count > 2 ? Show(recent[2].DeltaPoint) : "?")}");

            // 7) 摘要文案：有变化 / 无变化 / 首次三种形态都要能写出来（顺带防 null 引用）
            var d1 = DescribeChange(up);
            var d2 = DescribeChange(same);
            var d3 = DescribeChange(first);
            Check("摘要文案三态齐全",
                d1.Contains("积分余额", StringComparison.Ordinal)
                && d2.Contains("与上次一致", StringComparison.Ordinal)
                && d3.Contains("首次记录基线", StringComparison.Ordinal),
                $"{d1} ｜ {d2} ｜ {d3}");
        }
        catch (Exception ex)
        {
            Check("积分变化历史往返测试", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { File.Delete(file); } catch { /* 删不掉也不影响 */ }
        }

        return (ok, lines);
    }
}
