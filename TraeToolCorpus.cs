using System.Text.Json.Nodes;

namespace TrancnProxy;

// 线上每条被拒绝的工具调用都要留档：测试靠回放这些真实载荷扩覆盖率，而不是靠人工想象补用例。
public static class TraeToolCorpus
{
    // 上限只为防日志失控；定得太低会把样本本身截断，回放时就在测一个不存在的畸形。
    private const int MaxPayloadChars = 200_000;
    private static readonly object Gate = new();
    private static string? _directory;

    public static void Configure(string? directory) =>
        _directory = string.IsNullOrWhiteSpace(directory) ? null : directory;

    public static void Record(string toolName, string reason, string payload)
    {
        string? directory = _directory;
        if (directory is null || string.IsNullOrEmpty(payload)) return;

        string line = new JsonObject
        {
            ["at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["tool"] = toolName,
            ["reason"] = reason,
            ["payload"] = payload.Length <= MaxPayloadChars ? payload : payload[..MaxPayloadChars]
        }.ToJsonString();

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, $"tool-failures-{DateTime.UtcNow:yyyyMMdd}.jsonl"),
                    line + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
