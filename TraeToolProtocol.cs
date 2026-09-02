using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JsonRepairSharp;

namespace TrancnProxy;

public abstract record TraeOutputBlock;

public sealed record TraeTextBlock(string Text) : TraeOutputBlock;

public sealed record TraeToolUseBlock(string Id, string Name, JsonObject Input) : TraeOutputBlock;

public sealed record TraeToolUseStartBlock(string Id, string Name) : TraeOutputBlock;

public sealed record TraeToolInputDeltaBlock(string PartialJson) : TraeOutputBlock;

public sealed record TraeToolUseEndBlock : TraeOutputBlock;

// 解析失败的工具调用绝不能当正文回给客户端，只能作为独立块交由上层记录并重试。
public sealed record TraeToolCallFailureBlock(string ToolName, string Reason, string RawPayload) : TraeOutputBlock;

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
        "<(?<tag>｜DSML｜|antml:invoke|invoke|function_calls|function_call|function|tool_call)\\s+name\\s*=\\s*[\"'](?<name>[^\"']+)[\"'][^>]*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex XmlToolParameter = new(
        "<parameter\\s+name\\s*=\\s*[\"'](?<key>[^\"']+)[\"'][^>]*>(?<value>.*?)</parameter>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex XmlToolWrapper = new(
        "</?(?:antml:)?(?:tool_calls|function_calls)\\s*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // DeepSeek 会把原生 ｜DSML｜ 控制符漏进闭合标签，需容忍后再定位。
    private static readonly Regex ToolCallClose = new(
        "</\\s*(?:｜DSML｜\\s*)?tool_call\\s*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // 控制符从不是合法参数内容，解析前先整体清掉。
    private static readonly Regex DsmlNoise = new(
        "</?\\s*｜DSML｜\\s*[A-Za-z_]*\\s*>|｜DSML｜",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TrailingCloseTag = new(
        "(?:\\s*</[^>]*>)+\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NumericParameter = new(
        @"^-?(?:0|[1-9]\d*)(?:\.\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MalformedObjectSeparator = new(
        "(?<=[\"}])\\]\\[?\\s*(?=\"(?:name|arguments|input|parameters)\"\\s*:)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ToolNameProperty = new(
        "\"name\"\\s*:\\s*\"(?<name>[^\"]{1,120})\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // 部分模型把工具名裸写在载荷开头，后面直接跟 JSON 或 XML 属性。
    private static readonly Regex BareNameToolCall = new(
        @"^\s*(?<name>[A-Za-z_][\w.\-]{0,63})\s*(?<rest>[\s\S]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex XmlAttribute = new(
        "(?<key>[A-Za-z_][\\w.\\-]*)\\s*=\\s*\"(?<value>[^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // 模型会把 Form A 的 JSON 和 Form B 的 <parameter> 混着写，后续参数于是被吞进上一个字符串值里。
    private static readonly Regex EmbeddedParameterBoundary = new(
        "</?parameter\\s+name\\s*=\\s*[\"'](?<key>[^\"']+)[\"'][^>]*>",
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
    Any reasoning you show must stay short. If you reason in the open, put it in exactly one <thinking>...</thinking> block before anything else, and never place user-visible prose, Markdown, code, or tool calls inside it. Do not draft the full solution inside that block; it is for a brief plan only. Never end your turn inside that block or immediately after it: once you close </thinking> you MUST still deliver the user-visible answer, and you MUST still emit any tool call the task requires.
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
Reasoning is for a short plan only. Never draft the full file, code, or answer inside your reasoning; produce it once, in the tool call itself. A turn that ends after reasoning without a tool call or a visible answer is a failed turn.
You have access to the tools described below. When a tool is needed, emit a tool call using one of the two forms below and do not describe the call as prose.
Form A (preferred for short values) - one JSON object whose keys MUST be in this order, name first, arguments second:
<tool_call>{"name":"TOOL_NAME","arguments":{"PARAM_NAME":"PARAM_VALUE"}}</tool_call>
Form B (use whenever a value is long or spans multiple lines, such as file contents or code) - write the value literally between the tags, so you never need to escape quotes or newlines:
<tool_call name="TOOL_NAME"><parameter name="PARAM_NAME">PARAM_VALUE</parameter></tool_call>
TOOL_NAME, PARAM_NAME and PARAM_VALUE above are placeholders. Always replace all three with the real tool name, the real parameter name, and the real value. Emitting the placeholder text itself is a failure.
Never mix the two forms in a single call, and never wrap a Form B value in JSON quoting.
Every property listed in the tool's input_schema.required array MUST be present and non-empty. Never emit an empty arguments object when required properties exist.
You may emit multiple tool_call blocks when calls can run in parallel. Tool definitions:
""" + "\n" + tools.ToJsonString());
            sections.Add("Use Markdown for all user-visible prose. Put any code shown to the user in fenced code blocks with a language identifier. Tool-call JSON is not user-visible prose and must not be wrapped in a Markdown fence.");
        }

        return string.Join("\n\n", sections);
    }

    // 上游 JSON 常缺括号、多逗号或用单引号；标准解析失败后交给 jsonrepair，避免自行拼修复规则。
    private static JsonObject? TryParseJsonObject(string text)
    {
        string normalized = MalformedObjectSeparator.Replace(text, ",");
        try
        {
            if (JsonNode.Parse(normalized) is JsonObject direct) return direct;
        }
        catch (System.Text.Json.JsonException) { }

        // 载荷在字符串中间断掉时，jsonrepair 会自己补上引号，结果是一个看似合法的半截文件，比直接失败更危险。
        if (!EndsInsideString(normalized))
        {
            try
            {
                if (JsonNode.Parse(JsonRepair.RepairJson(normalized)) is JsonObject repaired) return repaired;
            }
            catch { }
        }

        return SalvageJsonObject(normalized);
    }

    private static bool EndsInsideString(string text)
    {
        bool inString = false, escaped = false;
        foreach (char current in text)
        {
            if (inString)
            {
                if (escaped) escaped = false;
                else if (current == '\\') escaped = true;
                else if (current == '"') inString = false;
                continue;
            }
            if (current == '"') inString = true;
        }
        return inString;
    }

    // jsonrepair 也救不回来时（实测：值后多出一个 ')'），逐个抢救顶层键值对。
    // 只有干净走到 '}' 才算可信；中途读不出键说明字符串边界认错了（模型漏转义），宁可整体作废去重试，
    // 也不能把半截文件当成合法参数交出去。
    private static JsonObject? SalvageJsonObject(string text)
    {
        int cursor = text.IndexOf('{');
        if (cursor < 0) return null;
        cursor++;

        var salvaged = new JsonObject();
        while (cursor < text.Length)
        {
            cursor = SkipWhitespace(text, cursor);
            if (cursor >= text.Length) return null;
            if (text[cursor] == '}') return salvaged.Count > 0 ? salvaged : null;
            if (text[cursor] == ',') { cursor++; continue; }
            if (!TryReadJsonString(text, ref cursor, out string key)) return null;

            cursor = SkipWhitespace(text, cursor);
            if (cursor >= text.Length || text[cursor] != ':') return null;
            cursor = SkipWhitespace(text, cursor + 1);

            int valueEnd = JsonValueEnd(text, cursor);
            if (valueEnd < 0) return null;
            try { salvaged[key] = JsonNode.Parse(text[cursor..valueEnd]); }
            catch (System.Text.Json.JsonException) { return null; }

            cursor = valueEnd;
            while (cursor < text.Length && text[cursor] != ',' && text[cursor] != '}') cursor++;
        }
        return null;
    }

    private static int JsonValueEnd(string text, int start)
    {
        if (start >= text.Length) return -1;
        char opening = text[start];
        if (opening == '{' || opening == '[') return BracketedValueEnd(text, start);
        if (opening == '"')
        {
            int cursor = start;
            return TryReadJsonString(text, ref cursor, out _) ? cursor : -1;
        }

        int end = start;
        while (end < text.Length && text[end] != ',' && text[end] != '}' && text[end] != ']' && !char.IsWhiteSpace(text[end])) end++;
        return end > start ? end : -1;
    }

    private static int BracketedValueEnd(string text, int start)
    {
        int depth = 0;
        bool inString = false, escaped = false;
        for (int index = start; index < text.Length; index++)
        {
            char current = text[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (current == '\\') escaped = true;
                else if (current == '"') inString = false;
                continue;
            }
            if (current == '"') { inString = true; continue; }
            if (current is '{' or '[') depth++;
            else if (current is '}' or ']' && --depth == 0) return index + 1;
        }
        return -1;
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
            try { result = JsonNode.Parse(value[start..cursor])?.GetValue<string>() ?? ""; }
            catch (System.Text.Json.JsonException) { return false; }
            return true;
        }
        return false;
    }

    private static int SkipWhitespace(string value, int cursor)
    {
        while (cursor < value.Length && char.IsWhiteSpace(value[cursor])) cursor++;
        return cursor;
    }

    public static bool TryParseArguments(string serialized, out JsonObject arguments)
    {
        arguments = new JsonObject();
        if (string.IsNullOrWhiteSpace(serialized)) return true;
        if (TryParseJsonObject(serialized) is not { } parsed) return false;
        arguments = SplitEmbeddedParameters(parsed);
        return true;
    }

    private static JsonObject SplitEmbeddedParameters(JsonObject arguments)
    {
        foreach (var property in arguments.ToArray())
        {
            if (property.Value is not JsonValue value ||
                !value.TryGetValue(out string? text) ||
                text is null) continue;

            MatchCollection boundaries = EmbeddedParameterBoundary.Matches(text);
            if (boundaries.Count == 0) continue;

            arguments[property.Key] = JsonValue.Create(text[..boundaries[0].Index]);
            for (int index = 0; index < boundaries.Count; index++)
            {
                string key = boundaries[index].Groups["key"].Value;
                if (arguments[key] is JsonValue existing &&
                    existing.TryGetValue(out string? current) &&
                    !string.IsNullOrEmpty(current)) continue;

                int start = boundaries[index].Index + boundaries[index].Length;
                int end = index + 1 < boundaries.Count ? boundaries[index + 1].Index : text.Length;
                arguments[key] = JsonValue.Create(TrailingCloseTag.Replace(text[start..end], ""));
            }
        }
        return arguments;
    }

    private static string ToolNameHint(string payload)
    {
        Match match = ToolNameProperty.Match(payload);
        return match.Success ? match.Groups["name"].Value : "unknown";
    }

    // 提示词里的占位符会被模型原样抄进参数（实测写出过内容为 "raw value" 的文件），必须当成无效调用拦下。
    private static readonly string[] TemplatePlaceholders =
        ["TOOL_NAME", "PARAM_NAME", "PARAM_VALUE", "raw value", "tool_name", "parameter"];

    public static bool TryValidateToolUse(TraeToolUseBlock toolUse, JsonArray? tools, out string error)
    {
        error = "";
        string[] placeholders = toolUse.Input
            .Where(property => property.Value is JsonValue value &&
                              value.TryGetValue(out string? text) &&
                              TemplatePlaceholders.Contains(text?.Trim(), StringComparer.Ordinal))
            .Select(property => property.Key)
            .ToArray();
        if (placeholders.Length > 0)
        {
            error = $"arguments still contain template placeholders: {string.Join(", ", placeholders)}";
            return false;
        }
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
        private int _argumentsDepth;
        private bool _argumentsInString;
        private bool _argumentsEscaped;
        private bool _argumentsComplete;

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
                    int closeIndex = FindCloseTag(buffered, out int closeLength);
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
                                EmitToolArguments(blocks, _buffer.ToString());
                                _buffer.Clear();
                                blocks.Add(new TraeToolUseEndBlock());
                                ResetToolCall();
                                break;
                            }

                            int deltaLength = _bareToolCall
                                ? Math.Max(0, LastNonWhitespaceIndex(buffered))
                                : buffered.Length - (PartialCloseTagLength(buffered) + 1);
                            if (deltaLength > 0)
                            {
                                EmitToolArguments(blocks, buffered[..deltaLength]);
                                _buffer.Remove(0, deltaLength);
                            }
                            break;
                        }

                        EmitToolArguments(blocks, buffered[..closeIndex]);
                        _buffer.Remove(0, closeIndex + closeLength);
                        blocks.Add(new TraeToolUseEndBlock());
                        ResetToolCall();
                        continue;
                    }

                    if (TryTakeToolRegion(buffered, out string regionPayload, out int regionLength))
                    {
                        _buffer.Remove(0, regionLength);
                        if (ParseToolUse(regionPayload) is { } toolUse) blocks.Add(toolUse);
                        else blocks.Add(new TraeToolCallFailureBlock(ToolNameHint(regionPayload), "tool call payload is not valid JSON", regionPayload));
                        ResetToolCall();
                        continue;
                    }

                    if (final)
                    {
                        // 闭合标签可能整个丢失，仍应尽量把缓冲区当成工具调用解析。
                        if (ParseToolUse(buffered) is { } unterminated) blocks.Add(unterminated);
                        else blocks.Add(new TraeToolCallFailureBlock(ToolNameHint(buffered), "tool call was truncated before it could be parsed", buffered));
                        _buffer.Clear();
                        _inToolCall = false;
                    }
                    break;
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
            _argumentsDepth = 0;
            _argumentsInString = false;
            _argumentsEscaped = false;
            _argumentsComplete = false;
        }

        // 模型经常漏写外层对象的收尾括号，只能按 JSON 深度判定 arguments 真正结束的位置。
        private void EmitToolArguments(List<TraeOutputBlock> blocks, string text)
        {
            if (_argumentsComplete || text.Length == 0) return;
            int end = ScanArgumentDepth(text);
            string payload = end >= 0 ? text[..end] : text;
            if (payload.Length > 0) blocks.Add(new TraeToolInputDeltaBlock(payload));
        }

        private int ScanArgumentDepth(string text)
        {
            for (int index = 0; index < text.Length; index++)
            {
                char current = text[index];
                if (_argumentsInString)
                {
                    if (_argumentsEscaped) _argumentsEscaped = false;
                    else if (current == '\\') _argumentsEscaped = true;
                    else if (current == '"') _argumentsInString = false;
                    continue;
                }
                if (current == '"') { _argumentsInString = true; continue; }
                if (current is '{' or '[') _argumentsDepth++;
                else if (current is '}' or ']')
                {
                    _argumentsDepth--;
                    if (_argumentsDepth <= 0)
                    {
                        _argumentsComplete = true;
                        return index + 1;
                    }
                }
            }
            return -1;
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
            if (TryParseJsonObject(trimmed) is not { } parsed) return null;

            JsonNode? arguments = parsed["arguments"] ?? parsed["input"] ?? parsed["parameters"];
            if (arguments is JsonValue value && value.TryGetValue<string>(out string? serialized))
                arguments = TryParseJsonObject(serialized ?? "{}");
            if (arguments is JsonObject argumentObject) return (JsonObject)argumentObject.DeepClone();
            return parsed["name"] is not null ? new JsonObject() : parsed;
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

        private static int FindCloseTag(string value, out int length)
        {
            Match match = ToolCallClose.Match(value);
            length = match.Success ? match.Length : 0;
            return match.Success ? match.Index : -1;
        }

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
            // 模型有时把同一个调用重复输出多份，只取第一个完整对象。
            string cleaned = DsmlNoise.Replace(payload, " ");
            if (ParseToolObject(FirstJsonObject(cleaned)) is { } tool && BuildToolUse(tool) is { } fromFirst)
                return fromFirst;
            if (ParseToolObject(TrailingCloseTag.Replace(cleaned, "").TrimEnd()) is { } repaired &&
                BuildToolUse(repaired) is { } fromRepair)
                return fromRepair;
            return ParseBareNameToolUse(cleaned);
        }

        private static TraeToolUseBlock? ParseBareNameToolUse(string payload)
        {
            string rest = TrailingCloseTag.Replace(payload, "").Trim();
            Match match = BareNameToolCall.Match(rest);
            if (!match.Success) return null;

            string name = match.Groups["name"].Value;
            rest = match.Groups["rest"].Value.Trim();
            if (rest.EndsWith("/>", StringComparison.Ordinal)) rest = rest[..^2].TrimEnd();

            if (rest.StartsWith('{'))
                return TryParseJsonObject(rest) is { } arguments
                    ? new TraeToolUseBlock($"toolu_{Guid.NewGuid():N}", name, arguments)
                    : null;

            var attributes = new JsonObject();
            foreach (Match attribute in XmlAttribute.Matches(rest))
                attributes[attribute.Groups["key"].Value] = JsonValue.Create(attribute.Groups["value"].Value);
            return attributes.Count > 0
                ? new TraeToolUseBlock($"toolu_{Guid.NewGuid():N}", name, attributes)
                : null;
        }

        private static TraeToolUseBlock? BuildToolUse(JsonObject tool)
        {
            string? name = (string?)tool["name"];
            if (string.IsNullOrWhiteSpace(name)) return null;
            string id = (string?)tool["id"] ?? $"toolu_{Guid.NewGuid():N}";
            JsonNode? arguments = tool["arguments"] ?? tool["input"] ?? tool["parameters"];
            if (arguments is JsonValue value && value.TryGetValue<string>(out string? serialized))
                arguments = TryParseJsonObject(serialized ?? "{}");
            // 部分模型把参数与 name 平铺在同一层，而不是包进 arguments。
            if (arguments is not JsonObject) arguments = FlattenedArguments(tool);
            return new TraeToolUseBlock(id, name,
                arguments is JsonObject argumentObject ? SplitEmbeddedParameters(argumentObject) : new JsonObject());
        }

        private static JsonObject? ParseToolObject(string? candidate) =>
            string.IsNullOrWhiteSpace(candidate) ? null : TryParseJsonObject(candidate);

        private static string? FirstJsonObject(string text)
        {
            int start = text.IndexOf('{');
            if (start < 0) return null;
            int end = JsonObjectEnd(text, start);
            return end < 0 ? null : text[start..end];
        }

        private static int JsonObjectEnd(string text, int start)
        {
            int depth = 0;
            bool inString = false, escaped = false;
            for (int index = start; index < text.Length; index++)
            {
                char current = text[index];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (current == '\\') escaped = true;
                    else if (current == '"') inString = false;
                    continue;
                }
                if (current == '"') { inString = true; continue; }
                if (current == '{') depth++;
                else if (current == '}' && --depth == 0) return index + 1;
            }
            return -1;
        }

        // 模型的闭合标签名不可靠（见 </tool_request>、</｜DSML｜>），改按 JSON 对象边界切分整个调用区域。
        private static bool TryTakeToolRegion(string buffered, out string payload, out int consumed)
        {
            payload = "";
            consumed = 0;
            int cursor = 0;
            bool sawObject = false;
            while (cursor < buffered.Length)
            {
                cursor = SkipWhitespace(buffered, cursor);
                if (cursor >= buffered.Length) break;
                if (buffered[cursor] == '{')
                {
                    int end = JsonObjectEnd(buffered, cursor);
                    if (end < 0)
                    {
                        // 对象未闭合：若后面已有闭合标签，就把标签前的内容交给括号补全逻辑。
                        int tag = buffered.IndexOf("</", cursor, StringComparison.Ordinal);
                        if (tag < 0) return false;
                        if (!sawObject)
                        {
                            payload = buffered[cursor..tag];
                            sawObject = true;
                        }
                        cursor = tag;
                        continue;
                    }
                    if (!sawObject)
                    {
                        payload = buffered[cursor..end];
                        sawObject = true;
                    }
                    cursor = end;
                    continue;
                }
                if (buffered[cursor] == '<' && cursor + 1 < buffered.Length && buffered[cursor + 1] == '/')
                {
                    int close = buffered.IndexOf('>', cursor);
                    if (close < 0) return false;
                    cursor = close + 1;
                    consumed = cursor;
                    continue;
                }
                break;
            }
            return sawObject && consumed > 0;
        }

        private static JsonObject FlattenedArguments(JsonObject tool)
        {
            var arguments = new JsonObject();
            foreach (var property in tool)
            {
                if (property.Key is "name" or "id" or "arguments" or "input" or "parameters") continue;
                arguments[property.Key] = property.Value?.DeepClone();
            }
            return arguments;
        }
    }
}