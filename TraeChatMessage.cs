using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TrancnProxy;

public sealed record TraeChatMessage(string Role, IReadOnlyList<TraeChatContent> Content)
{
    public bool HasImages => Content.Any(part => part.Type == "image_url");

    public static TraeChatMessage Text(string role, string text) =>
        new(role, [TraeChatContent.TextPart(text)]);

    public JsonObject ToJson()
    {
        var content = new JsonArray();
        foreach (TraeChatContent part in Content)
            content.Add(part.ToJson());
        return new JsonObject { ["role"] = Role, ["content"] = content };
    }
}

public sealed record TraeChatContent(
    string Type,
    string? Text = null,
    string? ImageUrl = null)
{
    public static TraeChatContent TextPart(string text) => new("text", Text: text);

    public static TraeChatContent Image(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new TraeMultimodalInputException("图片地址不能为空。");
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            Match match = Regex.Match(url, "^data:(image/(?:png|jpeg|gif|webp));base64,([A-Za-z0-9+/=]+)$", RegexOptions.IgnoreCase);
            if (!match.Success)
                throw new TraeMultimodalInputException("图片 data URL 必须是 PNG、JPEG、GIF 或 WebP 的 base64 数据。");
            try
            {
                if (Convert.FromBase64String(match.Groups[2].Value).Length == 0)
                    throw new TraeMultimodalInputException("图片数据不能为空。");
            }
            catch (FormatException ex)
            {
                throw new TraeMultimodalInputException("图片 base64 数据无效。", ex);
            }
        }
        else if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new TraeMultimodalInputException("图片地址必须是 data URL 或 HTTP(S) URL。");
        }
        return new("image_url", ImageUrl: url);
    }

    public JsonObject ToJson() => Type switch
    {
        "text" => new JsonObject { ["type"] = "text", ["text"] = Text ?? "" },
        "image_url" => new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject { ["url"] = ImageUrl }
        },
        _ => throw new InvalidOperationException($"不支持的 Trae 内容类型: {Type}")
    };
}

public sealed class TraeMultimodalInputException(string message, Exception? innerException = null)
    : ArgumentException(message, innerException)
{
}