using System.Text;
using System.Text.Json.Nodes;

namespace TrancnProxy;

public abstract record TraeOutputBlock;

public sealed record TraeTextBlock(string Text) : TraeOutputBlock;

public sealed record TraeToolUseBlock(string Id, string Name, JsonObject Input) : TraeOutputBlock;

public static class TraeToolProtocol
{
    private const string OpenTag = "<tool_call>";
    private const string CloseTag = "</tool_call>";

    public static string BuildSystemPrompt(JsonNode? system, JsonArray? tools, JsonNode? toolChoice = null)
    {
        var sections = new List<string>();
        string systemText = ContentText(system);
        if (!string.IsNullOrWhiteSpace(systemText)) sections.Add(systemText);

        string choiceType = (string?)toolChoice?["type"] ?? "auto";
        if (tools is { Count: > 0 } && choiceType != "none")
        {
            string choiceInstruction = choiceType switch
            {
                "tool" when (string?)toolChoice?["name"] is { Length: > 0 } name =>
                    $"You MUST call the '{name}' tool. Do not answer with prose instead of calling it.",
                "any" => "You MUST call at least one available tool. Do not answer with prose instead of calling a tool.",
                _ => "Call an available tool whenever it is needed to complete the request."
            };
            sections.Add(choiceInstruction + "\n" + """
You have access to the tools described below. When a tool is needed, output exactly one JSON object inside these tags and do not describe the call as prose:
<tool_call>{"name":"tool_name","arguments":{"parameter":"value"}}</tool_call>
You may emit multiple tool_call blocks when calls can run in parallel. Tool definitions:
""" + "\n" + tools.ToJsonString());
        }

        return string.Join("\n\n", sections);
    }

    public static string ContentText(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out string? text))
            return text ?? "";
        if (content is not JsonArray blocks) return "";

        var parts = new List<string>();
        foreach (JsonNode? node in blocks)
        {
            if (node is not JsonObject block) continue;
            string type = (string?)block["type"] ?? "text";
            switch (type)
            {
                case "text":
                    if ((string?)block["text"] is { Length: > 0 } blockText) parts.Add(blockText);
                    break;
                case "tool_use":
                    parts.Add(new JsonObject
                    {
                        ["id"] = (string?)block["id"],
                        ["name"] = (string?)block["name"],
                        ["arguments"] = block["input"]?.DeepClone() ?? new JsonObject()
                    }.ToJsonString().Insert(0, OpenTag) + CloseTag);
                    break;
                case "tool_result":
                    parts.Add($"<tool_result>{new JsonObject
                    {
                        ["tool_use_id"] = (string?)block["tool_use_id"],
                        ["is_error"] = (bool?)block["is_error"] ?? false,
                        ["content"] = ContentText(block["content"])
                    }.ToJsonString()}</tool_result>");
                    break;
            }
        }
        return string.Join("\n", parts);
    }

    public static IReadOnlyList<TraeOutputBlock> Parse(string text)
    {
        var parser = new StreamParser();
        var blocks = parser.Push(text).ToList();
        blocks.AddRange(parser.Complete());
        return blocks;
    }

    public sealed class StreamParser
    {
        private readonly StringBuilder _buffer = new();
        private bool _inToolCall;

        public IReadOnlyList<TraeOutputBlock> Push(string chunk)
        {
            _buffer.Append(chunk);
            return TakeCompletedBlocks(final: false);
        }

        public IReadOnlyList<TraeOutputBlock> Complete() => TakeCompletedBlocks(final: true);

        private List<TraeOutputBlock> TakeCompletedBlocks(bool final)
        {
            var blocks = new List<TraeOutputBlock>();
            while (_buffer.Length > 0)
            {
                string buffered = _buffer.ToString();
                if (_inToolCall)
                {
                    int closeIndex = buffered.IndexOf(CloseTag, StringComparison.Ordinal);
                    if (closeIndex < 0)
                    {
                        if (final)
                        {
                            blocks.Add(new TraeTextBlock(OpenTag + buffered));
                            _buffer.Clear();
                            _inToolCall = false;
                        }
                        break;
                    }

                    string payload = buffered[..closeIndex].Trim();
                    _buffer.Remove(0, closeIndex + CloseTag.Length);
                    if (ParseToolUse(payload) is { } toolUse) blocks.Add(toolUse);
                    else blocks.Add(new TraeTextBlock(OpenTag + payload + CloseTag));
                    _inToolCall = false;
                    continue;
                }

                int openIndex = buffered.IndexOf(OpenTag, StringComparison.Ordinal);
                if (openIndex >= 0)
                {
                    if (openIndex > 0) blocks.Add(new TraeTextBlock(buffered[..openIndex]));
                    _buffer.Remove(0, openIndex + OpenTag.Length);
                    _inToolCall = true;
                    continue;
                }

                int retainedLength = final ? 0 : PartialOpenTagLength(buffered);
                int textLength = buffered.Length - retainedLength;
                if (textLength > 0)
                {
                    blocks.Add(new TraeTextBlock(buffered[..textLength]));
                    _buffer.Remove(0, textLength);
                }
                if (final && _buffer.Length > 0)
                {
                    blocks.Add(new TraeTextBlock(_buffer.ToString()));
                    _buffer.Clear();
                }
                break;
            }
            return blocks;
        }

        private static int PartialOpenTagLength(string value)
        {
            int maximum = Math.Min(value.Length, OpenTag.Length - 1);
            for (int length = maximum; length > 0; length--)
            {
                if (OpenTag.StartsWith(value[^length..], StringComparison.Ordinal)) return length;
            }
            return 0;
        }

        private static TraeToolUseBlock? ParseToolUse(string payload)
        {
            try
            {
                if (JsonNode.Parse(payload) is not JsonObject tool) return null;
                string? name = (string?)tool["name"];
                if (string.IsNullOrWhiteSpace(name)) return null;
                string id = (string?)tool["id"] ?? $"toolu_{Guid.NewGuid():N}";
                JsonNode? arguments = tool["arguments"] ?? tool["input"] ?? tool["parameters"];
                if (arguments is JsonValue value && value.TryGetValue<string>(out string? serialized))
                    arguments = JsonNode.Parse(serialized ?? "{}");
                return new TraeToolUseBlock(id, name, arguments as JsonObject ?? new JsonObject());
            }
            catch
            {
                return null;
            }
        }
    }
}