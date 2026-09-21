using System.Text.Json;
using LynkSign.Infrastructure;

namespace LynkSign.Lynk;

/// <summary>
/// 一次「签到状态 + 连续天数 + 补签卡 + 账户信息」的快照（只读，不签到）。
///
/// <para>
/// 数值一律用可空类型：<c>null</c> 表示<b>这次没从接口拿到</b>（字段缺失 / 接口失败），
/// 与「真的是 0 天 / 0 张」是两回事 —— 调用方据此决定要不要覆盖手里的旧值，
/// 而不是拿一个假的 0 去顶替。
/// </para>
/// </summary>
public sealed class SignStatusSnapshot
{
    /// <summary>是否识别出「今天签没签」。</summary>
    public bool StatusFound { get; init; }

    /// <summary>今天是否已签到（仅 <see cref="StatusFound"/> 为 true 时有意义）。</summary>
    public bool Signed { get; init; }

    /// <summary>状态的来源（写日志用），如 <c>day/info.signStatus</c>。</summary>
    public string Origin { get; init; } = "-";

    public int? Streak { get; init; }
    public int? SignCard { get; init; }

    public bool EnergyOk { get; init; }
    public string EnergyPoint { get; init; } = "-";
    public string EnergyIncome { get; init; } = "-";

    public bool GrowthOk { get; init; }
    public string GrowthName { get; init; } = "-";
    public string GrowthValue { get; init; } = "-";

    /// <summary>两个状态接口至少有一个通了（都失败时该记一条警告）。</summary>
    public bool StatusQueryOk { get; init; }
    public string DayInfoCode { get; init; } = "-";
    public string DayInfoRaw { get; init; } = "";
    public string CardInfoCode { get; init; } = "-";

    /// <summary>
    /// 账户信息的一行摘要。<b>用词与推送正文保持一致</b>（积分余额 / 累计获得 / 成长等级 / 成长值），
    /// 避免日志里叫「Co积分」、推送里叫「积分余额」，对不上号。
    /// </summary>
    public string AccountLine()
        => $"积分余额={EnergyPoint}  累计获得={EnergyIncome}  成长等级={GrowthName}  成长值={GrowthValue}";
}

/// <summary>
/// 读一次状态 + 账户快照（4 个 GET，全走 H5 通道）。
///
/// <para>
/// 两个使用方：签到流程的「签到后复核」（首次签到会改变能量体 / 连续天数，推送正文用的就是这些数），
/// 以及每天 00:01 的定时状态刷新（见 <see cref="Services.StatusRefreshJob"/>）。
/// 抽出来共用是因为这两处要的是同一组数，各写一份的结果必然是两边慢慢跑偏。
/// </para>
///
/// <para>
/// 全程<b>不抛异常</b>：单个接口失败只是让对应字段留空/为 null，调用方看 <c>StatusQueryOk</c>
/// 与各 <c>*Ok</c> 决定说什么。签到链路不该因为一个统计接口抖动而整体失败。
/// </para>
/// </summary>
public static class SignStatusQuery
{
    public static async Task<SignStatusSnapshot> ReadAsync(LynkApiClient api, string accessToken, CancellationToken ct = default)
    {
        // ---- 状态 + 连续天数 / 补签卡 ----
        // day/info 是「今天签没签」的权威来源；旧接口继续负责连续天数 / 补签卡，并兼作状态兜底。
        var day = await api.SignDayInfoAsync(accessToken, ct).ConfigureAwait(false);
        var card = await api.SignCardInfoAsync(accessToken, ct).ConfigureAwait(false);

        var probe = day.IsOk ? SignStatusReader.Read(day.Data) : SignStatusProbe.NotFound;
        var legacy = card.IsOk ? card.Data : null;
        var legacyStatus = legacy.FirstTruthy("signStatus", "todaySigned", "status");

        var origin = probe.Found
            ? $"day/info.{probe.Field}"
            : legacyStatus is null
                ? "两个接口都未给出可判读的状态字段"
                : $"旧接口 signStatus={legacyStatus.Value.AsDisplay()}";

        JsonElement?[] sources = [day.Data, legacy];

        // ---- 账户信息 ----
        var energy = await api.EnergyAsync(accessToken, ct).ConfigureAwait(false);
        var growth = await api.GrowthAsync(accessToken, ct).ConfigureAwait(false);
        var level = growth.Data.Prop("accountLevelVo");

        return new SignStatusSnapshot
        {
            StatusFound = probe.Found || legacyStatus is not null,
            Signed = probe.Found ? probe.Signed : SignStatusReader.IsSigned(legacyStatus),
            Origin = origin,

            Streak = PickInt(sources, "continuousSignDays", "serialDays", "continueDays"),
            SignCard = PickInt(sources, "signCardNumber", "signCardCount", "cardNumber"),

            EnergyOk = energy.IsOk,
            EnergyPoint = energy.IsOk ? energy.Data.DisplayProp("point") : "-",
            EnergyIncome = energy.IsOk ? energy.Data.DisplayProp("incomePoint") : "-",

            GrowthOk = growth.IsOk,
            GrowthName = growth.IsOk ? level.DisplayProp("name") : "-",
            GrowthValue = growth.IsOk ? level.DisplayProp("growth") : "-",

            StatusQueryOk = day.IsOk || card.IsOk,
            DayInfoCode = day.CodeText,
            DayInfoRaw = day.IsOk ? day.Data.ToJsonString(400) : "",
            CardInfoCode = card.CodeText,
        };
    }

    /// <summary>
    /// 按顺序在多个数据源里找第一个存在的「真值」字段并转成 int；都没有则返回 null。
    /// 用于「新接口优先、旧接口兜底」的取值（新接口没这个字段就自动落到旧接口）。
    /// </summary>
    public static int? PickInt(IEnumerable<JsonElement?> sources, params string[] names)
    {
        foreach (var source in sources)
        {
            var value = source.FirstTruthy(names);
            if (value is not null) return value.AsInt();
        }
        return null;
    }
}
