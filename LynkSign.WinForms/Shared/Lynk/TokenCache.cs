using System.Text.Json;
using System.Text.Json.Serialization;
using LynkSign.Infrastructure;

namespace LynkSign.Lynk;

/// <summary>accessToken 缓存文件的内容结构（与 Python 版字段名保持一致）。</summary>
public sealed class TokenCacheEntry
{
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("expire_at")] public string ExpireAt { get; set; } = "";
    [JsonPropertyName("saved_at")] public string SavedAt { get; set; } = "";
    [JsonPropertyName("refresh_token_new")] public string? RefreshTokenNew { get; set; }
    [JsonPropertyName("refresh_expire_at")] public string? RefreshExpireAt { get; set; }
}

/// <summary>
/// accessToken 本地缓存，对应 Python 版的 <c>get_cache_file()</c> / <c>load_cached_at()</c> /
/// <c>save_cached_at()</c> / <c>clear_cached_at()</c>。
///
/// 缓存命中条件（三者同时满足）：
/// <list type="number">
///   <item>文件存在且能解析；</item>
///   <item>refreshToken 与当前一致（轮换过即作废）；</item>
///   <item>device_id 一致；</item>
///   <item>expire_at 距现在还有 &gt; 60 秒。</item>
/// </list>
/// </summary>
public sealed class TokenCache
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>缓存文件绝对路径。</summary>
    public string Path { get; }

    public TokenCache() => Path = ResolvePath();

    /// <summary>解析缓存文件位置：环境变量 &gt; 青龙目录（仅 Linux）&gt; /data 卷 &gt; 运行目录 &gt; 临时目录 &gt; 用户主目录。</summary>
    private static string ResolvePath()
    {
        if (!string.IsNullOrWhiteSpace(AppPaths.CacheFile)) return EnsureWritable(AppPaths.CacheFile);

        var candidates = new List<string>();

        if (OperatingSystem.IsLinux())
        {
            // 兼容青龙面板：优先把缓存放到持久化数据目录
            candidates.Add("/ql/data/lynk_access_token.json");
            candidates.Add("/ql/.lynk_access_token.json");
            candidates.Add("/data/.lynk_access_token.json");
        }

        candidates.Add(System.IO.Path.Combine(AppPaths.WorkDir, ".lynk_access_token.json"));
        candidates.Add(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lynk_access_token.json"));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            candidates.Add(System.IO.Path.Combine(home, ".lynk_access_token.json"));
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                return candidate;
            }
            catch
            {
                // 目录不可写, 试下一个
            }
        }

        return candidates[^1];
    }

    private static string EnsureWritable(string path)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch
        {
            // 交给后续读写自己报错
        }
        return path;
    }

    /// <summary>读取缓存；无效则返回 null。等价 Python 的 <c>load_cached_at()</c>。</summary>
    public TokenCacheEntry? Load(string refreshToken, string deviceId)
    {
        try
        {
            if (!File.Exists(Path)) return null;

            var entry = JsonSerializer.Deserialize<TokenCacheEntry>(File.ReadAllText(Path));
            if (entry is null) return null;

            if (!string.Equals(entry.RefreshToken, refreshToken, StringComparison.Ordinal)) return null;
            if (!string.Equals(entry.DeviceId, deviceId, StringComparison.Ordinal)) return null;

            if (!DateTime.TryParse(entry.ExpireAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var expireAt))
            {
                return null;
            }

            if (TimeUtil.Now() >= expireAt.AddSeconds(-60)) return null;
            return entry;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>写入缓存。等价 Python 的 <c>save_cached_at()</c>，返回实际路径或 null。</summary>
    public string? Save(string refreshToken, string deviceId, string accessToken, DateTime? expireAt,
                        string? newRefreshToken = null, DateTime? refreshExpireAt = null)
    {
        try
        {
            var entry = new TokenCacheEntry
            {
                RefreshToken = refreshToken,
                DeviceId = deviceId,
                AccessToken = accessToken,
                ExpireAt = Format(expireAt),
                SavedAt = TimeUtil.Now().ToString("yyyy-MM-ddTHH:mm:ss.fffffff"),
                RefreshTokenNew = string.IsNullOrEmpty(newRefreshToken) ? null : newRefreshToken,
                RefreshExpireAt = refreshExpireAt is null ? null : Format(refreshExpireAt),
            };

            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(Path, JsonSerializer.Serialize(entry, Options));

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch
                {
                    // 文件系统不支持权限位, 忽略
                }
            }

            return Path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清空缓存，返回被删除的路径（未删除时也返回路径）。</summary>
    public string Clear()
    {
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
        catch
        {
            // 忽略
        }
        return Path;
    }

    private static string Format(DateTime? value)
        => value?.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "";
}
