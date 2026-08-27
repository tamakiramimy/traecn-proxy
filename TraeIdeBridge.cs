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
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private bool _hookInitialized;

    /// <summary>Initializes the IDE bridge.</summary>
    public TraeIdeBridge(string debugEndpoint, TimeSpan? requestTimeout = null, TimeSpan? pollInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(debugEndpoint);
        _debugEndpoint = new Uri(debugEndpoint.TrimEnd('/') + "/", UriKind.Absolute);
        _requestTimeout = requestTimeout ?? TimeSpan.FromMinutes(5);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(35);
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
            var request = BuildBusinessRequest(prompt, model);
            string encodedRequest = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.ToJsonString()));
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
            ["type"] = type, ["key"] = "Enter", ["code"] = "Enter",
            ["windowsVirtualKeyCode"] = 13, ["nativeVirtualKeyCode"] = 36
        };
        await SendCdpCommandAsync("Input.dispatchKeyEvent", BuildKeyEvent("keyDown"), cancellationToken).ConfigureAwait(false);
        await SendCdpCommandAsync("Input.dispatchKeyEvent", BuildKeyEvent("keyUp"), cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject BuildBusinessRequest(string prompt, TraeModelDescriptor model)
    {
        string projectId = NewAgentId();
        string sessionId = NewAgentId();
        return new JsonObject
        {
            ["service"] = "chat",
            ["method"] = "chat",
            ["data"] = new JsonObject
            {
                ["agent_type"] = "solo_agent",
                ["agent_id"] = "solo_agent",
                ["session_id"] = sessionId,
                ["message_id"] = NewAgentId(),
                ["mention_context"] = EmptyMentionContext(),
                ["model_name"] = model.ConfigName,
                ["custom_model"] = new JsonObject
                {
                    ["provider"] = "",
                    ["config_name"] = model.ConfigName,
                    ["display_model_name"] = model.DisplayName,
                    ["multimodal"] = false,
                    ["ak"] = "",
                    ["use_remote_service"] = true,
                    ["is_preset"] = true,
                    ["config_source"] = 1,
                    ["custom_model_id"] = null,
                    ["base_url"] = "",
                    ["region"] = null,
                    ["sk"] = "",
                    ["auth_type"] = 0,
                    ["custom_model_type"] = null,
                    ["reasoning_effort_level"] = "high"
                },
                ["is_in_plan_mode"] = false,
                ["is_in_spec_mode"] = false,
                ["is_solo_mode"] = true,
                ["is_workspace_folder_changed"] = false,
                ["scene_location"] = 2,
                ["ask_question_config"] = new JsonObject
                {
                    ["feature_available"] = true,
                    ["ide_enable"] = true,
                    ["solo_enable"] = true
                },
                ["asr_times"] = 0,
                ["message_content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text_content"] = prompt
                }),
                ["parsed_query"] = new JsonArray(prompt),
                ["code_selections"] = new JsonArray(),
                ["multi_media"] = new JsonArray(),
                ["terminal_context"] = new JsonArray(),
                ["workspace_folders"] = new JsonArray(),
                ["runtime_environment_list"] = new JsonObject
                {
                    ["supportedEnvironments"] = new JsonArray(),
                    ["activedEnvironments"] = new JsonArray()
                }
            },
            ["context"] = new JsonObject
            {
                ["project_id"] = projectId,
                ["chat_session_id"] = sessionId
            }
        };
    }

    private static string NewAgentId() => Guid.NewGuid().ToString("N")[..24];

    private static JsonObject EmptyMentionContext() => new()
    {
        ["only_mention"] = false,
        ["hash_workspace"] = false,
        ["hash_folder"] = false,
        ["hash_files"] = new JsonArray(),
        ["hash_terminals"] = new JsonArray(),
        ["hash_symbols"] = new JsonArray(),
        ["hash_folders"] = new JsonArray(),
        ["hash_webs"] = new JsonArray(),
        ["hash_docs"] = new JsonArray(),
        ["hash_web_elements"] = new JsonArray(),
        ["hash_logs"] = new JsonArray(),
        ["hash_figma"] = new JsonArray(),
        ["hash_lint_error_flag"] = false,
        ["hash_rule_files"] = new JsonArray(),
        ["auto_rule_count"] = 0,
        ["agents_md_count"] = 0,
        ["claude_md_count"] = 0,
        ["hash_problem_items"] = new JsonArray(),
        ["hash_problem_files"] = new JsonArray(),
        ["hash_past_chats"] = new JsonArray()
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
                            if (state && !state.channelId && value?.method === 'request_stream' && params?.service === 'chat' && params?.method === 'chat') {
                                state.channelId = packet.channel_id;
                                params.data.model_name = state.configName;
                                params.data.custom_model = {
                                    ...(params.data.custom_model || {}), config_name: state.configName,
                                    display_model_name: state.displayName, is_preset: true, use_remote_service: true
                                };
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
                    const request = decode('{{{{encodedRequest}}}}'), data = request.data;
                    const bridgeId = crypto.randomUUID();
                    if (bridge.activeId && !bridge.requests.get(bridge.activeId)?.done)
                        throw new Error('Another TRAE UI bridge request is active.');
                    bridge.requests.set(bridgeId, {
                        configName: data.custom_model.config_name, displayName: data.custom_model.display_model_name,
                        channelId: null, events: [], itemText: new Map(), done: false, error: null
                    });
                    bridge.activeId = bridgeId;
                    const newTask = document.querySelector('button[aria-label^="新建任务"], [role="button"][aria-label^="新建任务"]');
                    if (!newTask) throw new Error('TRAE new task button was not found.');
                    newTask.click();
                    const input = document.querySelector('.chat-input-v2-input-box-editable[contenteditable="true"], .chat-input-v2-input-box-editable');
                    if (!input) throw new Error('TRAE chat input was not found.');
                    input.focus(); input.textContent = '';
                    const prompt = data.message_content?.[0]?.text_content || '';
                    document.execCommand('insertText', false, prompt);
                    input.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: null }));
                    return bridgeId;
                })()
                """;

    private static string BuildStartScript(string encodedRequest) => $$$$"""
        (() => {
          const bridge = globalThis.__trancnIdeBridge ??= { requests: new Map(), client: null };
          const bridgeId = crypto.randomUUID();
          const state = { events: [], done: false, error: null, connection: null, itemText: new Map(), phase: 'decode' };
          bridge.requests.set(bridgeId, state);
          const decode = value => JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(value), char => char.charCodeAt(0))));
          const locateClient = () => {
            if (bridge.client && typeof bridge.client.X === 'function') return bridge.client;
            const input = document.querySelector('.chat-input-v2-input-box-editable');
            const fiberKey = input && Object.getOwnPropertyNames(input).find(name => name.startsWith('__reactFiber$'));
            if (!fiberKey) throw new Error('TRAE chat workbench is not ready.');
            let root = input[fiberKey];
            while (root.return) root = root.return;
            const fibers = [], seenFibers = new WeakSet(), fiberQueue = [root];
            while (fiberQueue.length && fibers.length < 100000) {
              const fiber = fiberQueue.shift();
              if (!fiber || seenFibers.has(fiber)) continue;
              seenFibers.add(fiber); fibers.push(fiber);
              if (fiber.child) fiberQueue.push(fiber.child);
              if (fiber.sibling) fiberQueue.push(fiber.sibling);
            }
            const seen = new WeakSet();
            const queue = [];
            for (const fiber of fibers) {
              for (const key of ['memoizedProps', 'memoizedState', 'stateNode', 'dependencies'])
                queue.push({ value: fiber[key], depth: 0 });
            }
            let visited = 0;
            while (queue.length && visited < 300000) {
              const { value, depth } = queue.shift();
              if (!value || (typeof value !== 'object' && typeof value !== 'function') || seen.has(value) || value instanceof Node) continue;
              seen.add(value); visited++;
              const methods = new Set();
              for (let proto = value; proto && proto !== Object.prototype; proto = Object.getPrototypeOf(proto)) {
                for (const [name, descriptor] of Object.entries(Object.getOwnPropertyDescriptors(proto)))
                  if (typeof descriptor.value === 'function') methods.add(name);
              }
              if (methods.has('requestForStream') && methods.has('request') && methods.has('X') && methods.has('Y') && methods.has('qb')) {
                bridge.client = value;
                return value;
              }
              if (depth >= 6) continue;
              let keys = [];
              try { keys = Object.getOwnPropertyNames(value); } catch { continue; }
              for (const key of keys.slice(0, 300)) {
                if (['window', 'document', 'globalThis', 'parent', 'top', 'ownerDocument'].includes(key)) continue;
                let child;
                try { child = value[key]; } catch { continue; }
                if (child && (typeof child === 'object' || typeof child === 'function')) queue.push({ value: child, depth: depth + 1 });
              }
            }
            throw new Error('TRAE AiChatRequestClient was not found.');
          };
          (async () => {
            const request = decode('{{{{encodedRequest}}}}');
                        state.phase = 'locate-client';
            const client = locateClient();
                        state.phase = 'load-identity';
            const [deviceId, userInfo] = await Promise.all([client.qb(), client.Y()]);
                        const now = Math.floor(Date.now() / 1000);
                        request.data.workspace_folders = client.s?.getWorkspace?.()?.folders?.map(folder => folder.uri?.fsPath).filter(Boolean) || [];
                        request.data.agent = {
                            agent_id: 'solo_agent', user_id: String(userInfo?.user_id || userInfo?.userId || ''),
                            name: 'SOLO Agent', unique_name: 'solo_agent', prompt: '', type: 'solo_agent', description: '',
                            built_in_tool_list: [], mcp_list: [], avatar_id: null, created_at: now, updated_at: now,
                            members: [], can_be_sub_agent: false, is_enterprise: false, enterprise_agent_id: null,
                            enterprise_user_id: null, enterprise_tenant_id: null, enterprise_version: null, workspace: null
                        };
                        state.phase = 'build-payload';
                        const originalSessionCommand = client.Q;
                        let payload;
                        try {
                            client.Q = async () => '';
                            payload = await client.X(request, userInfo, deviceId);
                        } finally {
                            client.Q = originalSessionCommand;
                        }
                        state.phase = 'connect-aha';
            const connection = await vscode.ahaIpc.connect('ai-agent');
            state.connection = connection;
            const rpcId = 'trancn-' + crypto.randomUUID(), channelId = 'trancn-' + crypto.randomUUID();
            const packet = { packet_type: 'request', session_id: '', channel_id: channelId, params: payload };
            let streamId = null;
            const finish = error => {
              if (state.done) return;
              state.error = error || null; state.done = true;
              try { connection.off('message', onMessage); } catch {}
              try { connection.disconnect(); } catch {}
            };
            const onMessage = raw => {
              try {
                const message = JSON.parse(raw);
                if (message.id === rpcId) {
                  if (message.error) finish(message.error.message || 'TRAE Aha request failed.');
                  else streamId = message.result?.streamId;
                  return;
                }
                if (!streamId || message.method !== 'rpc.stream.' + streamId) return;
                const envelope = message.params?.data?.params;
                if (envelope?.code && envelope.code !== 0) {
                  finish(envelope.message || 'TRAE Agent error ' + envelope.code); return;
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
                  const current = typeof data.thought === 'string' && data.thought ? data.thought : (data.tool_call_info?.params?.summary || '');
                  if (current) {
                    const delta = current.startsWith(previous) ? current.slice(previous.length) : (previous === current ? '' : current);
                    state.itemText.set(itemId, current);
                    if (delta) state.events.push({ event: 'output', data: { response: delta } });
                  }
                } else if (event === 'done') {
                  state.events.push({ event: 'done', data: { finish_reason: 'stop' } });
                  finish(null);
                }
              } catch (error) { finish(error?.message || String(error)); }
            };
                        state.phase = 'send-request';
            connection.on('message', onMessage);
            connection.send(JSON.stringify({ jsonrpc: '2.0', method: 'request_stream', id: rpcId, params: [packet] }));
                        state.phase = 'streaming';
                    })().catch(error => { state.error = state.phase + ': ' + (error?.message || String(error)); state.done = true; });
          return bridgeId;
        })()
        """;

    private static string BuildPollScript(string bridgeRequestId) => $$$$"""
        (() => {
                    const bridge = globalThis.__trancnUiBridge;
                    const state = bridge?.requests?.get('{{{{bridgeRequestId}}}}');
          if (!state) return JSON.stringify({ events: [], done: true, error: 'TRAE bridge request state was lost.' });
          const events = state.events.splice(0, state.events.length);
          const result = JSON.stringify({ events, done: state.done, error: state.error });
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