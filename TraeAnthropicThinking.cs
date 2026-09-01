using System;
using System.Text.Json.Nodes;

namespace TrancnProxy;

/// <summary>
/// Anthropic's streaming spec requires a non-empty "signature" on thinking content blocks
/// (see signature_delta in the Messages Streaming API). Trae/Qwen upstreams never produce a
/// real cryptographic signature, so this placeholder exists purely to satisfy client-side
/// schema validation; it is never verified against a real Anthropic backend.
/// </summary>
public static class TraeAnthropicThinking
{
    public const string SignaturePlaceholder = "unsigned-trae-proxy-thinking";

    /// <summary>
    /// Anthropic defines "enabled", "adaptive" and "disabled". Real Claude Desktop sends
    /// {"type":"adaptive","display":"omitted"}, so matching only "enabled" silently drops
    /// reasoning for every desktop request.
    /// </summary>
    public static bool IsEnabled(JsonNode? thinking)
    {
        if (thinking is null) return false;
        string? type = (string?)thinking["type"];
        if (string.IsNullOrWhiteSpace(type)) return false;
        return !string.Equals(type, "disabled", StringComparison.OrdinalIgnoreCase);
    }

    public static JsonObject ContentBlockStart() =>
        new() { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" };

    public static JsonObject SignatureDelta() =>
        new() { ["type"] = "signature_delta", ["signature"] = SignaturePlaceholder };

    public static JsonObject CompletedContent(string thinkingText) =>
        new() { ["type"] = "thinking", ["thinking"] = thinkingText, ["signature"] = SignaturePlaceholder };
}
