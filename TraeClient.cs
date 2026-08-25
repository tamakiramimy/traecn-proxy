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

/// <summary>
/// Trae CN(企业版)上游 API 客户端。自动走系统代理(企业网络必需)。
/// </summary>
public class TraeClient
{
    public const string DefaultAppId = "6eefa01c-1036-4c7e-9ca5-d891f63bfcd8";
    public const string DefaultClientId = "ono9krqynydwx5";
    public const int AppVersionCode = 20260806;

    private readonly HttpClient _http;
    private readonly string _apiHost;
    private readonly string _deviceId;
    private readonly string _machineId;
    private readonly TraeAuthData _auth;
    private JsonNode? _modelCatalog;

    public TraeClient(TraeAuthData auth, string? deviceId = null, string? machineId = null)
    {
        _auth = auth;
        _apiHost = string.IsNullOrWhiteSpace(auth.ApiHost) ? "https://console.enterprise.trae.cn" : auth.ApiHost!;
        (_deviceId, _machineId) = (deviceId ?? "0", machineId ?? "0");
        _http = BuildHttpClient();
    }

    public string ApiHost => _apiHost;

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

    private void AddHeaders(HttpRequestMessage req)
    {
        var h = req.Headers;
        h.TryAddWithoutValidation("Authorization", $"Cloud-IDE-JWT {_auth.Token}");
        h.TryAddWithoutValidation("x-cloudide-token", _auth.Token);
        h.TryAddWithoutValidation("x-app-id", DefaultAppId);
        h.TryAddWithoutValidation("x-app-version", "default");
        h.TryAddWithoutValidation("x-app-version-code", AppVersionCode.ToString());
        h.TryAddWithoutValidation("x-device-id", _deviceId);
        h.TryAddWithoutValidation("x-machine-id", _machineId);
        h.TryAddWithoutValidation("x-device-type", OperatingSystem.IsMacOS() ? "mac" : OperatingSystem.IsWindows() ? "windows" : "linux");
        h.TryAddWithoutValidation("x-device-brand", DeviceBrand());
        h.TryAddWithoutValidation("x-device-cpu", OperatingSystem.IsMacOS() ? "Apple" : "Unknown");
        h.TryAddWithoutValidation("x-os-version", OsVersion());
        h.TryAddWithoutValidation("x-ide-version", "3.3.87");
        h.TryAddWithoutValidation("x-ide-version-code", AppVersionCode.ToString());
        h.TryAddWithoutValidation("x-ide-version-type", "stable");
        h.TryAddWithoutValidation("request-traffic-type", "prod");
        h.TryAddWithoutValidation("x-request-id", Guid.NewGuid().ToString());
        if (!string.IsNullOrEmpty(_auth.UserId))
            h.TryAddWithoutValidation("x-uid", _auth.UserId);
        req.Headers.Accept.TryParseAdd("text/event-stream");
    }

    private static string DeviceBrand()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("sysctl", "-n hw.model") { RedirectStandardOutput = true };
            var p = System.Diagnostics.Process.Start(psi);
            return (p?.StandardOutput.ReadToEnd().Trim() ?? "Mac");
        }
        catch { return OperatingSystem.IsMacOS() ? "Mac" : "PC"; }
    }

    private static string OsVersion() => $"{Environment.OSVersion.Platform} {Environment.OSVersion.Version}";

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

    /// <summary>企业模型目录(chat_v3 函数配置),用于 /v1/models。</summary>
    public async Task<JsonNode> GetModelCatalogAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _modelCatalog != null) return _modelCatalog;
        var body = new JsonObject
        {
            ["functions"] = new JsonArray("chat_v3", "chat", "inline_chat"),
            ["agentType"] = "",
            ["currentConfigInfo"] = new JsonObject { ["configName"] = "", ["isCustomModel"] = false },
            ["modeType"] = "Manual",
            ["accessType"] = "Default",
            ["abForceVids"] = "",
            ["abAutotestAdvancedMode"] = 0,
            ["showCustomModel"] = true
        };
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_apiHost}/api/ide/v1/batch_get_detail_param");
        AddHeaders(req);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        _modelCatalog = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        return _modelCatalog!;
    }

    public IAsyncEnumerable<TraeSseEvent> ChatStreamAsync(
        IEnumerable<(string role, string text)> messages, string model, CancellationToken ct = default)
    {
        return ChatStreamCore(messages, model, ct);
    }

    private async IAsyncEnumerable<TraeSseEvent> ChatStreamCore(
        IEnumerable<(string role, string text)> messages, string model, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var msgList = new JsonArray();
        foreach (var (role, text) in messages)
            msgList.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text })
            });

        string sessionId = Guid.NewGuid().ToString();
        var body = new JsonObject
        {
            ["messages"] = msgList,
            ["model"] = model,
            ["function"] = "chat_v3",
            ["stream"] = true,
            ["request_id"] = Guid.NewGuid().ToString(),
            ["session_id"] = sessionId,
            ["app_version_code"] = AppVersionCode
        };

        var req = new HttpRequestMessage(HttpMethod.Post, $"{_apiHost}/api/agent/v3/llm_utils_chat");
        AddHeaders(req);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Trae API {resp.StatusCode}: {Truncate(err, 300)}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? eventName = null;
        bool receivedMetadata = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("event:"))
            {
                eventName = t[6..].Trim();
                continue;
            }
            if (t.StartsWith("data:"))
            {
                string data = t[5..].Trim();
                if (eventName == "metadata")
                {
                    receivedMetadata = true;
                    string? actualModel = (string?)(JsonNode.Parse(data) as JsonObject)?["model"];
                    if (!string.IsNullOrWhiteSpace(actualModel) && !MatchesRequestedModel(model, actualModel))
                        throw new TraeModelSelectionException(model, actualModel);
                }
                else if (eventName == "output" && !receivedMetadata)
                {
                    throw new InvalidOperationException("Trae 响应缺少模型 metadata，无法确认实际调用模型。");
                }
                yield return new TraeSseEvent(eventName ?? "", data);
                eventName = null;
            }
        }
    }

    private static bool MatchesRequestedModel(string requestedModel, string actualModel)
    {
        if (string.Equals(requestedModel, actualModel, StringComparison.OrdinalIgnoreCase)) return true;
        const string maxVariant = "__max";
        return requestedModel.EndsWith(maxVariant, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(requestedModel[..^maxVariant.Length], actualModel, StringComparison.OrdinalIgnoreCase);
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
