using System.Net;

namespace LynkSign.Infrastructure;

/// <summary>
/// 出站 HTTP handler 的统一构造（Web 版与桌面版共用同一份）。
///
/// <para>
/// <b>为什么不能吃框架默认值：</b>.NET 的 <c>HttpClientHandler</c> 默认 <c>UseProxy = true</c>，
/// 代理取自系统「Internet 选项」里的设置。国内代理软件（Clash Verge / Mihomo / v2rayN 等）
/// 会把系统代理写成 <c>127.0.0.1:7897</c>（或 7890 / 10809），
/// <b>一旦代理进程退出而注册表设置没被清干净，所有请求就会打向这个死端口</b>，报：
/// </para>
/// <code>
/// HttpRequestException: 由于目标计算机积极拒绝，无法连接。(127.0.0.1:7897)
/// </code>
/// <para>
/// 这个报错看起来像"接口挂了 / 网络断了"，实际跟领克接口毫无关系（实测直连 80ms 返回 401）。
/// 所以这里把默认值改成<b>直连、不吃系统代理</b>；确需代理时按下面的优先级取。
/// </para>
///
/// <para><b>代理取值优先级：</b></para>
/// <list type="number">
///   <item><b>config.json 的 <c>http_proxy</c></b>：
///     <c>"direct"</c> 强制直连 ／ <c>"system"</c> 沿用系统代理 ／ <c>http://host:port</c> 走指定代理；
///     留空（默认）则看下一条。</item>
///   <item><b>环境变量</b> <c>LYNK_HTTP_PROXY</c> → <c>HTTPS_PROXY</c> → <c>HTTP_PROXY</c> → <c>ALL_PROXY</c>
///     （Docker / Linux 部署惯用这套；也顺带避免老部署里已设的代理变量失效）。</item>
///   <item>都没有 → <b>直连</b>。</item>
/// </list>
/// </summary>
public static class HttpClientSetup
{
    /// <summary>config.json 里 <c>http_proxy</c> 的取值：强制直连。</summary>
    public const string ProxyDirect = "direct";

    /// <summary>config.json 里 <c>http_proxy</c> 的取值：沿用系统代理（.NET 的原始默认行为）。</summary>
    public const string ProxySystem = "system";

    /// <summary>用于探测"系统代理到底有没有生效"的目标地址（领克网关，国内直连可达）。</summary>
    private static readonly Uri ProbeUri = new("https://app-api-gw-toc.lynkco.com/");

    private static readonly string[] EnvProxyNames =
    [
        "LYNK_HTTP_PROXY", "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy",
    ];

    /// <summary>按优先级解析出站策略，构造一个全新的 handler（由 IHttpClientFactory 负责缓存与轮换）。</summary>
    public static HttpMessageHandler CreateHandler(string? proxySetting)
    {
        var handler = new SocketsHttpHandler
        {
            // 安全默认：直连。领克网关在阿里云杭州，国内不需要任何代理。
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
        };

        var raw = (proxySetting ?? "").Trim();

        // ---- 1) config.json 显式指定 ----
        if (raw.Length > 0)
        {
            if (raw.Equals(ProxyDirect, StringComparison.OrdinalIgnoreCase)) return handler;

            if (raw.Equals(ProxySystem, StringComparison.OrdinalIgnoreCase))
            {
                handler.UseProxy = true;
                handler.Proxy = HttpClient.DefaultProxy;   // 显式绑定，避免"null 到底走哪条路"的歧义
                return handler;
            }

            if (TryParseProxy(raw, out var configured))
            {
                handler.UseProxy = true;
                handler.Proxy = configured;
                return handler;
            }

            Console.WriteLine($"[!] config.json 的 http_proxy 不是合法代理地址，已忽略并直连: {raw}");
            Console.WriteLine("    合法写法: http://127.0.0.1:7897 ｜ system ｜ direct ｜ 留空(直连)");
            return handler;
        }

        // ---- 2) 环境变量（Docker / Linux 部署） ----
        foreach (var name in EnvProxyNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (TryParseProxy(value.Trim(), out var fromEnv))
            {
                handler.UseProxy = true;
                handler.Proxy = fromEnv;
                return handler;
            }
        }

        // ---- 3) 直连 ----
        return handler;
    }

    /// <summary>
    /// 一行描述当前出站策略，启动时打进日志。
    /// 下次再遇到"127.0.0.1:xxxx 连接被拒"，看一眼这行就知道是不是代理没清干净。
    /// </summary>
    public static string Describe(string? proxySetting)
    {
        var raw = (proxySetting ?? "").Trim();

        if (raw.Length > 0)
        {
            if (raw.Equals(ProxyDirect, StringComparison.OrdinalIgnoreCase))
                return $"直连（config.json http_proxy={ProxyDirect}）";

            if (raw.Equals(ProxySystem, StringComparison.OrdinalIgnoreCase))
                return $"系统代理 {SystemProxyHint()}（config.json http_proxy={ProxySystem}）";

            if (TryParseProxy(raw, out _))
                return $"指定代理 {raw}（config.json http_proxy）";

            return $"直连（http_proxy=\"{raw}\" 不是合法地址，已忽略）";
        }

        foreach (var name in EnvProxyNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) return $"环境变量 {name}={value.Trim()}";
        }

        var system = SystemProxyHint();
        return system.Length > 0
            ? $"直连 · 已忽略系统代理 {system}"
            : "直连";
    }

    /// <summary>当前系统代理地址（形如 <c>http://127.0.0.1:7897</c>）；没开代理时返回空串。</summary>
    private static string SystemProxyHint()
    {
        try
        {
            var uri = HttpClient.DefaultProxy?.GetProxy(ProbeUri);
            if (uri is null) return "";

            // 不走代理时 GetProxy 会把目标地址原样返回，据此判断"到底有没有代理"
            if (string.Equals(uri.Host, ProbeUri.Host, StringComparison.OrdinalIgnoreCase)) return "";

            return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>宽松解析代理地址：允许省略 scheme（<c>127.0.0.1:7897</c> 视作 http）。</summary>
    private static bool TryParseProxy(string value, out IWebProxy proxy)
    {
        proxy = default!;

        var text = value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (string.IsNullOrEmpty(uri.Host)) return false;
        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals("socks4", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        proxy = new WebProxy(uri);
        return true;
    }
}
