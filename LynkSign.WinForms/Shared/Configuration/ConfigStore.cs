using System.Text.Encodings.Web;
using System.Text.Json;
using LynkSign.Infrastructure;

namespace LynkSign.Configuration;

/// <summary>
/// config.json 的读写，对应 Python 版的 <c>read_config()</c> / <c>write_config()</c> /
/// <c>load_config()</c> / <c>update_config_field()</c>。
///
/// 关键约定：
/// <list type="bullet">
///   <item>写盘采用「临时文件 + 原子替换」，避免写一半崩溃导致配置损坏。</item>
///   <item>读取时缺失字段用默认值补齐（顶层浅合并 + push 深合并）。</item>
///   <item>写盘前做字段裁剪，杜绝前端塞进来的野字段污染配置文件。</item>
/// </list>
/// </summary>
public sealed class ConfigStore
{
    /// <summary>JSON 读取选项（宽容解析：允许注释、尾逗号、token_b 的字符串形态）。</summary>
    public static JsonSerializerOptions JsonReadOptions => ReadOptions;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new StringListConverter(), new LenientBoolConverter() },
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // 中文不转义成 \uXXXX，保证配置文件人类可读
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new StringListConverter(), new LenientBoolConverter() },
    };

    private readonly object _lock = new();

    /// <summary>配置文件绝对路径。</summary>
    public string Path { get; } = AppPaths.ConfigFile;

    /// <summary>读取配置；文件不存在 / 解析失败时返回默认配置。</summary>
    public AppConfig Read()
    {
        lock (_lock)
        {
            if (!File.Exists(Path)) return AppConfig.CreateDefault();

            try
            {
                var json = File.ReadAllText(Path);
                if (string.IsNullOrWhiteSpace(json)) return AppConfig.CreateDefault();

                var cfg = JsonSerializer.Deserialize<AppConfig>(json, ReadOptions);
                return Merge(cfg);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] 解析 {Path} 失败, 用默认值: {ex.GetType().Name}: {ex.Message}");
                return AppConfig.CreateDefault();
            }
        }
    }

    /// <summary>裁剪 + 原子写入，返回落盘后的配置。</summary>
    public AppConfig Write(AppConfig? data)
    {
        var clean = (data ?? AppConfig.CreateDefault()).Sanitized();

        lock (_lock)
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(clean, WriteOptions));

            // File.Move(overwrite: true) 在 Windows / Linux 上都是原子替换
            File.Move(tmp, Path, overwrite: true);
        }

        return clean;
    }

    /// <summary>
    /// 原子更新单个字段（用于 refreshToken 轮换写回），对应 Python 的 <c>update_config_field()</c>。
    /// 返回 (是否成功, 说明信息)。
    /// </summary>
    public (bool Ok, string Info) UpdateField(Action<AppConfig> mutate)
    {
        try
        {
            var cfg = Read();
            mutate(cfg);
            var written = Write(cfg);
            _ = written;
            return (true, Path);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>缺失字段补默认值（等价 Python 的 <c>dict(DEFAULT_CONFIG) + update</c>）。</summary>
    private static AppConfig Merge(AppConfig? cfg)
    {
        if (cfg is null) return AppConfig.CreateDefault();

        cfg.RefreshToken ??= "";
        cfg.DeviceId ??= "";
        cfg.Cron = string.IsNullOrWhiteSpace(cfg.Cron) ? AppConfig.DefaultCron : cfg.Cron;
        cfg.TokenB ??= [];
        cfg.ShareContentId ??= "";
        if (string.IsNullOrWhiteSpace(cfg.ShareContentId)) cfg.ShareContentId = AppConfig.DefaultShareContentId;
        cfg.ShareContentMode ??= "";
        cfg.OpenShareUrl ??= "";
        cfg.HttpProxy ??= "";
        cfg.CaNativeKey ??= "";
        cfg.CaNativeSecret ??= "";
        cfg.Push ??= new PushConfig();

        var p = cfg.Push;
        p.WecomWebhook ??= "";
        p.DingtalkWebhook ??= "";
        p.FeishuWebhook ??= "";
        p.TgBotToken ??= "";
        p.TgChatId ??= "";
        p.ServerChanKey ??= "";
        p.PushPlusToken ??= "";
        p.BarkUrl ??= "";

        return cfg;
    }
}
