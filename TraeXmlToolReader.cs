using System.Text.Json.Nodes;

namespace TrancnProxy;

internal enum TraeXmlTagKind
{
    Open,
    Close,
    SelfClosing
}

internal readonly record struct TraeXmlTag(
    TraeXmlTagKind Kind,
    string Name,
    IReadOnlyDictionary<string, string> Attributes,
    int Start,
    int Length)
{
    public int End => Start + Length;

    public string? Attribute(string key) => Attributes.TryGetValue(key, out string? value) ? value : null;
}

/// <summary>
/// 宽松标签扫描器。模型输出不是合法 XML（未转义的 &lt; 与 &amp;、属性值里带引号），<see cref="System.Xml.XmlReader"/>
/// 会直接抛错；这里只做形状无关的识别，属性顺序、元素名、未知标签都不影响结果。
/// </summary>
internal static class TraeXmlToolReader
{
    private static readonly IReadOnlyDictionary<string, string> NoAttributes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // 容器标签：内部承载一次工具调用。
    private static readonly HashSet<string> ContainerTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "tool_call", "toolcall", "tool_use", "function_call", "function",
        "invoke", "antml:invoke", "｜DSML｜", "tool_request"
    };

    // 包装标签：只是把多个调用括起来，本身没有语义，必须整体丢弃而不是泄漏成正文。
    private static readonly HashSet<string> WrapperTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "tool_calls", "toolcalls", "function_calls", "antml:function_calls", "tool_uses"
    };

    // 参数元素用的通用占位名，本身不携带参数名，需要按工具 schema 回填。
    private static readonly HashSet<string> AnonymousParameterTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "parameter", "param", "arg", "arg_value", "argument", "value", "input"
    };

    public static bool IsContainer(string tagName) => ContainerTags.Contains(tagName);

    public static bool IsWrapper(string tagName) => WrapperTags.Contains(tagName);

    /// <summary>Reads a tag at <paramref name="start"/>, or null when the text is not a tag or is still incomplete.</summary>
    public static TraeXmlTag? TryReadTag(string text, int start)
    {
        if (start >= text.Length || text[start] != '<') return null;

        int cursor = start + 1;
        bool closing = cursor < text.Length && text[cursor] == '/';
        if (closing) cursor++;

        int nameStart = cursor;
        while (cursor < text.Length && IsNameChar(text[cursor])) cursor++;
        if (cursor == nameStart) return null;
        string name = text[nameStart..cursor];

        Dictionary<string, string>? attributes = null;
        while (cursor < text.Length)
        {
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
            if (cursor >= text.Length) return null;

            if (text[cursor] == '>')
                return Tag(closing ? TraeXmlTagKind.Close : TraeXmlTagKind.Open, name, attributes, start, cursor + 1 - start);
            if (text[cursor] == '/' && cursor + 1 < text.Length && text[cursor + 1] == '>')
                return Tag(TraeXmlTagKind.SelfClosing, name, attributes, start, cursor + 2 - start);

            int keyStart = cursor;
            while (cursor < text.Length && IsNameChar(text[cursor])) cursor++;
            if (cursor == keyStart) return null;
            string key = text[keyStart..cursor];

            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
            string value = "";
            if (cursor < text.Length && text[cursor] == '=')
            {
                cursor++;
                while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
                if (cursor >= text.Length) return null;

                char quote = text[cursor];
                if (quote is '"' or '\'')
                {
                    int valueStart = ++cursor;
                    while (cursor < text.Length && text[cursor] != quote) cursor++;
                    if (cursor >= text.Length) return null;
                    value = text[valueStart..cursor++];
                }
                else
                {
                    int valueStart = cursor;
                    while (cursor < text.Length && !char.IsWhiteSpace(text[cursor]) && text[cursor] != '>' && text[cursor] != '/') cursor++;
                    value = text[valueStart..cursor];
                }
            }

            (attributes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[key] = value;
        }
        return null;
    }

    /// <summary>Finds the next tag whose name passes <paramref name="accept"/>.</summary>
    public static bool TryFindTag(string text, int from, Func<TraeXmlTag, bool> accept, out TraeXmlTag tag)
    {
        for (int index = text.IndexOf('<', from); index >= 0; index = text.IndexOf('<', index + 1))
        {
            if (TryReadTag(text, index) is not { } candidate) continue;
            if (!accept(candidate)) continue;
            tag = candidate;
            return true;
        }
        tag = default;
        return false;
    }

    /// <summary>Extracts parameters from a container body, regardless of which element form the model used.</summary>
    public static JsonObject ReadParameters(string body, IReadOnlyList<string> schemaOrder)
    {
        var parameters = new JsonObject();
        int anonymousIndex = 0;
        int cursor = 0;

        while (cursor < body.Length)
        {
            int open = body.IndexOf('<', cursor);
            if (open < 0) break;
            if (TryReadTag(body, open) is not { Kind: TraeXmlTagKind.Open } element)
            {
                cursor = open + 1;
                continue;
            }

            int valueStart = element.End;
            int valueEnd = FindMatchingClose(body, element.Name, valueStart);
            if (valueEnd < 0) valueEnd = body.Length;

            string key = element.Attribute("name")
                ?? (AnonymousParameterTags.Contains(element.Name)
                    ? NextSchemaName(schemaOrder, parameters, ref anonymousIndex)
                    : element.Name);
            if (!string.IsNullOrEmpty(key)) parameters[key] = ParameterValue(body[valueStart..valueEnd]);

            cursor = valueEnd < body.Length ? SkipClose(body, element.Name, valueEnd) : body.Length;
        }
        return parameters;
    }

    /// <summary>Infers the tool name from the parameter set when the container did not carry one.</summary>
    public static string? InferToolName(JsonObject parameters, IReadOnlyDictionary<string, string[]> toolRequirements)
    {
        if (parameters.Count == 0) return null;
        var present = parameters.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);

        string? single = null;
        foreach (var (tool, required) in toolRequirements)
        {
            if (required.Length == 0 || !required.All(present.Contains)) continue;
            if (single is not null) return null;
            single = tool;
        }
        return single;
    }

    private static string NextSchemaName(IReadOnlyList<string> schemaOrder, JsonObject parameters, ref int index)
    {
        while (index < schemaOrder.Count)
        {
            string candidate = schemaOrder[index++];
            if (parameters[candidate] is null) return candidate;
        }
        return "";
    }

    private static int FindMatchingClose(string body, string name, int from)
    {
        int depth = 0;
        for (int index = body.IndexOf('<', from); index >= 0; index = body.IndexOf('<', index + 1))
        {
            if (TryReadTag(body, index) is not { } tag) continue;
            if (!string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (tag.Kind == TraeXmlTagKind.Open) { depth++; continue; }
            if (tag.Kind != TraeXmlTagKind.Close) continue;
            if (depth == 0) return index;
            depth--;
        }
        return -1;
    }

    private static int SkipClose(string body, string name, int closeStart) =>
        TryReadTag(body, closeStart) is { } close && string.Equals(close.Name, name, StringComparison.OrdinalIgnoreCase)
            ? close.End
            : closeStart + 1;

    private static JsonNode? ParameterValue(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length > 0 && trimmed[0] is '{' or '[')
        {
            try { return JsonNode.Parse(trimmed); }
            catch (System.Text.Json.JsonException) { }
        }
        if (trimmed is "true" or "false") return JsonValue.Create(trimmed == "true");
        if (trimmed.Length > 0 && long.TryParse(trimmed, out long integer)) return JsonValue.Create(integer);
        if (trimmed.Length > 0 && double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double number)) return JsonValue.Create(number);
        return JsonValue.Create(raw);
    }

    private static TraeXmlTag Tag(
        TraeXmlTagKind kind, string name, Dictionary<string, string>? attributes, int start, int length) =>
        new(kind, name, attributes ?? NoAttributes, start, length);

    private static bool IsNameChar(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '-' or '.' or ':' or '｜';
}
