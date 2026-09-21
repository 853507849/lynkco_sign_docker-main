using System.Globalization;
using System.Text.Json;

namespace LynkSign.Infrastructure;

/// <summary>
/// <see cref="JsonElement"/> 的读取辅助，用来替代 Python 里 <c>dict.get(...)</c> 的宽松取值习惯：
/// 链式取值、类型不匹配时不抛异常而是返回默认值。
/// </summary>
public static class JsonExt
{
    /// <summary>取子属性，不存在或不是对象时返回 null。</summary>
    public static JsonElement? Prop(this JsonElement? element, string name)
    {
        if (element is not { } e || e.ValueKind != JsonValueKind.Object) return null;
        return e.TryGetProperty(name, out var v) ? v : null;
    }

    /// <summary>取子属性（非 nullable 版本）。</summary>
    public static JsonElement? Prop(this JsonElement element, string name)
        => ((JsonElement?)element).Prop(name);

    // ---------- 非 nullable 转发重载 ----------
    // C# 的扩展方法解析不会自动做 JsonElement → JsonElement? 的装箱转换，
    // 所以直接持有 JsonElement 变量时（例如遍历数组元素）需要下面这组转发。

    /// <inheritdoc cref="Str(JsonElement?, string, string)"/>
    public static string Str(this JsonElement element, string name, string fallback = "")
        => ((JsonElement?)element).Str(name, fallback);

    /// <inheritdoc cref="Int(JsonElement?, string, int)"/>
    public static int Int(this JsonElement element, string name, int fallback = 0)
        => ((JsonElement?)element).Int(name, fallback);

    /// <inheritdoc cref="Bool(JsonElement?, string, bool)"/>
    public static bool Bool(this JsonElement element, string name, bool fallback = false)
        => ((JsonElement?)element).Bool(name, fallback);

    /// <inheritdoc cref="Raw(JsonElement?, string)"/>
    public static object? Raw(this JsonElement element, string name)
        => ((JsonElement?)element).Raw(name);

    /// <inheritdoc cref="TruthyProp(JsonElement?, string)"/>
    public static bool TruthyProp(this JsonElement element, string name)
        => ((JsonElement?)element).TruthyProp(name);

    /// <inheritdoc cref="AsInt(JsonElement?, int)"/>
    public static int AsInt(this JsonElement element, int fallback = 0)
        => ((JsonElement?)element).AsInt(fallback);

    /// <inheritdoc cref="AsDisplay(JsonElement?)"/>
    public static string AsDisplay(this JsonElement element)
        => ((JsonElement?)element).AsDisplay();

    /// <inheritdoc cref="DisplayProp(JsonElement?, string, string)"/>
    public static string DisplayProp(this JsonElement element, string name, string fallback = "-")
        => ((JsonElement?)element).DisplayProp(name, fallback);

    /// <inheritdoc cref="Items(JsonElement?)"/>
    public static IEnumerable<JsonElement> Items(this JsonElement element)
        => ((JsonElement?)element).Items();

    /// <inheritdoc cref="JoinStrings(JsonElement?, string)"/>
    public static string JoinStrings(this JsonElement element, string separator = ", ")
        => ((JsonElement?)element).JoinStrings(separator);

    /// <inheritdoc cref="ToRawObject(JsonElement?)"/>
    public static object? ToRawObject(this JsonElement element)
        => ((JsonElement?)element).ToRawObject();

    /// <inheritdoc cref="ToJsonString(JsonElement?, int)"/>
    public static string ToJsonString(this JsonElement element, int maxLength = 0)
        => ((JsonElement?)element).ToJsonString(maxLength);

    /// <summary>属性是否存在且为「真值」（用于 <c>if d.get("x"):</c> 这类判断）。</summary>
    public static bool TruthyProp(this JsonElement? element, string name)
    {
        var v = element.Prop(name);
        return v is { } e && IsTruthy(e);
    }

    /// <summary>把任意 JSON 标量当整数读；失败返回默认值。</summary>
    public static int AsInt(this JsonElement? element, int fallback = 0)
    {
        if (element is not { } e) return fallback;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetInt32(out var i) ? i
                : (e.TryGetDouble(out var d) ? (int)d : fallback),
            JsonValueKind.String => int.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : fallback,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => fallback,
        };
    }

    /// <summary>
    /// 面向日志的展示形式，对齐 Python 的 <c>f"{value}"</c>：
    /// null/None → "None"，布尔 → "True"/"False"，字符串与数字原样。
    /// </summary>
    public static string AsDisplay(this JsonElement? element)
    {
        if (element is not { } e) return "None";
        return e.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => "None",
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.String => e.GetString() ?? "",
            _ => e.GetRawText(),
        };
    }

    /// <summary>读属性并转成展示字符串；属性缺失时用 <paramref name="fallback"/>（默认 "-"）。</summary>
    public static string DisplayProp(this JsonElement? element, string name, string fallback = "-")
    {
        var v = element.Prop(name);
        return v is null ? fallback : v.AsDisplay();
    }

    /// <summary>取字符串，非字符串时返回默认值。</summary>
    public static string Str(this JsonElement? element, string name, string fallback = "")
    {
        var v = element.Prop(name);
        if (v is not { } e) return fallback;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? fallback,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => e.GetRawText(),
            _ => fallback,
        };
    }

    /// <summary>取整数，解析失败返回默认值。</summary>
    public static int Int(this JsonElement? element, string name, int fallback = 0)
    {
        var v = element.Prop(name);
        if (v is not { } e) return fallback;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetInt32(out var i) ? i
                : (e.TryGetDouble(out var d) ? (int)d : fallback),
            JsonValueKind.String => int.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : fallback,
            _ => fallback,
        };
    }

    /// <summary>取布尔，解析失败返回默认值（兼容 "1"/"true"/1）。</summary>
    public static bool Bool(this JsonElement? element, string name, bool fallback = false)
    {
        var v = element.Prop(name);
        if (v is not { } e) return fallback;
        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => e.TryGetDouble(out var d) && Math.Abs(d) > double.Epsilon,
            JsonValueKind.String => e.GetString()?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on",
            _ => fallback,
        };
    }

    /// <summary>把值原样取出来（string / double / bool / null），用于日志展示。</summary>
    public static object? Raw(this JsonElement? element, string name)
    {
        var v = element.Prop(name);
        if (v is not { } e) return null;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : (e.TryGetDouble(out var d) ? d : null),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => e.GetRawText(),
        };
    }

    /// <summary>枚举数组元素，非数组时返回空集合。</summary>
    public static IEnumerable<JsonElement> Items(this JsonElement? element)
    {
        if (element is not { } e || e.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in e.EnumerateArray()) yield return item;
    }

    /// <summary>把字符串数组拍平成逗号分隔文本；非数组时退化为原字符串。</summary>
    public static string JoinStrings(this JsonElement? element, string separator = ", ")
    {
        if (element is not { } e) return string.Empty;
        if (e.ValueKind == JsonValueKind.String) return e.GetString() ?? string.Empty;
        if (e.ValueKind != JsonValueKind.Array) return string.Empty;

        var parts = new List<string>();
        foreach (var item in e.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) parts.Add(item.GetString() ?? string.Empty);
            else if (item.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) parts.Add(item.GetRawText());
        }
        return string.Join(separator, parts);
    }

    /// <summary>
    /// Python 的 <c>a or b or c</c> 语义：返回第一个“真值”属性的原始值。
    /// 用于 <c>data.get("signStatus") or data.get("todaySigned") or data.get("status")</c> 这类链式兜底。
    /// </summary>
    public static JsonElement? FirstTruthy(this JsonElement? element, params string[] names)
    {
        foreach (var name in names)
        {
            var v = element.Prop(name);
            if (v is { } e && IsTruthy(e)) return e;
        }
        return null;
    }

    /// <summary>Python 真值判断（空字符串 / 0 / false / 空数组 / 空对象 / null 皆为假）。</summary>
    public static bool IsTruthy(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => !string.IsNullOrEmpty(e.GetString()),
        JsonValueKind.Number => e.TryGetDouble(out var d) && d != 0d,
        JsonValueKind.Array => e.GetArrayLength() > 0,
        JsonValueKind.Object => e.EnumerateObject().Any(),
        _ => false,
    };

    /// <summary>
    /// 把 JSON 值转成 CLR 原生对象（string / long / double / bool / null），
    /// 便于交给 <see cref="TimeUtil.ParseExpiry"/> 这类宽松解析器处理。
    /// </summary>
    public static object? ToRawObject(this JsonElement? element)
    {
        if (element is not { } e) return null;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : (e.TryGetDouble(out var d) ? d : null),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => e.GetRawText(),
        };
    }

    /// <summary>把 JSON 元素渲染成紧凑字符串（用于日志，可截断）。</summary>
    public static string ToJsonString(this JsonElement? element, int maxLength = 0)
    {
        string text;
        if (element is not { } e) text = "null";
        else text = e.ValueKind == JsonValueKind.Undefined ? "null" : e.GetRawText();

        if (maxLength > 0 && text.Length > maxLength)
        {
            text = text[..maxLength];
        }
        return text;
    }
}
