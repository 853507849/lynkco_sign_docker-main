using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using LynkSign.Configuration;

namespace LynkSign.Push;

/// <summary>解析后的推送渠道参数（环境变量优先于 config.json）。</summary>
public sealed class PushChannels
{
    public string WecomWebhook { get; init; } = "";
    public string DingtalkWebhook { get; init; } = "";
    public string FeishuWebhook { get; init; } = "";
    public string TgBotToken { get; init; } = "";
    public string TgChatId { get; init; } = "";
    public string ServerChanKey { get; init; } = "";
    public string PushPlusToken { get; init; } = "";
    public string BarkUrl { get; init; } = "";

    /// <summary>按 <c>环境变量 &gt; config.json</c> 的优先级解析。</summary>
    public static PushChannels Resolve(AppConfig cfg)
    {
        var push = cfg.Push ?? new PushConfig();

        static string Pick(string envName, string configValue)
        {
            var env = Environment.GetEnvironmentVariable(envName);
            return !string.IsNullOrWhiteSpace(env) ? env.Trim() : (configValue ?? "").Trim();
        }

        return new PushChannels
        {
            WecomWebhook = Pick("PUSH_WECOM_WEBHOOK", push.WecomWebhook),
            DingtalkWebhook = Pick("PUSH_DINGTALK_WEBHOOK", push.DingtalkWebhook),
            FeishuWebhook = Pick("PUSH_FEISHU_WEBHOOK", push.FeishuWebhook),
            TgBotToken = Pick("PUSH_TG_BOT_TOKEN", push.TgBotToken),
            TgChatId = Pick("PUSH_TG_CHAT_ID", push.TgChatId),
            ServerChanKey = Pick("PUSH_SERVERCHAN_KEY", push.ServerChanKey),
            PushPlusToken = Pick("PUSH_PUSHPLUS_TOKEN", push.PushPlusToken),
            BarkUrl = Pick("PUSH_BARK_URL", push.BarkUrl),
        };
    }
}

/// <summary>
/// 多渠道推送，逐条对齐 Python 版 <c>push_text()</c>。
///
/// 各渠道语法差异是本模块的核心难点：
/// <list type="bullet">
///   <item>企业微信 / 钉钉 / 飞书 / Server酱：不认标准 markdown 链接，需转成 HTML <c>&lt;a href&gt;</c>；</item>
///   <item>PushPlus / Bark / Telegram：支持标准 markdown <c>[text](url)</c>，Telegram 还需 MarkdownV2 转义。</item>
/// </list>
/// </summary>
public sealed class PushService
{
    /// <summary>注册在 DI 里的命名 HttpClient，超时 10 秒。</summary>
    public const string HttpClientName = "push";

    private readonly IHttpClientFactory _factory;

    public PushService(IHttpClientFactory factory) => _factory = factory;

    private HttpClient Http => _factory.CreateClient(HttpClientName);

    /// <summary>推送，返回各渠道结果汇总字符串（等价 Python 的 <c>" | ".join(results)</c>）。</summary>
    public async Task<string> PushAsync(string title, string markdownText, PushChannels channels, CancellationToken ct = default)
    {
        var results = new List<string>();
        var htmlText = MdToHtml(markdownText);
        var serverChanHtml = MdToServerChanHtml(markdownText);

        // 1. 企业微信 —— msgtype=markdown 不渲染 <a>，末尾附原始 URL 兜底方便复制
        if (!string.IsNullOrEmpty(channels.WecomWebhook))
        {
            try
            {
                var rawUrl = "";
                var m = Regex.Match(markdownText, @"\[([^\]]+)\]\((https?://[^)]+)\)");
                if (m.Success) rawUrl = $"\n\n📋 原始链接 (复制用): {m.Groups[2].Value}";

                var payload = new
                {
                    msgtype = "markdown",
                    markdown = new { content = $"# {title}\n\n{markdownText}{rawUrl}" },
                };
                var response = await PostJsonAsync(channels.WecomWebhook, payload, ct).ConfigureAwait(false);
                results.Add($"企业微信: {(response.Ok ? "OK" : "X " + Truncate(response.Body, 50))}");
            }
            catch (Exception ex)
            {
                results.Add($"企业微信: X {ex.Message}");
            }
        }

        // 2. 钉钉 —— msgtype=markdown, 支持 HTML 链接
        if (!string.IsNullOrEmpty(channels.DingtalkWebhook))
        {
            try
            {
                var payload = new
                {
                    msgtype = "markdown",
                    markdown = new { title, text = $"# {title}\n\n{htmlText}" },
                };
                var response = await PostJsonAsync(channels.DingtalkWebhook, payload, ct).ConfigureAwait(false);
                results.Add($"钉钉: {(response.Ok ? "OK" : "X")}");
            }
            catch (Exception ex)
            {
                results.Add($"钉钉: X {ex.Message}");
            }
        }

        // 3. 飞书 —— post 富文本, 只支持纯文本段落
        if (!string.IsNullOrEmpty(channels.FeishuWebhook))
        {
            try
            {
                var plainHtml = Regex.Replace(htmlText, @"\*\*(.+?)\*\*", "$1");
                plainHtml = Regex.Replace(plainHtml, @"`(.+?)`", "$1");

                var payload = new
                {
                    msg_type = "post",
                    content = new
                    {
                        post = new
                        {
                            zh_cn = new
                            {
                                title,
                                content = new object[]
                                {
                                    new object[] { new { tag = "text", text = plainHtml } },
                                },
                            },
                        },
                    },
                };
                var response = await PostJsonAsync(channels.FeishuWebhook, payload, ct).ConfigureAwait(false);
                results.Add($"飞书: {(response.Ok ? "OK" : "X")}");
            }
            catch (Exception ex)
            {
                results.Add($"飞书: X {ex.Message}");
            }
        }

        // 4. Telegram —— MarkdownV2
        if (!string.IsNullOrEmpty(channels.TgBotToken) && !string.IsNullOrEmpty(channels.TgChatId))
        {
            try
            {
                var full = $"*{EscapeTelegram(title)}*\n\n{EscapeTelegram(markdownText)}";
                var payload = new
                {
                    chat_id = channels.TgChatId,
                    text = full,
                    parse_mode = "MarkdownV2",
                    disable_web_page_preview = true,
                };
                var url = $"https://api.telegram.org/bot{channels.TgBotToken}/sendMessage";
                var response = await PostJsonAsync(url, payload, ct).ConfigureAwait(false);
                results.Add($"Telegram: {(response.Ok ? "OK" : "X")}");
            }
            catch (Exception ex)
            {
                results.Add($"Telegram: X {ex.Message}");
            }
        }

        // 5. Server酱 —— 表单提交 + HTML 正文
        if (!string.IsNullOrEmpty(channels.ServerChanKey))
        {
            try
            {
                var url = $"https://sctapi.ftqq.com/{channels.ServerChanKey}.send";
                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["title"] = title,
                    ["desp"] = serverChanHtml,
                });
                using var response = await Http.PostAsync(url, form, ct).ConfigureAwait(false);
                results.Add($"Server酱: {(response.IsSuccessStatusCode ? "OK" : "X")}");
            }
            catch (Exception ex)
            {
                results.Add($"Server酱: X {ex.Message}");
            }
        }

        // 6. PushPlus —— markdown 模板, 用标准 [text](url)
        if (!string.IsNullOrEmpty(channels.PushPlusToken))
        {
            try
            {
                var payload = new
                {
                    token = channels.PushPlusToken,
                    title,
                    content = markdownText,
                    template = "markdown",
                };
                var response = await PostJsonAsync("https://www.pushplus.plus/send", payload, ct).ConfigureAwait(false);
                results.Add($"PushPlus: {(response.Ok ? "OK" : "X")}");
            }
            catch (Exception ex)
            {
                results.Add($"PushPlus: X {ex.Message}");
            }
        }

        // 7. Bark —— URL 拼接, body 支持 markdown
        if (!string.IsNullOrEmpty(channels.BarkUrl))
        {
            try
            {
                var separator = channels.BarkUrl.Contains('?') ? "&" : "?";
                var url = $"{channels.BarkUrl}{separator}title={Uri.EscapeDataString(title)}" +
                          $"&body={Uri.EscapeDataString(markdownText)}&markdown=1";
                using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
                results.Add($"Bark: {(response.IsSuccessStatusCode ? "OK" : "X")}");
            }
            catch (Exception ex)
            {
                results.Add($"Bark: X {ex.Message}");
            }
        }

        return results.Count > 0 ? string.Join(" | ", results) : "(未配置推送渠道)";
    }

    // ==================== 内部工具 ====================

    private readonly record struct PostResult(bool Ok, string Body);

    private async Task<PostResult> PostJsonAsync(string url, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new PostResult(response.IsSuccessStatusCode, body);
    }

    /// <summary>markdown 链接 → HTML <c>&lt;a&gt;</c>（企业微信/钉钉/飞书/Server酱都认这个）。</summary>
    internal static string MdToHtml(string text)
        => Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)", "<a href=\"$2\">$1</a>");

    /// <summary>
    /// Server酱专用：完整 markdown → HTML（粗体 / 标题 / 列表 / 链接 / 代码 / 换行）。
    /// 处理顺序与原脚本严格一致，否则会破坏已生成的 &lt;a&gt; 标签。
    /// </summary>
    internal static string MdToServerChanHtml(string text)
    {
        text = MdToHtml(text);
        text = Regex.Replace(text, @"^#{1,6}\s*(.+)$", "<h3>$1</h3>", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\*\s*(.+)$", "• $1<br>", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "<b>$1</b>");
        text = Regex.Replace(text, @"`(.+?)`", "<code>$1</code>");
        return text.Replace("\n", "<br>");
    }

    /// <summary>Telegram MarkdownV2 需要转义的字符。</summary>
    internal static string EscapeTelegram(string text)
        => Regex.Replace(text, @"([_*\[\]()~`>#+\-=|{}.!\\])", "\\$1");

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) ? "" : (text.Length <= max ? text : text[..max]);
}
