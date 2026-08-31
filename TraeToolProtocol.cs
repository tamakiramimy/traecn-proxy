using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TrancnProxy;

public abstract record TraeOutputBlock;

public sealed record TraeTextBlock(string Text) : TraeOutputBlock;

public sealed record TraeToolUseBlock(string Id, string Name, JsonObject Input) : TraeOutputBlock;

public sealed record TraeToolUseStartBlock(string Id, string Name) : TraeOutputBlock;

public sealed record TraeToolInputDeltaBlock(string PartialJson) : TraeOutputBlock;

public sealed record TraeToolUseEndBlock : TraeOutputBlock;

public static class TraeToolProtocol
{
    private const string OpenTag = "<tool_call>";
    private const string CloseTag = "</tool_call>";
    private static readonly Regex EnglishAction = new(
        @"\b(create|write|build|implement|modify|edit|fix|delete|rename|move|run|execute|install|test|inspect|search|read|open)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EnglishTarget = new(
        @"\b(file|code|project|workspace|app|application|page|website|script|game|component|endpoint|command)\b|\.[a-z0-9]{1,10}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
                _ => "Call an available tool whenever it is needed to complete the request. Requests to create, read, modify, search, run, or inspect files, code, commands, or workspace state MUST use the appropriate tools. Never claim that an action was completed without a successful tool result."
            };
            sections.Add(choiceInstruction + "\n" + """
You have access to the tools described below. When a tool is needed, output exactly one JSON object inside these tags and do not describe the call as prose.
The object keys MUST be in this order: name first, arguments second:
<tool_call>{"name":"tool_name","arguments":{"parameter":"value"}}</tool_call>
You may emit multiple tool_call blocks when calls can run in parallel. Tool definitions:
""" + "\n" + tools.ToJsonString());
        }

        return string.Join("\n\n", sections);
    }

    public static bool ShouldForceToolUse(JsonArray? messages, JsonArray? tools, JsonNode? toolChoice)
    {
        if (tools is not { Count: > 0 } || ((string?)toolChoice?["type"] ?? "auto") != "auto") return false;
        if (!tools.OfType<JsonObject>().Any(tool => IsExecutionTool((string?)tool["name"] ?? ""))) return false;

        JsonObject? lastUser = messages?
            .OfType<JsonObject>()
            .LastOrDefault(message => (string?)message["role"] == "user");
        if (lastUser is null) return false;
        if (lastUser["content"] is JsonArray blocks &&
            blocks.OfType<JsonObject>().Any(block => (string?)block["type"] == "tool_result")) return false;

        string text = ContentText(lastUser["content"]);
        bool chineseAction = ContainsAny(text, "创建", "新建", "生成", "写一个", "帮我写", "修改", "修复", "实现", "开发", "删除", "重命名", "移动", "运行", "执行", "安装", "测试", "检查", "搜索", "读取", "打开");
        bool chineseTarget = ContainsAny(text, "文件", "代码", "项目", "工作区", "应用", "页面", "网页", "脚本", "游戏", "网站", "组件", "接口", "命令", "H5", "h5");
        return (chineseAction && chineseTarget) || (EnglishAction.IsMatch(text) && EnglishTarget.IsMatch(text));
    }

    private static bool IsExecutionTool(string name)
    {
        string normalized = name.ToLowerInvariant();
        return ContainsAny(normalized, "write", "edit", "create", "file", "shell", "bash", "computer", "replace", "patch", "execute", "command");
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

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
        private readonly bool _streamToolCalls;
        private bool _inToolCall;
        private bool _toolCallStarted;

        public StreamParser(bool streamToolCalls = false)
        {
            _streamToolCalls = streamToolCalls;
        }

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
                    if (_streamToolCalls && !_toolCallStarted && TryParseToolHeader(buffered, out string id, out string name, out int headerLength))
                    {
                        _buffer.Remove(0, headerLength);
                        _toolCallStarted = true;
                        blocks.Add(new TraeToolUseStartBlock(id, name));
                        continue;
                    }

                    if (_streamToolCalls && _toolCallStarted)
                    {
                        if (closeIndex < 0)
                        {
                            if (final)
                            {
                                if (_buffer.Length > 0) blocks.Add(new TraeToolInputDeltaBlock(_buffer.ToString()));
                                _buffer.Clear();
                                blocks.Add(new TraeToolUseEndBlock());
                                ResetToolCall();
                                break;
                            }

                            int toolBoundaryLength = PartialCloseTagLength(buffered) + 1;
                            int deltaLength = buffered.Length - toolBoundaryLength;
                            if (deltaLength > 0)
                            {
                                blocks.Add(new TraeToolInputDeltaBlock(buffered[..deltaLength]));
                                _buffer.Remove(0, deltaLength);
                            }
                            break;
                        }

                        string remainder = buffered[..closeIndex];
                        int wrapperCloseIndex = LastNonWhitespaceIndex(remainder);
                        if (wrapperCloseIndex >= 0 && remainder[wrapperCloseIndex] == '}')
                            remainder = remainder.Remove(wrapperCloseIndex, 1);
                        if (remainder.Length > 0) blocks.Add(new TraeToolInputDeltaBlock(remainder));
                        _buffer.Remove(0, closeIndex + CloseTag.Length);
                        blocks.Add(new TraeToolUseEndBlock());
                        ResetToolCall();
                        continue;
                    }

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
                    ResetToolCall();
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

        private void ResetToolCall()
        {
            _inToolCall = false;
            _toolCallStarted = false;
        }

        private static bool TryParseToolHeader(string value, out string id, out string name, out int headerLength)
        {
            id = $"toolu_{Guid.NewGuid():N}";
            name = "";
            headerLength = 0;

            int cursor = SkipWhitespace(value, 0);
            if (cursor >= value.Length || value[cursor++] != '{') return false;
            cursor = SkipWhitespace(value, cursor);
            if (!TryReadJsonString(value, ref cursor, out string firstProperty) || firstProperty != "name") return false;
            cursor = SkipWhitespace(value, cursor);
            if (cursor >= value.Length || value[cursor++] != ':') return false;
            cursor = SkipWhitespace(value, cursor);
            if (!TryReadJsonString(value, ref cursor, out name) || string.IsNullOrWhiteSpace(name)) return false;
            cursor = SkipWhitespace(value, cursor);
            if (cursor >= value.Length || value[cursor++] != ',') return false;
            cursor = SkipWhitespace(value, cursor);
            if (!TryReadJsonString(value, ref cursor, out string argumentsProperty) || argumentsProperty != "arguments") return false;
            cursor = SkipWhitespace(value, cursor);
            if (cursor >= value.Length || value[cursor++] != ':') return false;
            headerLength = SkipWhitespace(value, cursor);
            return headerLength < value.Length;
        }

        private static bool TryReadJsonString(string value, ref int cursor, out string result)
        {
            result = "";
            if (cursor >= value.Length || value[cursor] != '"') return false;
            int start = cursor++;
            bool escaped = false;
            while (cursor < value.Length)
            {
                char current = value[cursor++];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (current != '"') continue;
                result = JsonNode.Parse(value[start..cursor])?.GetValue<string>() ?? "";
                return true;
            }
            return false;
        }

        private static int SkipWhitespace(string value, int cursor)
        {
            while (cursor < value.Length && char.IsWhiteSpace(value[cursor])) cursor++;
            return cursor;
        }

        private static int LastNonWhitespaceIndex(string value)
        {
            for (int index = value.Length - 1; index >= 0; index--)
                if (!char.IsWhiteSpace(value[index])) return index;
            return -1;
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

        private static int PartialCloseTagLength(string value)
        {
            int maximum = Math.Min(value.Length, CloseTag.Length - 1);
            for (int length = maximum; length > 0; length--)
            {
                if (CloseTag.StartsWith(value[^length..], StringComparison.Ordinal)) return length;
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