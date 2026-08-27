using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace TrancnProxy.Agent;

/// <summary>Represents a server-controlled workspace descriptor for an upstream Agent session.</summary>
public sealed record TraeAgentWorkspace(string ProjectId, string WorkspaceId, Uri RootUri)
{
    /// <summary>Creates an empty, per-session virtual workspace without inspecting a local IDE directory.</summary>
    public static TraeAgentWorkspace CreateEphemeral()
    {
        string identifier = Guid.NewGuid().ToString("N");
        return new TraeAgentWorkspace($"project-{identifier}", $"workspace-{identifier}", new Uri($"file:///workspace/{identifier}/"));
    }

    /// <summary>Creates a descriptor for an administrator-configured workspace root.</summary>
    public static TraeAgentWorkspace FromRoot(Uri rootUri, string? projectId = null, string? workspaceId = null)
    {
        ArgumentNullException.ThrowIfNull(rootUri);
        if (!rootUri.IsAbsoluteUri || !string.Equals(rootUri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Agent workspace root must be an absolute file URI.", nameof(rootUri));

        string identifier = Guid.NewGuid().ToString("N");
        return new TraeAgentWorkspace(
            string.IsNullOrWhiteSpace(projectId) ? $"project-{identifier}" : projectId,
            string.IsNullOrWhiteSpace(workspaceId) ? $"workspace-{identifier}" : workspaceId,
            rootUri);
    }
}

/// <summary>Represents one fully framed SSE event returned by the TRAE Agent API.</summary>
public sealed record TraeAgentSseFrame(string Event, string Data);

/// <summary>Reads SSE frames without relying on TRAE, Electron, CDP, or Aha IPC.</summary>
public static class TraeAgentSseReader
{
    /// <summary>Reads UTF-8 Server-Sent Events, preserving multi-line data payloads.</summary>
    public static async IAsyncEnumerable<TraeAgentSseFrame> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string eventName = "message";
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new TraeAgentSseFrame(eventName, data.ToString());
                    eventName = "message";
                    data.Clear();
                }
                continue;
            }

            if (line[0] == ':') continue;
            int separator = line.IndexOf(':');
            string field = separator < 0 ? line : line[..separator];
            string value = separator < 0 ? "" : line[(separator + 1)..].TrimStart(' ');
            if (field == "event") eventName = value;
            else if (field == "data")
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(value);
            }
        }

        if (data.Length > 0)
            yield return new TraeAgentSseFrame(eventName, data.ToString());
    }

    /// <summary>Parses a JSON payload and reports an invalid upstream event clearly.</summary>
    public static JsonObject ParseObject(TraeAgentSseFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return JsonNode.Parse(frame.Data)?.AsObject()
            ?? throw new InvalidDataException($"TRAE Agent SSE event '{frame.Event}' does not contain a JSON object.");
    }
}