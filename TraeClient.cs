using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TrancnProxy;

public record TraeSseEvent(string Event, string Data);

public sealed class TraeModelSelectionException(string requestedModel, string actualModel)
    : InvalidOperationException($"Trae 未选择请求模型 '{requestedModel}'，实际模型为 '{actualModel}'。")
{
}

public sealed class TraeUpstreamException(string message) : InvalidOperationException(message)
{
}

public sealed class TraeIncompleteStreamException(string? detail = null)
    : InvalidOperationException(detail is null
        ? "Trae 响应在完成事件前中断。"
        : $"Trae 响应在完成事件前中断（{detail}）。")
{
}

/// <summary>
/// Trae CN(企业版)上游 API 客户端。自动走系统代理(企业网络必需)。
/// </summary>
public class TraeClient
{
    private static readonly HashSet<string> ReservedChatFields = new(StringComparer.Ordinal)
    {
        "messages", "model", "config_name", "function", "stream", "request_id", "session_id",
        "reasoning_effort_level", "model_auto_selection", "context_window_size"
    };

    public const string DefaultAppId = "6eefa01c-1036-4c7e-9ca5-d891f63bfcd8";
    public const string DefaultClientId = "ono9krqynydwx5";

    private readonly HttpClient _http;
    private readonly string _apiHost;
    private readonly string _chatApiHost;
    private readonly TraeServiceFace _face;
    private readonly TraeClientProfile _controlProfile;
    private readonly string _deviceId;
    private readonly string _machineId;
    private readonly TraeAuthData _auth;
    private readonly TraeModelCatalogCache _modelCatalog;
    private readonly IReadOnlyDictionary<string, string[]> _modelAliases;

    public TraeClient(
        TraeAuthData auth,
        string? deviceId = null,
        string? machineId = null,
        HttpMessageHandler? httpMessageHandler = null,
        string? chatApiHost = null,
        TraeAccountKind accountKind = TraeAccountKind.Auto,
        TraeUpstreamOptions? upstreamOptions = null)
    {
        _auth = auth;
        _apiHost = string.IsNullOrWhiteSpace(auth.ApiHost) ? "https://console.enterprise.trae.cn" : auth.ApiHost!;
        _chatApiHost = string.IsNullOrWhiteSpace(chatApiHost) ? _apiHost : chatApiHost.TrimEnd('/');
        bool hasStandaloneChatHost = !string.Equals(_apiHost, _chatApiHost, StringComparison.OrdinalIgnoreCase);
        _face = TraeServiceFace.Resolve(accountKind, hasStandaloneChatHost, upstreamOptions);
        // 令牌与用户信息始终打在企业控制面上，与 chat 服务面无关。
        _controlProfile = TraeServiceFace.Enterprise(upstreamOptions?.EnterpriseProfile).Profile;
        (_deviceId, _machineId) = (deviceId ?? "0", machineId ?? "0");
        _http = httpMessageHandler is null
            ? BuildHttpClient()
            : new HttpClient(httpMessageHandler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(10) };
        _modelCatalog = new TraeModelCatalogCache(FetchModelCatalogAsync, parseCatalog: _face.ParseCatalog);
        _modelAliases = upstreamOptions?.ModelAliases ?? new Dictionary<string, string[]>();
    }

    public string ApiHost => _apiHost;
    public string ChatApiHost => _chatApiHost;

    /// <summary>Gets the protocol bundle selected for this account.</summary>
    public TraeServiceFace ServiceFace => _face;

    /// <summary>Gets the model used when a request omits one, resolved on the account's service face.</summary>
    public string DefaultModelId => _face.DefaultModelId;

    /// <summary>Sends a JSON request with the authenticated TRAE client headers and proxy configuration.</summary>
    public Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method,
        string relativePath,
        JsonNode body,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(body);

        var request = new HttpRequestMessage(method, new Uri(new Uri(_apiHost.TrimEnd('/') + "/"), relativePath.TrimStart('/')));
        AddHeaders(request);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return _http.SendAsync(request, completionOption, cancellationToken);
    }

    private static HttpClient BuildHttpClient()
    {
        var handler = new HttpClientHandler { UseProxy = true, AutomaticDecompression = DecompressionMethods.All };
        string? proxy = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                     ?? Environment.GetEnvironmentVariable("https_proxy")
                     ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
                     ?? Environment.GetEnvironmentVariable("http_proxy");
        if (!string.IsNullOrWhiteSpace(proxy))
            handler.Proxy = new WebProxy(proxy) { BypassList = new[] { "127.0.0.1", "localhost" } };

        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }

    private void AddHeaders(HttpRequestMessage req, TraeClientProfile? profile = null, bool streaming = true)
    {
        profile ??= _controlProfile;
        var h = req.Headers;
        h.TryAddWithoutValidation("Authorization", $"Cloud-IDE-JWT {_auth.Token}");
        h.TryAddWithoutValidation("x-cloudide-token", _auth.Token);
        h.TryAddWithoutValidation("x-app-id", DefaultAppId);
        if (profile.SendIdeToken)
            h.TryAddWithoutValidation("x-ide-token", _auth.Token);
        if (profile.UserAgent is { } userAgent)
            h.TryAddWithoutValidation("User-Agent", userAgent);
        h.TryAddWithoutValidation("x-app-version", "default");
        h.TryAddWithoutValidation("x-app-version-code", profile.IdeVersionCode);
        h.TryAddWithoutValidation("x-device-id", _deviceId);
        h.TryAddWithoutValidation("x-machine-id", _machineId);
        h.TryAddWithoutValidation("x-device-type", profile.DeviceType);
        h.TryAddWithoutValidation("x-os-version", profile.OsVersion);
        h.TryAddWithoutValidation("x-ide-version", profile.IdeVersion);
        h.TryAddWithoutValidation("x-ide-version-code", profile.IdeVersionCode);
        h.TryAddWithoutValidation("x-device-brand", profile.DeviceBrand);
        if (profile.DeviceCpu is { } deviceCpu)
            h.TryAddWithoutValidation("x-device-cpu", deviceCpu);
        h.TryAddWithoutValidation("x-ide-version-type", "stable");
        h.TryAddWithoutValidation("request-traffic-type", "prod");
        h.TryAddWithoutValidation("x-request-id", Guid.NewGuid().ToString());
        if (!string.IsNullOrEmpty(_auth.UserId))
            h.TryAddWithoutValidation("x-uid", _auth.UserId);
        req.Headers.Accept.TryParseAdd(streaming ? "text/event-stream" : "application/json");
    }

    public async Task<bool> ValidateTokenAsync(CancellationToken ct = default)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_apiHost}/cloudide/api/v3/trae/GetUserInfo");
            AddHeaders(req);
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return resp.IsSuccessStatusCode && doc.RootElement.TryGetProperty("code", out var c) && c.GetInt32() == 0;
        }
        catch { return false; }
    }

    /// <summary>Gets the selectable enterprise <c>chat_v3</c> model catalog.</summary>
    /// <param name="force">Forces an upstream refresh.</param>
    /// <param name="ct">Cancels catalog loading.</param>
    /// <returns>The current account's model catalog.</returns>
    public Task<TraeModelCatalogSnapshot> GetModelCatalogAsync(bool force = false, CancellationToken ct = default) =>
        _modelCatalog.GetAsync(force, ct);

    /// <summary>Fetches the raw upstream catalog response for diagnostics.</summary>
    public Task<JsonNode> GetRawModelCatalogAsync(CancellationToken ct = default) => FetchModelCatalogAsync(ct);

    /// <summary>Resolves an exact model ID in the current account's catalog.</summary>
    /// <param name="modelId">The exact upstream model ID.</param>
    /// <param name="ct">Cancels catalog loading.</param>
    /// <returns>The matching model descriptor.</returns>
    public Task<TraeModelDescriptor> ResolveModelAsync(string modelId, CancellationToken ct = default) =>
        _modelCatalog.ResolveAsync(modelId, ct);

    private async Task<JsonNode> FetchModelCatalogAsync(CancellationToken ct)
    {
        string host = _face.CatalogOnChatHost ? _chatApiHost : _apiHost;
        var req = new HttpRequestMessage(HttpMethod.Post, $"{host}{_face.CatalogPath}");
        AddHeaders(req, _face.CatalogOnChatHost ? _face.Profile : null, streaming: _face.CatalogAcceptsEventStream);
        req.Content = new StringContent(_face.BuildCatalogRequest().ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        string raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new TraeModelCatalogException($"{_face.CatalogPath} {resp.StatusCode}: {Truncate(raw, 300)}");
        return JsonNode.Parse(raw)
            ?? throw new TraeModelCatalogException($"{_face.CatalogPath} response is empty.");
    }

    public IAsyncEnumerable<TraeSseEvent> ChatStreamAsync(
        IEnumerable<(string role, string text)> messages, string model, CancellationToken ct = default)
    {
        return ChatStreamAsync(messages.Select(message => TraeChatMessage.Text(message.role, message.text)), model, ct);
    }

    public IAsyncEnumerable<TraeSseEvent> ChatStreamAsync(
        IEnumerable<TraeChatMessage> messages, string model, CancellationToken ct = default)
    {
        return ChatStreamCore(messages, model, model, null, ct);
    }

    /// <summary>Streams a chat completion for an exact catalog model.</summary>
    /// <param name="messages">The ordered conversation turns.</param>
    /// <param name="model">The resolved catalog model.</param>
    /// <param name="ct">Cancels streaming.</param>
    /// <returns>The upstream SSE events.</returns>
    public IAsyncEnumerable<TraeSseEvent> ChatStreamAsync(
        IEnumerable<(string role, string text)> messages, TraeModelDescriptor model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ChatStreamAsync(messages.Select(message => TraeChatMessage.Text(message.role, message.text)), model, ct);
    }

    public IAsyncEnumerable<TraeSseEvent> ChatStreamAsync(
        IEnumerable<TraeChatMessage> messages, TraeModelDescriptor model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ChatStreamCore(messages, model.Id, model.ConfigName, null, ct);
    }

    public IAsyncEnumerable<TraeSseEvent> ChatStreamAsync(
        IEnumerable<(string role, string text)> messages,
        TraeModelDescriptor model,
        TraeChatTuning tuning,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tuning);
        return ChatStreamAsync(messages.Select(message => TraeChatMessage.Text(message.role, message.text)), model, tuning, ct);
    }

    public IAsyncEnumerable<TraeSseEvent> ChatStreamAsync(
        IEnumerable<TraeChatMessage> messages,
        TraeModelDescriptor model,
        TraeChatTuning tuning,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tuning);
        return ChatStreamCore(messages, model.Id, model.ConfigName, tuning.ApplyModel(model), ct);
    }

    private async IAsyncEnumerable<TraeSseEvent> ChatStreamCore(
        IEnumerable<TraeChatMessage> messages,
        string model,
        string configName,
        TraeChatTuning? tuning,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var msgList = new JsonArray();
        foreach (TraeChatMessage message in messages)
            msgList.Add(message.ToJson());

        string sessionId = Guid.NewGuid().ToString();
        var body = new JsonObject
        {
            ["messages"] = msgList,
            ["model"] = model,
            ["config_name"] = configName,
            ["function"] = _face.ChatFunction,
            ["stream"] = true,
            ["request_id"] = sessionId,
            ["session_id"] = sessionId
        };
        if (tuning?.UpstreamReasoningEffort is { } reasoningEffort)
            body["reasoning_effort_level"] = reasoningEffort;
        if (tuning?.UpstreamModelSelectionStrategy is { } selectionStrategy)
            body["model_auto_selection"] = new JsonObject { ["strategy"] = selectionStrategy };
        if (tuning?.ContextWindowSize is { } contextWindowSize)
            body["context_window_size"] = contextWindowSize;

        var req = new HttpRequestMessage(HttpMethod.Post, $"{_chatApiHost}/api/agent/v3/llm_utils_chat");
        AddHeaders(req, _face.Profile);
        ApplyExtraChatFields(body, Environment.GetEnvironmentVariable("TRANCN_EXTRA_CHAT_FIELDS"));
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Trae API {resp.StatusCode}: {Truncate(err, 300)}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? eventName = null;
        var dataLines = new List<string>();
        bool receivedMetadata = false;
        long streamStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        long lastFrameAt = streamStarted;
        var frameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int responseChars = 0;

        TraeSseEvent? TakeFrame()
        {
            if (dataLines.Count == 0)
            {
                eventName = null;
                return null;
            }

            var frame = new TraeSseEvent(eventName ?? "message", string.Join('\n', dataLines));
            eventName = null;
            dataLines.Clear();
            return frame;
        }

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                var frame = TakeFrame();
                if (frame is null) continue;
                ValidateFrame(frame);
                frameCounts[frame.Event] = frameCounts.GetValueOrDefault(frame.Event) + 1;
                lastFrameAt = System.Diagnostics.Stopwatch.GetTimestamp();
                if (frame.Event == "output") responseChars += frame.Data.Length;
                yield return frame;
                if (frame.Event == "done") yield break;
                continue;
            }

            if (line[0] == ':') continue;
            int separator = line.IndexOf(':');
            string field = separator < 0 ? line : line[..separator];
            string value = separator < 0 ? "" : line[(separator + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            if (field == "event") eventName = value;
            else if (field == "data") dataLines.Add(value);
        }

        var finalFrame = TakeFrame();
        if (finalFrame is not null)
        {
            ValidateFrame(finalFrame);
            frameCounts[finalFrame.Event] = frameCounts.GetValueOrDefault(finalFrame.Event) + 1;
            lastFrameAt = System.Diagnostics.Stopwatch.GetTimestamp();
            yield return finalFrame;
            if (finalFrame.Event == "done") yield break;
        }
        throw new TraeIncompleteStreamException(
            $"{System.Diagnostics.Stopwatch.GetElapsedTime(streamStarted).TotalSeconds:F0}s, " +
            $"末帧后静默 {System.Diagnostics.Stopwatch.GetElapsedTime(lastFrameAt).TotalSeconds:F0}s, " +
            $"帧 [{string.Join(", ", frameCounts.Select(pair => $"{pair.Key}={pair.Value}"))}], " +
            $"output {responseChars} 字符");

        void ValidateFrame(TraeSseEvent frame)
        {
            JsonObject? payload = null;
            if (frame.Event is "metadata" or "output" or "error")
                payload = JsonNode.Parse(frame.Data) as JsonObject;
            if (frame.Event == "error")
            {
                string message = (string?)payload?["message"]
                    ?? (string?)payload?["error"]?["message"]
                    ?? "Trae 上游返回错误事件。";
                throw new TraeUpstreamException(message);
            }
            if (frame.Event == "metadata")
            {
                receivedMetadata = true;
                string? actualModel = (string?)payload?["model"];
                if (!string.IsNullOrWhiteSpace(actualModel) && !MatchesRequestedModel(model, actualModel) && !IsApprovedAlias(model, actualModel))
                    throw new TraeModelSelectionException(model, actualModel);
            }
            else if (frame.Event == "output" && !receivedMetadata)
                throw new TraeUpstreamException("Trae 响应缺少模型 metadata，无法确认实际调用模型。");
        }
    }

    internal static void ApplyExtraChatFields(JsonObject body, string? serializedFields)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrWhiteSpace(serializedFields)) return;

        JsonObject extra;
        try
        {
            extra = JsonNode.Parse(serializedFields) as JsonObject
                ?? throw new InvalidDataException("TRANCN_EXTRA_CHAT_FIELDS must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("TRANCN_EXTRA_CHAT_FIELDS contains invalid JSON.", ex);
        }

        foreach (var field in extra)
        {
            if (ReservedChatFields.Contains(field.Key))
                throw new InvalidDataException($"TRANCN_EXTRA_CHAT_FIELDS cannot override reserved field '{field.Key}'.");
            body[field.Key] = field.Value?.DeepClone();
        }
    }

    // 上游目录不声明真实后端模型名，部分 config 的实际模型与请求 ID 没有任何字面关系，
    // 只能靠人工核验后显式登记，不能把包含判定放宽。
    private bool IsApprovedAlias(string requestedModel, string actualModel)
    {
        foreach (string key in (string[])[requestedModel, StripVariant(requestedModel)])
        {
            if (!_modelAliases.TryGetValue(key, out string[]? approved)) continue;
            if (approved.Any(alias => string.Equals(alias, actualModel, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private static bool MatchesRequestedModel(string requestedModel, string actualModel)
    {
        if (string.Equals(requestedModel, actualModel, StringComparison.OrdinalIgnoreCase)) return true;

        string requested = Normalize(StripVariant(requestedModel));
        string actual = Normalize(actualModel);
        if (requested.Length == 0 || actual.Length == 0) return false;
        // 上游回显的是服务商内部模型名（如 ali-deepseek-v4-pro-0813），但必须包含所选模型，否则视为降级。
        if (actual.Contains(requested, StringComparison.Ordinal)) return true;

        const string officialSuffix = "official";
        return requested.EndsWith(officialSuffix, StringComparison.Ordinal) &&
               actual.Contains(requested[..^officialSuffix.Length], StringComparison.Ordinal);
    }

    private static string Normalize(string modelId) =>
        new(modelId.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string StripVariant(string modelId)
    {
        foreach (string suffix in (string[])["__max", "__dev"])
            if (modelId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return modelId[..^suffix.Length];
        return modelId;
    }

    /// <summary>用 refreshToken 换新 token(与 IDE SaaS 版 oauthService 一致:POST /cloudide/api/v3/trae/oauth/ExchangeToken)。</summary>
    public async Task<(string Token, string RefreshToken, string? TokenExpireAt, string? RefreshExpireAt, long? TokenExpireDurationMs)>
        ExchangeTokenAsync(string? refreshToken = null, CancellationToken ct = default)
    {
        refreshToken ??= _auth.RefreshToken;
        if (string.IsNullOrEmpty(refreshToken))
            throw new InvalidOperationException("没有可用的 refreshToken");

        var body = new JsonObject
        {
            ["RefreshToken"] = refreshToken,
            ["ClientSecret"] = "-",
            ["UserID"] = ""
        };
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_apiHost}/cloudide/api/v3/trae/oauth/ExchangeToken");
        req.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        string raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ExchangeToken {resp.StatusCode}: {Truncate(raw, 200)}");

        var doc = JsonNode.Parse(raw)!.AsObject();
        if (doc["code"] is JsonValue cv && cv.TryGetValue<long>(out var code) && code != 0)
            throw new InvalidOperationException($"ExchangeToken code={code}: {Truncate(raw, 200)}");

        // SaaS 版响应在 Data 字段,消费版在 Result 字段,两者都兼容
        var data = (doc["Data"] ?? doc["Result"])?.AsObject()
                   ?? throw new InvalidOperationException($"ExchangeToken 响应缺少 Data/Result: {Truncate(raw, 200)}");
        string token = (string?)data["Token"] ?? throw new InvalidOperationException("ExchangeToken 缺少 Token");
        string newRefresh = (string?)data["RefreshToken"] ?? refreshToken;
        string? tokenExpireAt = DateToString(data["TokenExpireAt"]);
        string? refreshExpireAt = DateToString(data["RefreshExpireAt"]);
        long? duration = null;
        if (data["TokenExpireDuration"] is JsonValue dv && dv.TryGetValue<long>(out var d)) duration = d;

        _auth.Token = token;
        _auth.RefreshToken = newRefresh;
        _auth.ExpiredAt = ParseDateTolerant(tokenExpireAt);
        _auth.RefreshExpiredAt = ParseDateTolerant(refreshExpireAt);
        return (token, newRefresh, tokenExpireAt, refreshExpireAt, duration);
    }

    private static string? DateToString(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(),
        _ => n.ToJsonString()
    };

    private static DateTimeOffset? ParseDateTolerant(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTimeOffset.TryParse(s, out var d)) return d;
        if (long.TryParse(s, out var ms) && ms > 0) return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return null;
    }

    /// <summary>POST /cloudide/api/v3/trae/GetUserInfo,返回完整 JSON(code=0 时)。</summary>
    public async Task<JsonObject> GetUserInfoAsync(string? token = null, CancellationToken ct = default)
    {
        token ??= _auth.Token;
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_apiHost}/cloudide/api/v3/trae/GetUserInfo");
        req.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        req.Headers.TryAddWithoutValidation("x-cloudide-token", token);
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        string raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GetUserInfo {resp.StatusCode}: {Truncate(raw, 200)}");
        var doc = JsonNode.Parse(raw)!.AsObject();
        if (doc["code"] is JsonValue cv && cv.TryGetValue<long>(out var code) && code != 0)
            throw new InvalidOperationException($"GetUserInfo code={code}: {Truncate(raw, 200)}");
        return doc;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
