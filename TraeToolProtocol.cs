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

public sealed record TraeThinkingStartBlock : TraeOutputBlock;

public sealed record TraeThinkingDeltaBlock(string Text) : TraeOutputBlock;

public sealed record TraeThinkingEndBlock : TraeOutputBlock;

public static class TraeToolProtocol
{
    private const string OpenTag = "<tool_call>";
    private const string CloseTag = "</tool_call>";
    private static readonly (string Open, string Close)[] ThinkingTags =
    {
        ("<thinking>", "</thinking>"),
        ("<think>", "</think>")
    };

    // 模型常以自身原生 XML 语法发起工具调用（DeepSeek 用全角 ｜DSML｜，Qwen 用带 name 属性的 tool_call），需与 <tool_call> JSON 一并识别。
    private static readonly Regex XmlToolOpen = new(
        "<(?<tag>｜DSML｜|antml:invoke|invoke|function_call|tool_call)\\s+name\\s*=\\s*[\"'](?<name>[^\"']+)[\"']\\s*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex XmlToolParameter = new(
        "<parameter\\s+name\\s*=\\s*[\"'](?<key>[^\"']+)[\"']\\s*>(?<value>.*?)</parameter>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex XmlToolWrapper = new(
        "</?(?:antml:)?(?:tool_calls|function_calls)\\s*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NumericParameter = new(
        @"^-?(?:0|[1-9]\d*)(?:\.\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MalformedObjectSeparator = new(
        "(?<=[\"}])\\]\\[?\\s*(?=\"(?:name|arguments|input|parameters)\"\\s*:)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] PartialGuards =
    {
        OpenTag, "<thinking>", "<think>",
        "<｜DSML｜", "<invoke", "<invoke", "<function_call",
        "<tool_call",
        "<tool_calls>", "<function_calls>", "<function_calls>",
        "</tool_calls>", "</function_calls>", "</function_calls>"
    };
    private static readonly Regex EnglishAction = new(
        @"\b(create|write|build|implement|modify|edit|fix|delete|rename|move|run|execute|install|test|inspect|search|read|open)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EnglishTarget = new(
        @"\b(file|code|project|workspace|app|application|page|website|script|game|component|endpoint|command)\b|\.[a-z0-9]{1,10}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string BuildSystemPrompt(
        JsonNode? system,
        JsonArray? tools,
        JsonNode? toolChoice = null,
        bool thinkingEnabled = false)
    {
        var sections = new List<string>();
        string systemText = ContentText(system);
        if (!string.IsNullOrWhiteSpace(systemText)) sections.Add(systemText);

        if (thinkingEnabled)
        {
            sections.Add("""
    You MUST begin every response with exactly one non-empty <thinking>...</thinking> block containing a brief analysis summary, even for simple requests. The <thinking> opening tag MUST be the first output token. Only after the closing </thinking> tag may you emit the user-visible answer or a tool call. Do not place user-visible prose, Markdown, code, or tool calls inside that block.
""");
        }

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
You have access to the tools described below. When a tool is needed, emit a tool call using one of the two forms below and do not describe the call as prose.
Form A (preferred for short values) — one JSON object whose keys MUST be in this order, name first, arguments second:
<tool_call>{"name":"tool_name","arguments":{"parameter":"value"}}</tool_call>
Form B (use whenever a value is long or spans multiple lines, such as file contents or code) — raw values, so you never need to escape quotes or newlines:
<tool_call name="tool_name"><parameter name="parameter">raw value</parameter></tool_call>
Never mix the two forms in a single call, and never wrap a Form B value in JSON quoting.
Every property listed in the tool's input_schema.required array MUST be present and non-empty. Never emit an empty arguments object when required properties exist.
You may emit multiple tool_call blocks when calls can run in parallel. Tool definitions:
""" + "\n" + tools.ToJsonString());
            sections.Add("Use Markdown for all user-visible prose. Put any code shown to the user in fenced code blocks with a language identifier. Tool-call JSON is not user-visible prose and must not be wrapped in a Markdown fence.");
        }

        return string.Join("\n\n", sections);
    }

    public static bool TryValidateToolUse(TraeToolUseBlock toolUse, JsonArray? tools, out string error)
    {
        error = "";
        if (tools is not { Count: > 0 }) return true;

        JsonObject? definition = tools
            .OfType<JsonObject>()
            .FirstOrDefault(tool => string.Equals((string?)tool["name"], toolUse.Name, StringComparison.Ordinal));
        if (definition is null)
        {
            error = $"unknown tool: {toolUse.Name}";
            return false;
        }

        if (definition["input_schema"]?["required"] is not JsonArray required) return true;
        string[] missing = required
            .Select(node => (string?)node)
            .Where(name => !string.IsNullOrWhiteSpace(name) && IsMissing(toolUse.Input[name!]))
            .Select(name => name!)
            .ToArray();
        if (missing.Length == 0) return true;

        error = $"missing required properties: {string.Join(", ", missing)}";
        return false;
    }

    private static bool IsMissing(JsonNode? value) => value switch
    {
        null => true,
        JsonValue jsonValue when jsonValue.TryGetValue<string>(out string? text) => string.IsNullOrWhiteSpace(text),
        _ => false
    };

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
        return (chineseAction || EnglishAction.IsMatch(text)) && (chineseTarget || EnglishTarget.IsMatch(text));
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
        private bool _bareToolCall;
        private bool _inThinking;
        private string _thinkingCloseTag = "";
        private bool _inXmlTool;
        private string _xmlToolCloseTag = "";

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
                if (_inThinking)
                {
                    int thinkingCloseIndex = buffered.IndexOf(_thinkingCloseTag, StringComparison.Ordinal);
                    if (thinkingCloseIndex < 0)
                    {
                        if (final)
                        {
                            if (buffered.Length > 0) blocks.Add(new TraeThinkingDeltaBlock(buffered));
                            _buffer.Clear();
                            blocks.Add(new TraeThinkingEndBlock());
                            _inThinking = false;
                            break;
                        }

                        int thinkingBoundaryLength = PartialTagLength(buffered, _thinkingCloseTag);
                        int thinkingDeltaLength = buffered.Length - thinkingBoundaryLength;
                        if (thinkingDeltaLength > 0)
                        {
                            blocks.Add(new TraeThinkingDeltaBlock(buffered[..thinkingDeltaLength]));
                            _buffer.Remove(0, thinkingDeltaLength);
                        }
                        break;
                    }

                    if (thinkingCloseIndex > 0) blocks.Add(new TraeThinkingDeltaBlock(buffered[..thinkingCloseIndex]));
                    _buffer.Remove(0, thinkingCloseIndex + _thinkingCloseTag.Length);
                    blocks.Add(new TraeThinkingEndBlock());
                    _inThinking = false;
                    continue;
                }

                if (_inXmlTool)
                {
                    int xmlCloseIndex = buffered.IndexOf(_xmlToolCloseTag, StringComparison.Ordinal);
                    if (xmlCloseIndex < 0)
                    {
                        if (!final) break;
                        EmitXmlToolInput(blocks, buffered);
                        _buffer.Clear();
                        _inXmlTool = false;
                        break;
                    }

                    EmitXmlToolInput(blocks, buffered[..xmlCloseIndex]);
                    _buffer.Remove(0, xmlCloseIndex + _xmlToolCloseTag.Length);
                    _inXmlTool = false;
                    continue;
                }

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
                                string finalRemainder = _buffer.ToString();
                                if (_bareToolCall)
                                {
                                    int finalWrapperCloseIndex = LastNonWhitespaceIndex(finalRemainder);
                                    if (finalWrapperCloseIndex >= 0 && finalRemainder[finalWrapperCloseIndex] == '}')
                                        finalRemainder = finalRemainder.Remove(finalWrapperCloseIndex, 1);
                                }
                                if (finalRemainder.Length > 0) blocks.Add(new TraeToolInputDeltaBlock(finalRemainder));
                                _buffer.Clear();
                                blocks.Add(new TraeToolUseEndBlock());
                                ResetToolCall();
                                break;
                            }

                            int deltaLength;
                            if (_bareToolCall)
                            {
                                int finalJsonCharacter = LastNonWhitespaceIndex(buffered);
                                deltaLength = Math.Max(0, finalJsonCharacter);
                            }
                            else
                            {
                                int toolBoundaryLength = PartialCloseTagLength(buffered) + 1;
                                deltaLength = buffered.Length - toolBoundaryLength;
                            }
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

                if (_streamToolCalls &&
                    TryParseToolHeader(buffered, out string bareId, out string bareName, out int bareHeaderLength))
                {
                    _buffer.Remove(0, bareHeaderLength);
                    _inToolCall = true;
                    _toolCallStarted = true;
                    _bareToolCall = true;
                    blocks.Add(new TraeToolUseStartBlock(bareId, bareName));
                    continue;
                }
                if (_streamToolCalls && !final && IsPotentialBareToolHeader(buffered)) break;

                int openIndex = buffered.IndexOf(OpenTag, StringComparison.Ordinal);
                int thinkingIndex = -1;
                string thinkingOpenTag = "";
                string thinkingCloseTag = "";
                foreach (var tag in ThinkingTags)
                {
                    int candidate = buffered.IndexOf(tag.Open, StringComparison.Ordinal);
                    if (candidate < 0 || (thinkingIndex >= 0 && candidate >= thinkingIndex)) continue;
                    thinkingIndex = candidate;
                    thinkingOpenTag = tag.Open;
                    thinkingCloseTag = tag.Close;
                }

                if (thinkingIndex >= 0 && (openIndex < 0 || thinkingIndex < openIndex))
                {
                    if (thinkingIndex > 0) blocks.Add(new TraeTextBlock(buffered[..thinkingIndex]));
                    _buffer.Remove(0, thinkingIndex + thinkingOpenTag.Length);
                    _inThinking = true;
                    _thinkingCloseTag = thinkingCloseTag;
                    blocks.Add(new TraeThinkingStartBlock());
                    continue;
                }

                if (openIndex >= 0)
                {
                    if (openIndex > 0) blocks.Add(new TraeTextBlock(buffered[..openIndex]));
                    _buffer.Remove(0, openIndex + OpenTag.Length);
                    _inToolCall = true;
                    continue;
                }

                Match xmlTool = XmlToolOpen.Match(buffered);
                Match xmlWrapper = XmlToolWrapper.Match(buffered);
                bool wrapperFirst = xmlWrapper.Success && (!xmlTool.Success || xmlWrapper.Index < xmlTool.Index);
                if (xmlTool.Success && !wrapperFirst)
                {
                    if (xmlTool.Index > 0) blocks.Add(new TraeTextBlock(buffered[..xmlTool.Index]));
                    _buffer.Remove(0, xmlTool.Index + xmlTool.Length);
                    _inXmlTool = true;
                    _xmlToolCloseTag = $"</{xmlTool.Groups["tag"].Value}>";
                    blocks.Add(new TraeToolUseStartBlock($"toolu_{Guid.NewGuid():N}", xmlTool.Groups["name"].Value));
                    continue;
                }

                if (wrapperFirst)
                {
                    if (xmlWrapper.Index > 0) blocks.Add(new TraeTextBlock(buffered[..xmlWrapper.Index]));
                    _buffer.Remove(0, xmlWrapper.Index + xmlWrapper.Length);
                    continue;
                }

                int textLength = final ? buffered.Length : SafeTextLength(buffered);
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
            _bareToolCall = false;
        }

        private static bool IsPotentialBareToolHeader(string value)
        {
            int cursor = SkipWhitespace(value, 0);
            if (cursor >= value.Length || value[cursor++] != '{') return false;
            cursor = SkipWhitespace(value, cursor);
            if (cursor >= value.Length) return true;

            const string property = "\"name\"";
            string remainder = value[cursor..];
            return remainder.Length <= property.Length
                ? property.StartsWith(remainder, StringComparison.Ordinal)
                : remainder.StartsWith(property, StringComparison.Ordinal);
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

        // 属性标签长度不固定，只能以末尾未闭合的 '<' 作为切分点。
        private static int SafeTextLength(string value)
        {
            int lastOpen = value.LastIndexOf('<');
            if (lastOpen < 0) return value.Length;
            string tail = value[lastOpen..];
            if (tail.Contains('>')) return value.Length;
            foreach (string guard in PartialGuards)
            {
                if (guard.StartsWith(tail, StringComparison.Ordinal) || tail.StartsWith(guard, StringComparison.Ordinal))
                    return lastOpen;
            }
            return value.Length;
        }

        private static void EmitXmlToolInput(List<TraeOutputBlock> blocks, string body)
        {
            var input = new JsonObject();
            bool sawParameter = false;
            foreach (Match parameter in XmlToolParameter.Matches(body))
            {
                sawParameter = true;
                input[parameter.Groups["key"].Value] = ParameterValue(parameter.Groups["value"].Value);
            }
            // 部分模型在属性式标签内直接放 JSON 参数，而不是 <parameter> 子元素。
            if (!sawParameter && TryParseArgumentObject(body) is { } jsonBody) input = jsonBody;
            blocks.Add(new TraeToolInputDeltaBlock(input.ToJsonString()));
            blocks.Add(new TraeToolUseEndBlock());
        }

        private static JsonObject? TryParseArgumentObject(string body)
        {
            string trimmed = body.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{') return null;
            try
            {
                if (JsonNode.Parse(MalformedObjectSeparator.Replace(trimmed, ",")) is not JsonObject parsed) return null;
                JsonNode? arguments = parsed["arguments"] ?? parsed["input"] ?? parsed["parameters"];
                if (arguments is JsonValue value && value.TryGetValue<string>(out string? serialized))
                    arguments = JsonNode.Parse(serialized ?? "{}");
                if (arguments is JsonObject argumentObject) return (JsonObject)argumentObject.DeepClone();
                return parsed["name"] is not null ? new JsonObject() : parsed;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        private static JsonNode? ParameterValue(string raw)
        {
            string trimmed = raw.Trim();
            if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
            {
                try { return JsonNode.Parse(trimmed); }
                catch (System.Text.Json.JsonException) { }
            }
            if (trimmed is "true" or "false") return JsonValue.Create(trimmed == "true");
            if (NumericParameter.IsMatch(trimmed)) return JsonNode.Parse(trimmed);
            return JsonValue.Create(raw);
        }

        private static int PartialCloseTagLength(string value) => PartialTagLength(value, CloseTag);

        private static int PartialTagLength(string value, string tag)
        {
            int maximum = Math.Min(value.Length, tag.Length - 1);
            for (int length = maximum; length > 0; length--)
            {
                if (tag.StartsWith(value[^length..], StringComparison.Ordinal)) return length;
            }
            return 0;
        }

        private static TraeToolUseBlock? ParseToolUse(string payload)
        {
            try
            {
                string normalizedPayload = MalformedObjectSeparator.Replace(payload, ",");
                if (JsonNode.Parse(normalizedPayload) is not JsonObject tool) return null;
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