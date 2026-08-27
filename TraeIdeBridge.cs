using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TrancnProxy;

/// <summary>Streams TRAE Agent chat through the running IDE renderer.</summary>
public sealed class TraeIdeBridge
{
    private readonly HttpClient _httpClient;
    private readonly Uri _debugEndpoint;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly TraeProtocolEvidenceWriter? _protocolEvidenceWriter;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private bool _hookInitialized;

    /// <summary>Initializes the IDE bridge.</summary>
    public TraeIdeBridge(
        string debugEndpoint,
        TimeSpan? requestTimeout = null,
        TimeSpan? pollInterval = null,
        TraeProtocolEvidenceWriter? protocolEvidenceWriter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(debugEndpoint);
        _debugEndpoint = new Uri(debugEndpoint.TrimEnd('/') + "/", UriKind.Absolute);
        _requestTimeout = requestTimeout ?? TimeSpan.FromMinutes(5);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(35);
        _protocolEvidenceWriter = protocolEvidenceWriter;
        _httpClient = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    /// <summary>Gets whether a compatible TRAE workbench renderer is reachable.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await GetWorkbenchWebSocketUriAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Starts a chat and returns normalized proxy events.</summary>
    public async IAsyncEnumerable<TraeSseEvent> ChatStreamAsync(
        IEnumerable<(string role, string text)> messages,
        TraeModelDescriptor model,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(model);

        string prompt = FormatPrompt(messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("At least one non-empty message is required.", nameof(messages));

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? bridgeRequestId = null;
        try
        {
            await EnsureHookInitializedAsync(cancellationToken).ConfigureAwait(false);
            var payload = BuildUiPayload(prompt, model);
            string encodedRequest = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
            bridgeRequestId = await EvaluateStringAsync(
                BuildUiStartScript(encodedRequest),
                cancellationToken).ConfigureAwait(false);
            await DispatchEnterAsync(cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            bool modelVerified = false;
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                string rawPoll = await EvaluateStringAsync(BuildPollScript(bridgeRequestId), timeout.Token).ConfigureAwait(false);
                var poll = JsonNode.Parse(rawPoll)?.AsObject()
                    ?? throw new TraeIdeBridgeException("TRAE bridge returned an invalid poll response.");

                foreach (var eventNode in poll["events"]?.AsArray() ?? new JsonArray())
                {
                    var eventObject = eventNode?.AsObject();
                    if (eventObject is null) continue;
                    string eventName = (string?)eventObject["event"] ?? "";
                    var data = eventObject["data"]?.AsObject() ?? new JsonObject();
                    if (eventName == "metadata")
                    {
                        string actualModel = (string?)data["model"] ?? "";
                        if (!string.Equals(actualModel, model.Id, StringComparison.Ordinal))
                            throw new TraeModelSelectionException(model.Id, actualModel);
                        modelVerified = true;
                    }
                    else if (eventName == "output" && !modelVerified)
                    {
                        throw new TraeIdeBridgeException("TRAE Agent produced output before confirming the actual model.");
                    }

                    yield return new TraeSseEvent(eventName, data.ToJsonString());
                }

                foreach (var evidenceNode in poll["evidence"]?.AsArray() ?? new JsonArray())
                    _protocolEvidenceWriter?.Write(evidenceNode);

                string? error = (string?)poll["error"];
                if (!string.IsNullOrWhiteSpace(error))
                    throw new TraeIdeBridgeException(error);

                bool done = (bool?)poll["done"] == true;
                if (done)
                {
                    if (!modelVerified)
                        throw new TraeIdeBridgeException("TRAE Agent response did not identify the actual model.");
                    yield break;
                }

                await Task.Delay(_pollInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (bridgeRequestId is not null)
            {
                try
                {
                    await EvaluateStringAsync(BuildCancelScript(bridgeRequestId), CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // The renderer may already be gone; cancellation is best effort.
                }
            }
            _requestGate.Release();
        }
    }

    private async Task EnsureHookInitializedAsync(CancellationToken cancellationToken)
    {
        if (_hookInitialized && await IsHookReadyAsync(cancellationToken).ConfigureAwait(false)) return;

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsHookReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                _hookInitialized = true;
                return;
            }

            if (!await InstallHookAndReloadAsync(cancellationToken).ConfigureAwait(false))
                throw new TraeIdeBridgeException("TRAE workbench did not become ready after bridge initialization.");
            _hookInitialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task<bool> InstallHookAndReloadAsync(CancellationToken cancellationToken)
    {
        Uri webSocketUri = await GetWorkbenchWebSocketUriAsync(cancellationToken).ConfigureAwait(false);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(webSocketUri, cancellationToken).ConfigureAwait(false);

        int commandId = 1;
        await SendCdpCommandAsync(socket, commandId++, "Page.enable", new JsonObject(), cancellationToken).ConfigureAwait(false);
        await SendCdpCommandAsync(socket, commandId++, "Page.addScriptToEvaluateOnNewDocument", new JsonObject
        {
            ["source"] = BuildHookBootstrapScript()
        }, cancellationToken).ConfigureAwait(false);
        await SendCdpCommandAsync(socket, commandId++, "Page.reload", new JsonObject
        {
            ["ignoreCache"] = true
        }, cancellationToken).ConfigureAwait(false);

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            try
            {
                JsonObject response = await SendCdpCommandAsync(
                    socket,
                    commandId++,
                    "Runtime.evaluate",
                    new JsonObject
                    {
                        ["expression"] = "String(Boolean(globalThis.__trancnUiBridge?.installed && document.querySelector('.chat-input-v2-input-box-editable')))",
                        ["returnByValue"] = true
                    },
                    cancellationToken).ConfigureAwait(false);
                if ((string?)response["result"]?["result"]?["value"] == "true") return true;
            }
            catch (TraeIdeBridgeException)
            {
                // The old execution context may disappear while the workbench reloads.
            }
        }
        return false;
    }

    private async Task<bool> IsHookReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            string value = await EvaluateStringAsync(
                "String(Boolean(globalThis.__trancnUiBridge?.installed && document.querySelector('.chat-input-v2-input-box-editable')))",
                cancellationToken).ConfigureAwait(false);
            return string.Equals(value, "true", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private async Task DispatchEnterAsync(CancellationToken cancellationToken)
    {
        static JsonObject BuildKeyEvent(string type) => new()
        {
            ["type"] = type,
            ["key"] = "Enter",
            ["code"] = "Enter",
            ["windowsVirtualKeyCode"] = 13,
            ["nativeVirtualKeyCode"] = 36
        };
        await SendCdpCommandAsync("Input.dispatchKeyEvent", BuildKeyEvent("keyDown"), cancellationToken).ConfigureAwait(false);
        await SendCdpCommandAsync("Input.dispatchKeyEvent", BuildKeyEvent("keyUp"), cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject BuildUiPayload(string prompt, TraeModelDescriptor model) => new()
    {
        ["prompt"] = prompt,
        ["config_name"] = model.ConfigName,
        ["display_name"] = model.DisplayName
    };

    private static string FormatPrompt(IEnumerable<(string role, string text)> messages) =>
        string.Join("\n\n", messages
            .Where(message => !string.IsNullOrWhiteSpace(message.text))
            .Select(message => $"{NormalizeRole(message.role)}:\n{message.text}"));

    private static string NormalizeRole(string role) => role.ToLowerInvariant() switch
    {
        "system" => "System",
        "assistant" => "Assistant",
        _ => "User"
    };

    private async Task<string> EvaluateStringAsync(string expression, CancellationToken cancellationToken)
    {
        JsonObject response = await SendCdpCommandAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = true
        }, cancellationToken).ConfigureAwait(false);
        if (response["result"]?["exceptionDetails"] is JsonObject exception)
            throw new TraeIdeBridgeException(
                (string?)exception["exception"]?["description"]
                ?? (string?)exception["text"]
                ?? "TRAE renderer evaluation failed.");

        JsonNode? value = response["result"]?["result"]?["value"];
        return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out string? stringValue)
            ? stringValue ?? ""
            : value?.ToJsonString() ?? "";
    }

    private async Task<JsonObject> SendCdpCommandAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        Uri webSocketUri = await GetWorkbenchWebSocketUriAsync(cancellationToken).ConfigureAwait(false);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(webSocketUri, cancellationToken).ConfigureAwait(false);
        return await SendCdpCommandAsync(socket, 1, method, parameters, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonObject> SendCdpCommandAsync(
        ClientWebSocket socket,
        int commandId,
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var command = new JsonObject
        {
            ["id"] = commandId,
            ["method"] = method,
            ["params"] = parameters
        };
        byte[] requestBytes = Encoding.UTF8.GetBytes(command.ToJsonString());
        await socket.SendAsync(requestBytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

        JsonObject response;
        do
        {
            string responseText = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
            response = JsonNode.Parse(responseText)?.AsObject()
                ?? throw new TraeIdeBridgeException("TRAE CDP returned invalid JSON.");
        }
        while ((int?)response["id"] != commandId);

        if (response["error"] is JsonObject protocolError)
            throw new TraeIdeBridgeException((string?)protocolError["message"] ?? "TRAE CDP command failed.");
        return response;
    }

    private async Task<Uri> GetWorkbenchWebSocketUriAsync(CancellationToken cancellationToken)
    {
        Uri listUri = new(_debugEndpoint, "json/list");
        string body = await _httpClient.GetStringAsync(listUri, cancellationToken).ConfigureAwait(false);
        var targets = JsonNode.Parse(body)?.AsArray()
            ?? throw new TraeIdeBridgeException("TRAE CDP target list is invalid.");
        string? webSocketUrl = targets
            .OfType<JsonObject>()
            .Where(target => (string?)target["type"] == "page")
            .Where(target => ((string?)target["url"])?.Contains("workbench.html", StringComparison.OrdinalIgnoreCase) == true)
            .Select(target => (string?)target["webSocketDebuggerUrl"])
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        return webSocketUrl is null
            ? throw new TraeIdeBridgeException($"No TRAE workbench renderer was found at {_debugEndpoint}.")
            : new Uri(webSocketUrl);
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var output = new MemoryStream();
        while (true)
        {
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new TraeIdeBridgeException("TRAE CDP connection closed unexpectedly.");
            output.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(output.ToArray());
        }
    }

    private static string BuildHookBootstrapScript() => """
                (() => {
                    if (globalThis.__trancnUiBridge?.installed) return;
                    const bridge = globalThis.__trancnUiBridge = { installed: true, requests: new Map(), activeId: null };
                    const stringify = JSON.stringify, parse = JSON.parse;
                    const active = () => bridge.activeId ? bridge.requests.get(bridge.activeId) : null;
                    const finish = (state, error) => {
                        if (state.done) return;
                        state.error = error || null;
                        state.done = true;
                    };
                    JSON.stringify = function(value, ...args) {
                        try {
                            const state = active(), packet = Array.isArray(value?.params) ? value.params[0] : null, params = packet?.params;
                            if (state && value?.method === 'request_stream' && packet?.channel_id) {
                                state.captureChannels.add(packet.channel_id);
                                if (!state.channelId && params?.service === 'chat' && params?.method === 'chat') {
                                    state.channelId = packet.channel_id;
                                    params.data.model_name = state.configName;
                                    params.data.custom_model = {
                                        ...(params.data.custom_model || {}), config_name: state.configName,
                                        display_model_name: state.displayName, is_preset: true, use_remote_service: true
                                    };
                                }
                                state.evidence.push({ direction: 'outbound', envelope: value });
                            }
                        } catch (error) {
                            const state = active();
                            if (state) finish(state, error?.message || String(error));
                        }
                        return stringify.call(this, value, ...args);
                    };
                    JSON.parse = function(text, ...args) {
                        const value = parse.call(this, text, ...args);
                        try {
                            const state = active(), packet = value?.params?.data;
                            if (state?.captureChannels?.has(packet?.channel_id))
                                state.evidence.push({ direction: 'inbound', envelope: packet });
                            if (!state?.channelId || packet?.channel_id !== state.channelId) return value;
                            const envelope = packet.params;
                            if (envelope?.code && envelope.code !== 0) {
                                finish(state, envelope.message || 'TRAE Agent error ' + envelope.code);
                                return value;
                            }
                            const eventData = envelope?.data, event = eventData?.event, data = eventData?.payload || {};
                            if (event === 'model_config') {
                                state.events.push({ event: 'metadata', data: { model: data.model_name || '' } });
                            } else if (event === 'token_usage') {
                                state.events.push({ event: 'token_usage', data: {
                                    prompt_tokens: data.prompt_tokens || data.input_tokens || 0,
                                    completion_tokens: data.completion_tokens || data.output_tokens || 0,
                                    total_tokens: data.total_tokens || 0
                                }});
                            } else if (event === 'plan_item') {
                                const itemId = data.id || 'default', previous = state.itemText.get(itemId) || '';
                                const current = typeof data.thought === 'string' && data.thought
                                    ? data.thought : (data.tool_call_info?.params?.summary || '');
                                if (current) {
                                    const delta = current.startsWith(previous) ? current.slice(previous.length) : (previous === current ? '' : current);
                                    state.itemText.set(itemId, current);
                                    if (delta) state.events.push({ event: 'output', data: { response: delta } });
                                }
                            } else if (event === 'done') {
                                state.events.push({ event: 'done', data: { finish_reason: 'stop' } });
                                finish(state, null);
                            }
                        } catch (error) {
                            const state = active();
                            if (state) finish(state, error?.message || String(error));
                        }
                        return value;
                    };
                })()
                """;

    private static string BuildUiStartScript(string encodedRequest) => $$$$"""
                (() => {
                    const bridge = globalThis.__trancnUiBridge;
                    if (!bridge?.installed) throw new Error('TRAE UI bridge hook is not initialized.');
                    const decode = value => JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(value), char => char.charCodeAt(0))));
                    const payload = decode('{{{{encodedRequest}}}}');
                    const bridgeId = crypto.randomUUID();
                    if (bridge.activeId && !bridge.requests.get(bridge.activeId)?.done)
                        throw new Error('Another TRAE UI bridge request is active.');
                    bridge.requests.set(bridgeId, {
                        configName: payload.config_name, displayName: payload.display_name,
                        channelId: null, captureChannels: new Set(), evidence: [], events: [], itemText: new Map(), done: false, error: null
                    });
                    bridge.activeId = bridgeId;
                    const newTask = document.querySelector('button[aria-label^="新建任务"], [role="button"][aria-label^="新建任务"]');
                    if (!newTask) throw new Error('TRAE new task button was not found.');
                    newTask.click();
                    const input = document.querySelector('.chat-input-v2-input-box-editable[contenteditable="true"], .chat-input-v2-input-box-editable');
                    if (!input) throw new Error('TRAE chat input was not found.');
                    input.focus(); input.textContent = '';
                    const prompt = payload.prompt || '';
                    document.execCommand('insertText', false, prompt);
                    input.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: null }));
                    return bridgeId;
                })()
                """;

    private static string BuildPollScript(string bridgeRequestId) => $$$$"""
        (() => {
                    const bridge = globalThis.__trancnUiBridge;
                    const state = bridge?.requests?.get('{{{{bridgeRequestId}}}}');
          if (!state) return JSON.stringify({ events: [], done: true, error: 'TRAE bridge request state was lost.' });
          const events = state.events.splice(0, state.events.length);
          const evidence = state.evidence.splice(0, state.evidence.length);
          const result = JSON.stringify({ events, evidence, done: state.done, error: state.error });
                    if (state.done && events.length === 0) {
                        bridge.requests.delete('{{{{bridgeRequestId}}}}');
                        if (bridge.activeId === '{{{{bridgeRequestId}}}}') bridge.activeId = null;
                    }
          return result;
        })()
        """;

    private static string BuildCancelScript(string bridgeRequestId) => $$$$"""
        (() => {
                    const bridge = globalThis.__trancnUiBridge, state = bridge?.requests?.get('{{{{bridgeRequestId}}}}');
          if (state) {
            state.done = true; bridge.requests.delete('{{{{bridgeRequestId}}}}');
          }
                    if (bridge?.activeId === '{{{{bridgeRequestId}}}}') bridge.activeId = null;
          return 'ok';
        })()
        """;
}

/// <summary>Thrown when the running TRAE IDE bridge cannot complete a request.</summary>
public sealed class TraeIdeBridgeException(string message) : InvalidOperationException(message);