using System.Text.RegularExpressions;

namespace LynkSign.Services;

/// <summary>
/// 写回青龙面板 env 文件，对应 Python 版的 <c>update_ql_env()</c>。
/// 仅在确实存在 env 文件时才生效（Docker 部署下一般不可用，属于兼容性兜底）。
/// </summary>
public static class QlEnvUpdater
{
    private static readonly string[] Candidates =
    [
        Environment.GetEnvironmentVariable("QL_ENV_FILE") ?? "",
        "/ql/config/env.sh",
        "/ql/.env",
        "/ql/.env.local",
        "/data/config/env.sh",
    ];

    public static (bool Ok, string Info) Update(string variableName, string value)
    {
        var envFile = Candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
        if (envFile is null)
        {
            return (false, "未找到 env 文件 (设置 QL_ENV_FILE 或放 /ql/config/env.sh)");
        }

        try
        {
            var content = File.ReadAllText(envFile);
            var pattern = new Regex($@"^export\s+{Regex.Escape(variableName)}=.*$", RegexOptions.Multiline);
            var newLine = $"export {variableName}=\"{value}\"";

            // 用 MatchEvaluator 替换, 避免值与替换串里的 $ / \ 被当成反向引用
            content = pattern.IsMatch(content)
                ? pattern.Replace(content, _ => newLine)
                : (content.EndsWith('\n') ? content : content + "\n") + newLine + "\n";

            File.WriteAllText(envFile, content);
            return (true, envFile);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
