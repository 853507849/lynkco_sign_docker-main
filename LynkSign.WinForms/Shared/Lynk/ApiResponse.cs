using System.Text.Json;
using LynkSign.Infrastructure;

namespace LynkSign.Lynk;

/// <summary>
/// 统一封装领克接口响应，替代 Python 里到处 <c>dict.get()</c> 的写法。
///
/// 对应 Python 版三种响应形态：
/// <list type="bullet">
///   <item>正常 JSON：<c>{"code":"success"|200,"message":...,"data":...}</c></item>
///   <item>非 JSON 兜底：<c>{"code": status_code, "raw": text[:500]}</c></item>
///   <item>网络异常：<c>{"code":"EXCEPTION","message": str(e)}</c></item>
/// </list>
/// </summary>
public sealed class ApiResponse
{
    private readonly JsonElement _root;
    private readonly JsonElement? _data;

    private ApiResponse(string codeText, string message, string raw, JsonElement? root, JsonElement? data)
    {
        CodeText = codeText;
        Message = message;
        Raw = raw;
        _root = root ?? default(JsonElement);
        _data = data;
    }

    /// <summary><c>str(code)</c> 的结果："success" / "200" / "EXCEPTION" 等。</summary>
    public string CodeText { get; }

    /// <summary>响应 message 字段。</summary>
    public string Message { get; }

    /// <summary>
    /// 实际拿到的 HTTP 状态码；<c>0</c> 表示压根没拿到响应（网络层异常）。
    ///
    /// <para>
    /// 为什么非要单独存它：阿里云网关的拒绝（<c>400 Invalid Signature</c> /
    /// <c>401 Invalid Key</c> / <c>403 Unauthorized Consumer</c>）正文都不是 JSON，
    /// 会走兜底分支、把状态码写进 <see cref="CodeText"/>，此时还看得到；
    /// 但响应一旦是 JSON，<see cref="CodeText"/> 就变成业务 code，
    /// HTTP 状态码<b>无处可查</b> —— 而「400 / 401 / 403」恰恰是判断
    /// 「签名算错 / key 不认识 / 未授权」的唯一依据。留一个字段专门装它。
    /// </para>
    /// </summary>
    public int HttpStatus { get; private init; }

    /// <summary>非 JSON 响应的原始文本（截断 500 字符）。</summary>
    public string Raw { get; }

    /// <summary>data 节点；不存在时为 null。</summary>
    public JsonElement? Data => _data;

    /// <summary>整个响应体。</summary>
    public JsonElement? Root => _root.ValueKind == JsonValueKind.Undefined ? null : _root;

    /// <summary>等价于 Python 的 <c>str(code) in ("200", "success")</c>。</summary>
    public bool IsOk => CodeText is "200" or "success";

    /// <summary>从 JSON 响应构造。</summary>
    public static ApiResponse FromJson(JsonElement root)
    {
        string codeText;
        var codeEl = ((JsonElement?)root).Prop("code");

        if (codeEl is { } c)
        {
            codeText = c.ValueKind switch
            {
                JsonValueKind.String => c.GetString() ?? "",
                JsonValueKind.Number => c.GetRawText(),
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                JsonValueKind.Null => "",
                _ => c.GetRawText(),
            };
        }
        else
        {
            codeText = "";
        }

        var message = ((JsonElement?)root).Str("message");
        return new ApiResponse(codeText, message, "", root, ((JsonElement?)root).Prop("data"));
    }

    /// <summary>构造非 JSON / 异常兜底响应。</summary>
    public static ApiResponse FromFallback(string codeText, string raw = "", string message = "")
        => new(codeText, message, raw, null, null);

    /// <summary>
    /// 回填 HTTP 状态码（<see cref="LynkApiClient"/> 解析响应时调用）。
    ///
    /// <para>
    /// 用「返回新实例」而不是可变属性：<see cref="ApiResponse"/> 目前是全只读的，
    /// 为了一个诊断字段引入可变状态，会让人分不清「谁改过它」。
    /// </para>
    /// </summary>
    public ApiResponse WithHttpStatus(int status) => new(CodeText, Message, Raw, Root, _data)
    {
        HttpStatus = status,
    };

    /// <summary>
    /// 渲染成紧凑 JSON（日志用，可截断）。
    ///
    /// 兜底响应（网关报错、非 JSON 正文）没有 JSON 可渲染，此时回退到原始正文 / 异常信息。
    /// 否则日志只会打出一个字面量 "null"，把排查网关问题唯一的线索丢掉
    /// ——例如网关返回的 <c>400 Invalid Signature</c> 正文就藏在这里。
    /// </summary>
    public string ToJsonString(int maxLength = 0)
    {
        if (Root is { } root) return root.ToJsonString(maxLength);

        var text = !string.IsNullOrEmpty(Raw) ? Raw
            : !string.IsNullOrEmpty(Message) ? Message
            : "";
        if (text.Length == 0) return "(空响应体)";

        return maxLength > 0 && text.Length > maxLength ? text[..maxLength] : text;
    }

    public override string ToString() => $"code={CodeText} message={Message}";
}
