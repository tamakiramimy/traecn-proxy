using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TrancnProxy.Agent;

/// <summary>Represents a task request whose session identity must originate from the upstream session service.</summary>
public sealed record TraeAgentTaskRequest(string SessionId, JsonObject Body)
{
    /// <summary>Builds the task body while rejecting attempts to replace the server-issued session ID.</summary>
    public JsonObject ToJson()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SessionId);
        ArgumentNullException.ThrowIfNull(Body);

        var body = Body.DeepClone().AsObject();
        string? bodySessionId = (string?)body["session_id"];
        if (!string.IsNullOrWhiteSpace(bodySessionId) && !string.Equals(bodySessionId, SessionId, StringComparison.Ordinal))
            throw new InvalidOperationException("Agent task body must use the server-issued session ID.");
        body["session_id"] = SessionId;
        return body;
    }
}

/// <summary>Raised when the CUE Agent upstream rejects a task before streaming.</summary>
public sealed class TraeAgentUpstreamException(HttpStatusCode statusCode)
    : InvalidOperationException($"TRAE Agent upstream rejected the task with HTTP {(int)statusCode} ({statusCode}).")
{
    /// <summary>Gets the upstream response status.</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>Streams the confirmed CUE Agent HTTP API without Electron, CDP, Aha, or local TRAE state.</summary>
public sealed class TraeAgentClient
{
    /// <summary>The confirmed CUE Agent task endpoint.</summary>
    public const string CreateAgentTaskPath = "api/cue_agent/v3/create_agent_task";

    private readonly TraeClient _upstream;

    /// <summary>Initializes a client sharing the account's authenticated TRAE HTTP transport.</summary>
    public TraeAgentClient(TraeClient upstream)
    {
        _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
    }

    /// <summary>Posts a task using a server-issued session ID and yields fully framed upstream SSE events.</summary>
    public async IAsyncEnumerable<TraeAgentSseFrame> CreateAgentTaskStreamAsync(
        TraeAgentTaskRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _upstream.SendJsonAsync(
            HttpMethod.Post,
            CreateAgentTaskPath,
            request.ToJson(),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TraeAgentUpstreamException(response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var frame in TraeAgentSseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
            yield return frame;
    }
}