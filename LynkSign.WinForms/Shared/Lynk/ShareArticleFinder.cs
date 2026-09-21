using System.Text.Json;
using LynkSign.Infrastructure;

namespace LynkSign.Lynk;

/// <summary>一篇文章的最小信息：id 与标题（标题可能为空，仅用于日志/推送展示）。</summary>
public sealed record ShareArticle(string ArticleId, string Title)
{
    /// <summary>给日志/推送用的一行展示。</summary>
    public string Display => string.IsNullOrWhiteSpace(Title) ? ArticleId : $"{Title}  (id={ArticleId})";
}

/// <summary>
/// 从探索广场内容流（<c>POST /app/explore/home-page/square/index2</c>）的响应里
/// 找出第一篇文章的 id。
///
/// <para>
/// 为什么要动态取：分享链接里带着文章 id，B 账号点开加分时后端会校验两步用的是同一个 id。
/// 硬编码一个 id 迟早会撞上文章被下架，而那时不会有任何报错 ——
/// 只会表现为「签到成功、分享也显示成功，但能量体没涨」。
/// </para>
///
/// <para>
/// 为什么是「递归找第一个」而不是写死 <c>data.userByteDynamicsResponseDTOS</c> 这个路径：
/// 该接口是首页推荐流，同一份响应里混着「动态 / 文章 / 视频」多种内容，
/// 而且字段名随 App 版本变过（v3/upgrade、index2 …）。按「深度优先 + 文档顺序」找第一个
/// 带 <c>articleId</c> 且类型是文章的节点，既不需要跟着改字段名，
/// 又能天然落在推荐流的时间顺序上 —— 第一个就是最新的那篇。
/// </para>
/// </summary>
public static class ShareArticleFinder
{
    /// <summary>「文章」类型的取值集合。<c>contentType</c> 是中文、<c>contentTypeCode</c> 是英文。</summary>
    private static readonly string[] ArticleTypeValues = ["文章", "article"];

    /// <summary>
    /// 深度优先找出第一个文章节点；没找到返回 <c>null</c>。
    ///
    /// <para>
    /// 命中条件：该对象有非空 <c>articleId</c>，且它的类型字段为空或明确是「文章」。
    /// 「类型字段为空也算命中」是刻意留的宽松口子 —— 万一将来后端不再下发类型字段，
    /// 这条兜底还能让功能继续工作，而不是直接返回 null。
    /// </para>
    /// </summary>
    public static ShareArticle? FindFirst(JsonElement? root)
    {
        if (root is not { } element) return null;

        // 优先只看 data —— 正常响应里内容流都在它下面，先扫它等于「只看推荐流」。
        // data 缺失、或 data 里压根没有文章时，才退一步扫整个根节点：
        // 万一将来内容流被挪到 data 外面（或改成 data 直接是数组），这条兜底还能工作。
        return Scan(root.Prop("data")) ?? Scan(element);
    }

    private static ShareArticle? Scan(JsonElement? node)
    {
        if (node is not { } element) return null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryReadArticle(element) is { } article) return article;

                // 按文档顺序遍历子节点（JsonElement 的枚举本身就是文档顺序），
                // 这样「第一个」= 推荐流里最靠前的那篇。
                foreach (var property in element.EnumerateObject())
                {
                    if (Scan(property.Value) is { } found) return found;
                }
                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (Scan(item) is { } found) return found;
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>该对象本身是不是一篇文章；不是则返回 null。</summary>
    private static ShareArticle? TryReadArticle(JsonElement element)
    {
        // Str 对数字也返回其原文，所以 articleId 是字符串或数字都能取到。
        var articleId = element.Str("articleId");
        if (string.IsNullOrWhiteSpace(articleId)) return null;

        // contentType（中文）优先，缺失时看 contentTypeCode（英文）。语义对齐参考实现里
        // 的 `value.get("contentType") or value.get("contentTypeCode")`。
        var type = element.Str("contentType");
        if (string.IsNullOrWhiteSpace(type)) type = element.Str("contentTypeCode");

        if (type.Length > 0 && !ArticleTypeValues.Contains(type, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var title = element.Str("title");
        if (string.IsNullOrWhiteSpace(title)) title = element.Str("articleTitle");

        return new ShareArticle(articleId.Trim(), title.Trim());
    }

    /// <summary>
    /// 离线回归：用真实响应裁剪出的样本把解析规则钉死。
    ///
    /// <para>
    /// 这段逻辑错了不会抛异常，只会「取到一个视频/动态的 id」或者「什么都没取到然后静默回退」，
    /// 表现都是分享不加分 —— 而分享加不加分要看 2 秒后能量体有没有涨，排查成本很高。
    /// 所以这里用固定样本把边界都覆盖掉。
    /// </para>
    /// </summary>
    public static (bool Ok, List<string> Lines) SelfCheck()
    {
        var lines = new List<string>();
        var ok = true;

        // 故意不 Dispose JsonDocument：RootElement 在文档 Dispose 之后就失效了，
        // 而自检样本的生命周期是整个进程，所以这里返回元素、把文档留着。
        static JsonElement Fixture(string json) => JsonDocument.Parse(json).RootElement;

        void Expect(string label, string? actual, string? expected)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal)) ok = false;
            lines.Add($"{(string.Equals(actual, expected, StringComparison.Ordinal) ? "  [OK] " : "  [X]  ")}"
                + $"{label}: 实际={actual ?? "<null>"} 期望={expected ?? "<null>"}");
        }

        // ---- 样本 1：真实响应裁剪（首页推荐流里「动态」在前、「文章」在后）----
        const string mixedFixture = """
        {
          "code": "success",
          "data": {
            "myZones": [{ "imgUrl": "https://x/y.jpg", "joinTime": "1754534183000" }],
            "userByteDynamicsResponseDTOS": [
              { "dynamicId": "900001", "contentType": "动态", "contentTypeCode": "dynamic", "summary": "自由自在，说走就走" },
              { "articleId": "2101514669964636160", "title": "领克03家族｜感官之上，性能登场", "contentType": "文章", "contentTypeCode": "article" },
              { "articleId": "2101518193272213504", "title": "领克06｜六年朝夕相伴", "contentType": "文章", "contentTypeCode": "article" }
            ]
          }
        }
        """;

        var mixed = FindFirst(Fixture(mixedFixture));
        Expect("混合内容流 → 取到第一篇【文章】而不是前面的动态",
            mixed?.ArticleId, "2101514669964636160");
        Expect("  └ 标题一并带出", mixed?.Title, "领克03家族｜感官之上，性能登场");

        // ---- 样本 2：视频节点带 articleId，不能被当成文章 ----
        const string videoFixture = """
        { "data": { "list": [
            { "articleId": "111", "title": "视频标题", "contentType": "视频", "contentTypeCode": "video" },
            { "articleId": "222", "title": "真正的文章", "contentType": "文章", "contentTypeCode": "article" }
        ] } }
        """;
        Expect("带 articleId 的视频节点必须被跳过",
            FindFirst(Fixture(videoFixture))?.ArticleId, "222");

        // ---- 样本 3：articleId 是数字 ----
        const string numericFixture = """{ "data": { "records": [ { "articleId": 333, "contentType": "文章" } ] } }""";
        Expect("articleId 为数字也能读", FindFirst(Fixture(numericFixture))?.ArticleId, "333");

        // ---- 样本 4：没有类型字段 → 宽松命中（将来后端不下发类型时的兜底）----
        const string noTypeFixture = """{ "data": { "list": [ { "articleId": "444", "title": "无类型字段" } ] } }""";
        Expect("类型字段缺失时宽松命中", FindFirst(Fixture(noTypeFixture))?.ArticleId, "444");

        // ---- 样本 5：整个流里没有文章 → null（上层据此回退到配置值）----
        const string noArticleFixture = """
        { "data": { "userByteDynamicsResponseDTOS": [
            { "dynamicId": "1", "contentType": "动态", "contentTypeCode": "dynamic" },
            { "dynamicId": "2", "contentType": "动态", "contentTypeCode": "dynamic" }
        ] } }
        """;
        Expect("没有文章时必须返回 null", FindFirst(Fixture(noArticleFixture))?.ArticleId, null);

        // ---- 样本 6：data 缺失 / articleId 为空串 ----
        Expect("响应里连 data 都没有", FindFirst(Fixture("""{"code":"success"}"""))?.ArticleId, null);
        Expect("articleId 为空串", FindFirst(Fixture("""{"data":{"list":[{"articleId":"","contentType":"文章"}]}}"""))?.ArticleId, null);

        // ---- 样本 7：Display 的排版（有标题 / 无标题两种）----
        Expect("Display 带标题", new ShareArticle("555", "标题").Display, "标题  (id=555)");
        Expect("Display 无标题", new ShareArticle("555", "").Display, "555");

        return (ok, lines);
    }
}
