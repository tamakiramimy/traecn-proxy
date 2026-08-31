using System.Text;
using System.Text.Json.Nodes;

namespace TrancnProxy;

public sealed record TraeChatResult(
    string Text,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    string FinishReason)
{
    public static async Task<TraeChatResult> CollectAsync(
        IAsyncEnumerable<TraeSseEvent> upstream,
        CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        int promptTokens = 0;
        int completionTokens = 0;
        int totalTokens = 0;
        string finishReason = "stop";
        bool completed = false;

        await foreach (var streamEvent in upstream.WithCancellation(cancellationToken))
        {
            var payload = streamEvent.Data.Length == 0
                ? null
                : JsonNode.Parse(streamEvent.Data) as JsonObject;
            switch (streamEvent.Event)
            {
                case "output":
                    text.Append((string?)payload?["response"] ?? "");
                    if (payload?["finish_reason"] is JsonValue outputReason &&
                        outputReason.TryGetValue<string>(out var parsedOutputReason))
                        finishReason = parsedOutputReason;
                    break;
                case "token_usage":
                    promptTokens = (int?)payload?["prompt_tokens"] ?? promptTokens;
                    completionTokens = (int?)payload?["completion_tokens"] ?? completionTokens;
                    totalTokens = (int?)payload?["total_tokens"] ?? totalTokens;
                    break;
                case "done":
                    if (payload?["finish_reason"] is JsonValue doneReason &&
                        doneReason.TryGetValue<string>(out var parsedDoneReason))
                        finishReason = parsedDoneReason;
                    completed = true;
                    break;
                case "error":
                    throw new TraeUpstreamException(
                        (string?)payload?["message"] ??
                        (string?)payload?["error"]?["message"] ??
                        "Trae 上游返回错误事件。");
            }
        }

        if (!completed) throw new TraeIncompleteStreamException();
        if (text.Length == 0) throw new TraeUpstreamException("Trae 上游完成但未返回有效内容。");
        if (totalTokens == 0 && (promptTokens > 0 || completionTokens > 0))
            totalTokens = promptTokens + completionTokens;

        return new TraeChatResult(
            text.ToString(),
            promptTokens,
            completionTokens,
            totalTokens,
            finishReason);
    }
}
