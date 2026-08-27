using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TrancnProxy.Agent;

/// <summary>Represents a session created by the upstream service, not a client-generated identifier.</summary>
public sealed record TraeAgentSession(string SessionId, string ModelId)
{
    /// <summary>Validates the server-issued values required to start an Agent task.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ModelId);
    }
}

/// <summary>Normalizes verified CUE Agent events for the proxy transport layer.</summary>
public sealed record TraeAgentStreamEvent(string Event, JsonObject Payload);

/// <summary>Consumes a CUE Agent task while enforcing model confirmation and a terminal event.</summary>
public sealed class TraeAgentSessionRunner
{
    private readonly TraeAgentClient _client;

    /// <summary>Initializes the runner with an authenticated direct HTTP client.</summary>
    public TraeAgentSessionRunner(TraeAgentClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>Runs an Agent task using an upstream-issued session and yields validated events.</summary>
    public async IAsyncEnumerable<TraeAgentStreamEvent> RunAsync(
        TraeAgentSession session,
        JsonObject taskBody,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(taskBody);
        session.Validate();

        bool modelVerified = false;
        bool completed = false;
        await foreach (var frame in _client.CreateAgentTaskStreamAsync(
            new TraeAgentTaskRequest(session.SessionId, taskBody), cancellationToken).ConfigureAwait(false))
        {
            JsonObject payload = TraeAgentSseReader.ParseObject(frame);
            if (string.Equals(frame.Event, "model_config", StringComparison.Ordinal))
            {
                string actualModel = (string?)payload["model_name"] ?? (string?)payload["model"] ?? "";
                if (!string.Equals(actualModel, session.ModelId, StringComparison.Ordinal))
                    throw new TraeModelSelectionException(session.ModelId, actualModel);
                modelVerified = true;
            }
            else if (IsOutputEvent(frame.Event) && !modelVerified)
            {
                throw new InvalidOperationException("TRAE Agent produced output before confirming the actual model.");
            }
            else if (IsTerminalEvent(frame.Event))
            {
                completed = true;
            }

            yield return new TraeAgentStreamEvent(frame.Event, payload);
        }

        if (!modelVerified)
            throw new InvalidOperationException("TRAE Agent response did not confirm the actual model.");
        if (!completed)
            throw new InvalidOperationException("TRAE Agent stream ended without a terminal event.");
    }

    private static bool IsOutputEvent(string eventName) =>
        eventName is "thought" or "tool_call" or "chat_done" or "progress_notice";

    private static bool IsTerminalEvent(string eventName) =>
        eventName is "turn_completion" or "error";
}