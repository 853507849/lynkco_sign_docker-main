using System.Text.Json;

namespace LynkSign.Lynk;

/// <summary>「今天是否已签到」的解析结果。</summary>
/// <param name="Found">是否成功识别出状态字段（false 表示响应里没有可判读的字段）。</param>
/// <param name="Signed">是否已签到（仅 <see cref="Found"/> 为 true 时有意义）。</param>
/// <param name="Field">命中的字段路径，仅用于日志展示与事后排错。</param>
public readonly record struct SignStatusProbe(bool Found, bool Signed, string Field)
{
    /// <summary>未识别到：交由调用方回退到旧接口判定。</summary>
    public static readonly SignStatusProbe NotFound = new(false, false, "");
}

/// <summary>
/// 从「当日签到状态」接口的 data 里解析出「今天签没签」。
///
/// 为什么要写得这么啰嗦：<c>/up/api/v1/user/sign/day/info</c> 是后端的签到状态接口，
/// 不同 APP 版本返回的字段名并不统一——既可能叫 <c>signStatus</c>，也可能叫
/// <c>signed</c> / <c>todaySigned</c>，还可能嵌在 <c>data.signInfo.xxx</c> 这一层里。
/// 为了不在字段改名时又炸一次，这里按三级策略取值：
/// <list type="number">
///   <item>在 <c>data</c> 顶层按候选名（按命中概率排序）逐个匹配；</item>
///   <item>在 <c>data</c> 的下一层（嵌套对象 / 数组元素）里再匹配一遍；</item>
///   <item>仍未命中时，做一次「字段名含 sign 且值可判读」的模糊扫描。</item>
/// </list>
/// 任一级命中即返回；三级都没命中则返回 <see cref="SignStatusProbe.NotFound"/>，
/// 由调用方回退到旧接口，并把原始 JSON 打进日志，便于事后补充候选字段名。
/// </summary>
public static class SignStatusReader
{
    /// <summary>
    /// 候选字段名，按「命中概率从高到低」排列。
    /// <c>status</c> 故意放最后：它太泛，容易被别的语义占用。
    /// </summary>
    private static readonly string[] CandidateNames =
    [
        "signStatus", "signed", "signedToday", "todaySigned", "todaySign",
        "isSign", "isSigned", "isSignIn", "isSignToday", "todayIsSign",
        "signInStatus", "signDayStatus", "daySignStatus",
        "hasSign", "hasSigned", "signFlag", "sign", "status",
    ];

    /// <summary>模糊扫描只认字段名含这个关键字的属性，避免误判无关字段。</summary>
    private const string FuzzyKeyword = "sign";

    /// <summary>解析 data 节点，得到「今天是否已签到」。</summary>
    public static SignStatusProbe Read(JsonElement? data)
    {
        if (data is not { } root) return SignStatusProbe.NotFound;

        // 兜底：少数版本会把结果直接挂在 data 上（data: true / 1 / "signed"）
        if (root.ValueKind != JsonValueKind.Object)
        {
            return TryConvert(root, out var inline)
                ? new SignStatusProbe(true, inline, "data")
                : SignStatusProbe.NotFound;
        }

        // ---- 第 1 级: data 顶层精确匹配 ----
        foreach (var name in CandidateNames)
        {
            if (TryRead(root, name, name, out var probe)) return probe;
        }

        // ---- 第 2 级: data 的下一层（嵌套对象 / 数组元素）精确匹配 ----
        foreach (var top in root.EnumerateObject())
        {
            if (top.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in CandidateNames)
                {
                    if (TryRead(top.Value, name, $"{top.Name}.{name}", out var probe)) return probe;
                }
            }
            else if (top.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in top.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    foreach (var name in CandidateNames)
                    {
                        if (TryRead(item, name, $"{top.Name}[].{name}", out var probe)) return probe;
                    }
                }
            }
        }

        // ---- 第 3 级: 模糊扫描（顶层 + 下一层，字段名含 sign）----
        foreach (var (owner, prefix) in Scopes(root))
        {
            foreach (var prop in owner.EnumerateObject())
            {
                if (!prop.Name.Contains(FuzzyKeyword, StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryConvert(prop.Value, out var signed)) continue;

                return new SignStatusProbe(true, signed, $"{prefix}{prop.Name}");
            }
        }

        return SignStatusProbe.NotFound;
    }

    /// <summary>需要参与模糊扫描的层级：data 自身，以及它的每个对象型子属性。</summary>
    private static IEnumerable<(JsonElement Owner, string Prefix)> Scopes(JsonElement root)
    {
        yield return (root, "");

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                yield return (prop.Value, $"{prop.Name}.");
            }
        }
    }

    /// <summary>取属性并尝试判读；属性不存在或值不可判读时返回 false。</summary>
    private static bool TryRead(JsonElement owner, string name, string path, out SignStatusProbe probe)
    {
        probe = SignStatusProbe.NotFound;

        var value = Find(owner, name);
        if (value is not { } v) return false;
        if (!TryConvert(v, out var signed)) return false;

        probe = new SignStatusProbe(true, signed, path);
        return true;
    }

    /// <summary>先按原名精确取，取不到再做一次大小写不敏感的线性查找。</summary>
    private static JsonElement? Find(JsonElement owner, string name)
    {
        if (owner.TryGetProperty(name, out var exact)) return exact;

        foreach (var prop in owner.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) return prop.Value;
        }
        return null;
    }

    /// <summary>
    /// 把布尔 / 数字 / 字符串统一判读成「已签到 / 未签到」。
    /// 无法判读时返回 false（例如 null、空字符串、语义不明的枚举数字 2/3），
    /// 由上层继续去找别的候选字段，避免猜错。
    /// </summary>
    public static bool TryConvert(JsonElement value, out bool signed)
    {
        signed = false;

        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                signed = true;
                return true;

            case JsonValueKind.False:
                return true;

            case JsonValueKind.Number:
                if (!value.TryGetDouble(out var number)) return false;
                if (number == 1d) { signed = true; return true; }
                if (number == 0d) return true;
                return false;   // 2 / 3 这类枚举值语义不明，不猜

            case JsonValueKind.String:
                switch (value.GetString()?.Trim().ToLowerInvariant())
                {
                    case "1" or "true" or "yes" or "y" or "signed" or "already" or "done":
                        signed = true;
                        return true;

                    case "0" or "false" or "no" or "n" or "unsigned" or "none" or "notsigned" or "todo":
                        return true;

                    default:
                        return false;
                }

            default:
                return false;
        }
    }

    /// <summary>
    /// 判断一个状态值是否表示「今日已签」，等价 Python 的 <c>in (1, True, "1", "signed", "already")</c>。
    ///
    /// <para>
    /// 与 <see cref="TryConvert"/> 的分工：那个要回答「这个值能不能判读成签/未签」（写错就换下一个候选字段），
    /// 这个只回答「算不算已签」，且<b>只认肯定写法</b>—— 语义不明的值一律当未签。
    /// 它服务的是「旧接口兜底」那条路：宁可当天没签到被重试一次，也不能把没签的判成已签。
    /// </para>
    /// </summary>
    public static bool IsSigned(JsonElement? value)
    {
        if (value is not { } e) return false;

        return e.ValueKind switch
        {
            // Python 里 1 == True, 所以数值上等于 1 都算已签
            JsonValueKind.Number => e.TryGetDouble(out var d) && d == 1d,
            JsonValueKind.True => true,
            JsonValueKind.String => e.GetString() is "1" or "signed" or "already",
            _ => false,
        };
    }
}
