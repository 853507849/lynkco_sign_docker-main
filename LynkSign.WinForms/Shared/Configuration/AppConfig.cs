using System.Text.Json;
using System.Text.Json.Serialization;
using LynkSign.Infrastructure;

namespace LynkSign.Configuration;

/// <summary>推送渠道配置，对应 config.json 的 <c>push</c> 节点。</summary>
public sealed class PushConfig
{
    [JsonPropertyName("wecom_webhook")] public string WecomWebhook { get; set; } = "";
    [JsonPropertyName("dingtalk_webhook")] public string DingtalkWebhook { get; set; } = "";
    [JsonPropertyName("feishu_webhook")] public string FeishuWebhook { get; set; } = "";
    [JsonPropertyName("tg_bot_token")] public string TgBotToken { get; set; } = "";
    [JsonPropertyName("tg_chat_id")] public string TgChatId { get; set; } = "";
    [JsonPropertyName("serverchan_key")] public string ServerChanKey { get; set; } = "";
    [JsonPropertyName("pushplus_token")] public string PushPlusToken { get; set; } = "";
    [JsonPropertyName("bark_url")] public string BarkUrl { get; set; } = "";
}

/// <summary>
/// 完整配置模型，字段名与 Python 版的 config.json <b>完全一致</b>，保证配置页与既有文件双向兼容。
/// </summary>
public sealed class AppConfig
{
    public const string DefaultCron = "0 9 * * *";
    public const string DefaultShareContentId = "2072260486405246976";

    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("cron")] public string Cron { get; set; } = DefaultCron;
    [JsonPropertyName("auto_share")] public bool AutoShare { get; set; }
    [JsonPropertyName("token_b")] public List<string> TokenB { get; set; } = [];
    [JsonPropertyName("share_content_id")] public string ShareContentId { get; set; } = DefaultShareContentId;

    /// <summary>
    /// 分享用的文章 id 从哪来：<c>auto</c>（默认，每次运行拉广场最新一篇）/
    /// <c>fixed</c>（固定用 <see cref="ShareContentId"/>）。语义与宽容解析见
    /// <see cref="ShareContentModes"/>。
    ///
    /// <para>
    /// 默认 <c>auto</c>：硬编码的文章 id 被下架后，分享会「看起来成功但实际不加分」，
    /// 这种失效没有任何报错，只能靠事后对能量体才发现。
    /// 拉不到时仍会回退到 <see cref="ShareContentId"/>，所以不会比以前更容易失败。
    /// </para>
    /// </summary>
    [JsonPropertyName(ShareContentModes.ConfigField)]
    [JsonConverter(typeof(LenientStringConverter))]
    public string ShareContentMode { get; set; } = "";

    /// <summary>
    /// 拿到分享链接后是否自动用系统默认浏览器打开。
    ///
    /// <para>
    /// 取值 <c>off</c>（默认，不打开）/ <c>manual</c>（只在手动「立即签到」后打开）/
    /// <c>always</c>（手动与 cron 定时都打开）。语义与宽容解析见
    /// <see cref="ShareUrlOpenModes"/>。
    /// </para>
    ///
    /// <para>
    /// 之所以默认是 <c>off</c> 而不是 <c>manual</c>：升级上来的老配置里没有这个字段，
    /// 默认打开等于「升级之后每次点立即签到都弹浏览器」，属于用户没要求的副作用。
    /// </para>
    /// </summary>
    [JsonPropertyName(ShareUrlOpenModes.ConfigField)]
    [JsonConverter(typeof(LenientStringConverter))]
    public string OpenShareUrl { get; set; } = "";

    /// <summary>
    /// 出站代理。取值语义见 <c>HttpClientSetup</c>：
    /// <c>""</c>(默认) = 直连且<b>忽略系统代理</b>；
    /// <c>direct</c> = 强制直连；<c>system</c> = 沿用 Windows 系统代理；
    /// <c>http://host:port</c> = 走该代理。
    /// </summary>
    [JsonPropertyName("http_proxy")] public string HttpProxy { get; set; } = "";

    [JsonPropertyName("push")] public PushConfig Push { get; set; } = new();

    // ==================== 原生通道（签到写入专用） ====================

    /// <summary>
    /// 原生 iOS SDK 通道的 app key。
    /// 留空即使用抓包实测的默认值（<c>LynkConstants.NativeCaKey</c>）。
    /// </summary>
    [JsonPropertyName(LynkSign.Lynk.LynkConstants.NativeKeyConfigField)] public string CaNativeKey { get; set; } = "";

    /// <summary>
    /// 原生 iOS SDK 通道的 app secret，<b>签到写入接口的唯一前置条件</b>。
    ///
    /// <para>
    /// 它<b>不会出现在任何请求或响应里</b>，抓包拿不到（抓包只能看到 X-Ca-Key）；
    /// 也无法从 X-Ca-Signature 反推（HMAC 不可逆）。留空时签到会被跳过并给出明确提示，
    /// 其余功能（签到状态查询、连续天数、能量体、任务、分享）完全不受影响 —— 它们都走 H5 通道。
    /// </para>
    /// </summary>
    [JsonPropertyName(LynkSign.Lynk.LynkConstants.NativeSecretConfigField)] public string CaNativeSecret { get; set; } = "";

    /// <summary>config.json 不存在或损坏时的默认结构（对应 Python 的 <c>DEFAULT_CONFIG</c>）。</summary>
    public static AppConfig CreateDefault() => new();

    /// <summary>去掉首尾空白只保留已知字段（对应 Python 的 <c>write_config</c>）。</summary>
    public AppConfig Sanitized() => new()
    {
        RefreshToken = (RefreshToken ?? "").Trim(),
        DeviceId = (DeviceId ?? "").Trim(),
        Cron = string.IsNullOrWhiteSpace(Cron) ? DefaultCron : Cron.Trim(),
        AutoShare = AutoShare,
        TokenB = [.. (TokenB ?? []).Select(t => (t ?? "").Trim()).Where(t => t.Length > 0)],
        ShareContentId = (ShareContentId ?? "").Trim(),
        ShareContentMode = ShareContentModes.Parse(ShareContentMode).ToConfigValue(),
        OpenShareUrl = ShareUrlOpenModes.Parse(OpenShareUrl).ToConfigValue(),
        HttpProxy = (HttpProxy ?? "").Trim(),
        CaNativeKey = (CaNativeKey ?? "").Trim(),
        CaNativeSecret = (CaNativeSecret ?? "").Trim(),
        Push = new PushConfig
        {
            WecomWebhook = (Push?.WecomWebhook ?? "").Trim(),
            DingtalkWebhook = (Push?.DingtalkWebhook ?? "").Trim(),
            FeishuWebhook = (Push?.FeishuWebhook ?? "").Trim(),
            TgBotToken = (Push?.TgBotToken ?? "").Trim(),
            TgChatId = (Push?.TgChatId ?? "").Trim(),
            ServerChanKey = (Push?.ServerChanKey ?? "").Trim(),
            PushPlusToken = (Push?.PushPlusToken ?? "").Trim(),
            BarkUrl = (Push?.BarkUrl ?? "").Trim(),
        },
    };
}

/// <summary>
/// 宽容解析 <c>token_b</c>：既接受 JSON 数组，也接受逗号 / 换行分隔的字符串
/// （Python 版在 ql_lynk.py 里对两种形态都做了兼容）。
/// </summary>
public sealed class StringListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return [];

            case JsonTokenType.String:
            {
                var raw = reader.GetString() ?? "";
                return [.. raw.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            }

            case JsonTokenType.StartArray:
            {
                var list = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.String:
                            list.Add(reader.GetString() ?? "");
                            break;
                        case JsonTokenType.Number:
                            list.Add(reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture));
                            break;
                        default:
                            using (var skip = JsonDocument.ParseValue(ref reader)) { }
                            break;
                    }
                }
                return list;
            }

            default:
                using (JsonDocument.ParseValue(ref reader)) { }
                return [];
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value ?? []) writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
}

/// <summary>宽容解析布尔：接受 true/false、0/1、"true"/"false"/"on"。默认 <c>false</c>。</summary>
public sealed class LenientBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        JsonTokenType.Null => false,
        JsonTokenType.Number => reader.TryGetDouble(out var d) && Math.Abs(d) > double.Epsilon,
        JsonTokenType.String => reader.GetString()?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on",
        _ => false,
    };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}

/// <summary>
/// 宽容解析字符串型字段：<c>"manual"</c>、<c>true</c>、<c>1</c>、<c>null</c> 都能读成字符串。
///
/// <para>
/// 只挂在明确需要宽容的单个属性上（<c>[JsonConverter]</c>），<b>不做全局注册</b> ——
/// 全局注册会把所有 <c>string</c> 字段的解析行为一起改掉，而它们本来只需要老老实实读字符串。
/// </para>
///
/// <para>
/// 这里的关键收益是<b>不让整份配置崩掉</b>：<see cref="ConfigStore"/> 在反序列化抛异常时
/// 会整份回落到默认值，也就是说一个写成人话的 <c>"open_share_url": true</c>
/// 会顺手把 refreshToken 一起清空 —— 代价远大于收益。
/// </para>
/// </summary>
public sealed class LenientStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Null => "",
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        JsonTokenType.Number => reader.TryGetInt64(out var l)
            ? l.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonTokenType.String => reader.GetString() ?? "",
        _ => "",
    };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value ?? "");
}
