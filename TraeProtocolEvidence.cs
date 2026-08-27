using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TrancnProxy;

/// <summary>Removes sensitive values while retaining a useful upstream protocol schema.</summary>
public sealed class TraeProtocolEvidenceSanitizer
{
    private static readonly string[] SensitiveKeyFragments =
    [
        "token", "authorization", "cookie", "password", "secret", "credential",
        "content", "prompt", "text", "workspace", "path", "user", "uid",
        "device", "machine", "email", "phone"
    ];

    private static readonly string[] IdentifierKeyFragments = ["id", "session", "project", "task", "channel", "call"];
    private static readonly HashSet<string> SafeStringKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "event", "method", "service", "function", "model", "model_name", "config_name",
        "display_model_name", "host", "route", "type", "status", "finish_reason", "direction"
    };

    private readonly byte[] _salt;

    public TraeProtocolEvidenceSanitizer()
    {
        _salt = RandomNumberGenerator.GetBytes(32);
    }

    /// <summary>Produces a schema-preserving, secret-free projection of a protocol envelope.</summary>
    public JsonNode? Sanitize(JsonNode? node) => SanitizeNode(node, null);

    private JsonNode? SanitizeNode(JsonNode? node, string? propertyName)
    {
        if (node is null) return null;
        if (IsSensitive(propertyName)) return JsonValue.Create("[redacted]");

        if (node is JsonObject source)
        {
            var sanitized = new JsonObject();
            foreach (var property in source)
                sanitized[property.Key] = SanitizeNode(property.Value, property.Key);
            return sanitized;
        }

        if (node is JsonArray array)
        {
            var sanitized = new JsonArray();
            foreach (var item in array)
                sanitized.Add(SanitizeNode(item, propertyName));
            return sanitized;
        }

        if (node is not JsonValue value) return JsonValue.Create("[unsupported]");
        if (value.TryGetValue<string>(out var text))
        {
            if (IsIdentifier(propertyName)) return JsonValue.Create(Pseudonymize(text));
            return JsonValue.Create(IsSafeString(propertyName) ? text : "string");
        }
        if (value.TryGetValue<bool>(out var boolean)) return JsonValue.Create(boolean);
        if (value.TryGetValue<long>(out var integer)) return JsonValue.Create(integer);
        if (value.TryGetValue<double>(out var number)) return JsonValue.Create(number);
        return JsonValue.Create("[value]");
    }

    private static bool IsSensitive(string? propertyName) =>
        !string.IsNullOrWhiteSpace(propertyName) &&
        SensitiveKeyFragments.Any(fragment => propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool IsIdentifier(string? propertyName) =>
        !string.IsNullOrWhiteSpace(propertyName) &&
        IdentifierKeyFragments.Any(fragment => propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeString(string? propertyName) =>
        !string.IsNullOrWhiteSpace(propertyName) && SafeStringKeys.Contains(propertyName);

    private string Pseudonymize(string value)
    {
        byte[] input = Encoding.UTF8.GetBytes(value);
        byte[] joined = new byte[_salt.Length + input.Length];
        Buffer.BlockCopy(_salt, 0, joined, 0, _salt.Length);
        Buffer.BlockCopy(input, 0, joined, _salt.Length, input.Length);
        return "id_" + Convert.ToHexString(SHA256.HashData(joined))[..12].ToLowerInvariant();
    }
}

/// <summary>Appends sanitized development-only protocol evidence outside the workspace.</summary>
public sealed class TraeProtocolEvidenceWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly TraeProtocolEvidenceSanitizer _sanitizer = new();

    public TraeProtocolEvidenceWriter(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"trae-protocol-evidence-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.jsonl");
        _writer = new StreamWriter(path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    /// <summary>Writes one sanitized protocol envelope without retaining the raw value on disk.</summary>
    public void Write(JsonNode? envelope)
    {
        var entry = new JsonObject
        {
            ["observed_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["envelope"] = _sanitizer.Sanitize(envelope)
        };
        lock (_gate)
            _writer.WriteLine(entry.ToJsonString());
    }

    public void Dispose()
    {
        lock (_gate)
            _writer.Dispose();
    }
}