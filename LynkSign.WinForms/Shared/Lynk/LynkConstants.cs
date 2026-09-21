namespace LynkSign.Lynk;

/// <summary>
/// 接口常量，逐行对齐 Python 版 <c>ql_lynk.py</c> 顶部的配置常量。
/// </summary>
public static class LynkConstants
{
    public const string ApiBase = "https://app-api-gw-toc.lynkco.com";
    public const string OauthBase = "https://app-services.lynkco.com.cn";
    public const string RefreshUrl = OauthBase + "/auth/login/refresh";

    // ==================================================================
    //  H5 通道（key/secret 来自领克 H5 前端 JS，属公开常量）
    // ==================================================================

    /// <summary>H5 通道的 app key，等同于 Python 版的 <c>CA_KEY</c>。</summary>
    public const string CaKey = "204644386";

    /// <summary>H5 通道的 app secret，等同于 Python 版的 <c>CA_SECRET</c>。</summary>
    public const string CaSecret = "QCl7udM3PB9cOIOwquwPglikFQnzJRsX";

    /// <summary>
    /// H5 通道声明参与签名的请求头（<c>X-Ca-Signature-Headers</c> 的原样值）。
    /// 注意这只是「声明顺序」；真正拼接待签名字符串时要按头名 ASCII 序排列，
    /// 对本串而言两者恰好一致。
    /// </summary>
    public const string H5SignatureHeaders = "X-Ca-Key,X-Ca-Timestamp,X-Ca-Nonce,X-Ca-Signature-Method";

    // ==================================================================
    //  原生 iOS SDK 通道（抓包实测；secret 不在任何抓包里，需另行配置）
    // ==================================================================

    /// <summary>
    /// 原生 iOS SDK 通道的 app key。抓包实测值。
    ///
    /// <para>
    /// 签到写入接口（<see cref="EpSign"/>）只被授权给这个 key，H5 通道打过去恒为
    /// <c>403 Unauthorized Consumer</c>。而它的 secret <b>不会出现在任何请求/响应里</b>，
    /// 无法从抓包反推（已用 144 种待签名串拼法离线穷举验证过），
    /// 只能从 App 安装包里提取后填到 <c>ca_native_secret</c>。
    /// </para>
    /// </summary>
    public const string NativeCaKey = "203760416";

    /// <summary>
    /// 原生通道声明参与签名的请求头，逐字节照抄抓包里的
    /// <c>X-Ca-Signature-Headers</c> 值。比 H5 通道多出 <c>X-Ca-Version</c> 与 <c>token</c>。
    /// </summary>
    public const string NativeSignatureHeaders =
        "X-Ca-Key,X-Ca-Nonce,X-Ca-Signature-Method,X-Ca-Timestamp,X-Ca-Version,token";

    /// <summary>原生通道固定携带的 <c>X-Ca-Version</c>（它同时参与签名）。</summary>
    public const string NativeCaVersion = "1";

    /// <summary>原生通道的 <c>User-Agent</c>，抓包实测值。</summary>
    public const string NativeUserAgent = "CA_iOS_SDK_2.0";

    /// <summary>
    /// 原生通道的 <c>Content-Type</c>，抓包实测值。
    /// 大小写与分号后的空格都要原样保留 —— 它整串参与签名，差一个字符就是签名不匹配。
    /// </summary>
    public const string NativeContentType = "application/json; charset=UTF-8";

    public const string AppCode = "3fa3314998bd4195a9fe2df3e85e6a12";

    // ---- 业务端点 ----

    /// <summary>
    /// 【签到动作】<c>POST /up/api/v1/user/sign/upgrade</c>，请求体为空。
    ///
    /// <para>
    /// 来源：2026-09-19 的 App 抓包。判定依据不只是路径名 —— 签到前后各查了一次
    /// <see cref="EpSignDayInfo"/>，<c>signStatus</c> 从 0 变 1，中间只有这一个 POST；
    /// 响应 data 为 <c>{"todayFirstSign":true,"messageTip":"签到成功!","rewardEnergyNumber":1}</c>。
    /// </para>
    /// <para>
    /// 该 API 在网关里【只接受 POST】，且【未被授权给 H5 通道】，
    /// 因此必须走 <see cref="SignProfile.Native"/> 通道签名，并要求 <c>ca_native_secret</c>。
    /// </para>
    /// </summary>
    public const string EpSign = "/up/api/v1/user/sign/upgrade";

    /// <summary>
    /// 旧的签到端点。抓包实测它<b>仍然存在于网关</b>（返回 403 而非 404），
    /// 但同样未授权给 H5 通道，且 App 已不再使用 —— 保留常量仅为排查时对照，正常流程不再调用。
    /// </summary>
    public const string EpSignLegacy = "/up/api/v1/user/sign";

    /// <summary>
    /// 【权威】当日签到状态：今天到底签没签。
    /// 只接受 <c>GET</c>（<c>POST</c> 会返回 405），无查询参数、无请求体。
    /// </summary>
    public const string EpSignDayInfo = "/up/api/v1/user/sign/day/info";

    /// <summary>
    /// 旧接口：连续签到天数 + 补签卡数量。
    /// 仍然可用，继续作为「连续天数 / 补签卡」的来源，并兼作签到状态的兜底判定。
    /// </summary>
    public const string EpSignCardInfo = "/up/api/v1/userReward/getContinueDaysAndSignCard";

    /// <summary>签到日历（某个月份的逐日状态）。<c>POST</c>，body 为 <c>{startDate, endDate}</c> 毫秒时间戳。</summary>
    public const string EpSignCalendar = "/up/api/v1/user/sign/sign/info";

    public const string EpEnergy = "/app/energy/myEnergy";
    public const string EpTasks = "/up/api/v1/userReward/getTaskList";
    public const string EpGrowth = "/app/energy/my/growth";

    // ---- 分享端点 ----
    public const string EpShareCheck = "/app/v1/task/shareContentContectCheck";
    public const string EpShareReport = "/app/v1/task/shareContentContectReporting";
    public const string EpShareLookup = "/app/v1/task/shareCodeToUserId";
    public const string EpGetShareCode = "/app/v1/task/getShareCode";

    /// <summary>
    /// 探索广场内容流：<c>POST /app/explore/home-page/square/index2</c>。
    /// 用来动态取「当前最新的一篇文章」，避免把文章 id 写死 ——
    /// 详见 <see cref="ShareArticleFinder"/>。
    ///
    /// <para>
    /// <b>实测（2026-09-20，真网关 + 真 token）：</b>请求体必须带分页参数
    /// <c>{"dynamicSort":"new","uniqueId":"","refreshType":"MORE","pageNo":N}</c>，
    /// 不带这些参数只返回首屏、命中「文章」的概率极低。
    /// H5 通道与原生通道<b>都能调通</b>，因此这里走 H5 —— 不依赖用户配置的
    /// <c>ca_native_secret</c>，密钥没配也能正常取文章。
    /// </para>
    /// </summary>
    public const string EpExploreSquare = "/app/explore/home-page/square/index2";

    /// <summary>
    /// 取最新文章时最多翻几页。实测每页命中 7~12 篇「文章」（首页会掺「动态」），
    /// 正常第 1 页就能命中；留到 3 页是为了兜住「首页恰好全是动态」的偶发情况。
    /// </summary>
    public const int ExploreSquarePageCount = 3;

    /// <summary>H5 分享链接模板（复制到微信发给别人点，主账号 +5 能量体）。</summary>
    public const string ShareUrlTemplate =
        "https://h5.lynkco.com/app-h5/dist/web/pages/exploration/article/index.html?id={cid}&isShare={is_share}&shareCode={code}";

    /// <summary>分享每日上限（后端限频，超过会返回「今日已分享」）。</summary>
    public const int ShareDailyLimit = 1;

    // ---- 配置键名（config.json 字段 / 环境变量）----

    /// <summary>config.json 里存原生通道 app key 的字段名。</summary>
    public const string NativeKeyConfigField = "ca_native_key";

    /// <summary>config.json 里存原生通道 app secret 的字段名。</summary>
    public const string NativeSecretConfigField = "ca_native_secret";

    /// <summary>原生通道 app key 的环境变量名。</summary>
    public const string NativeKeyEnvVar = "LYNK_CA_NATIVE_KEY";

    /// <summary>原生通道 app secret 的环境变量名。</summary>
    public const string NativeSecretEnvVar = "LYNK_CA_NATIVE_SECRET";
}
