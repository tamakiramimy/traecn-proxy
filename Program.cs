using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TrancnProxy;

var argsList = args.ToList();
bool forceLogin = argsList.Remove("--login");
bool webLogin = argsList.Remove("--weblogin");
bool testMode = argsList.Remove("--test");
bool listChatModels = argsList.Remove("--chat-models");
bool listAccounts = argsList.Remove("--account-list");
var settings = ProxySettings.Load();
int port = settings.Server.Port;
string listen = settings.Server.Listen;
string? gatewayKey = Environment.GetEnvironmentVariable("TRANCN_API_KEY") ?? EmptyToNull(settings.Security.ApiKey);
string? adminKey = Environment.GetEnvironmentVariable("TRANCN_ADMIN_KEY") ?? EmptyToNull(settings.Security.AdminKey);
string testModel = TraeClient.DefaultChatModel;
string accountAlias = "default";
string? dataDirectory = EmptyToNull(settings.Accounts.DataDirectory);
string? importPath = null;
string? publicBaseUrl = Environment.GetEnvironmentVariable("TRANCN_PUBLIC_BASE_URL") ?? EmptyToNull(settings.Server.PublicBaseUrl);
string? protocolEvidenceDirectory = null;
string? chatApiHost = Environment.GetEnvironmentVariable("TRANCN_CHAT_API_HOST") ?? EmptyToNull(settings.Upstream.ChatApiHost);
for (int i = 0; i < argsList.Count; i++)
{
    if (argsList[i] == "--port" && i + 1 < argsList.Count) port = int.Parse(argsList[++i]);
    else if (argsList[i] == "--listen" && i + 1 < argsList.Count) listen = argsList[++i];
    else if (argsList[i] == "--api-key" && i + 1 < argsList.Count) gatewayKey = argsList[++i];
    else if (argsList[i] == "--model" && i + 1 < argsList.Count) testModel = argsList[++i];
    else if (argsList[i] == "--account" && i + 1 < argsList.Count) accountAlias = argsList[++i];
    else if (argsList[i] == "--data-dir" && i + 1 < argsList.Count) dataDirectory = argsList[++i];
    else if (argsList[i] == "--account-import" && i + 1 < argsList.Count) importPath = argsList[++i];
    else if (argsList[i] == "--public-base-url" && i + 1 < argsList.Count) publicBaseUrl = argsList[++i];
    else if (argsList[i] == "--protocol-evidence-dir" && i + 1 < argsList.Count) protocolEvidenceDirectory = argsList[++i];
    else if (argsList[i] == "--chat-api-host" && i + 1 < argsList.Count) chatApiHost = argsList[++i];
}

if (!string.IsNullOrWhiteSpace(protocolEvidenceDirectory) && IsWithinCurrentWorkspace(protocolEvidenceDirectory))
{
    Console.Error.WriteLine("协议证据目录必须位于当前工作区外，例如 /tmp/trae-protocol-evidence。");
    return 1;
}
using var protocolEvidenceWriter = string.IsNullOrWhiteSpace(protocolEvidenceDirectory)
    ? null
    : new TraeProtocolEvidenceWriter(Path.GetFullPath(protocolEvidenceDirectory));
var ideBridge = settings.IdeBridge.Enabled
    ? new TraeIdeBridge(
        settings.IdeBridge.DebugEndpoint,
        TimeSpan.FromSeconds(Math.Max(1, settings.IdeBridge.RequestTimeoutSeconds)),
        TimeSpan.FromMilliseconds(Math.Max(10, settings.IdeBridge.PollIntervalMilliseconds)),
        protocolEvidenceWriter)
    : null;

if (argsList.Remove("--tc-test"))
{
    string enc = TcCrypto.EncryptStorageValue("{\"hello\":\"世界\"}");
    Console.WriteLine("ENC:" + enc);
    Console.WriteLine("DEC:" + TcCrypto.DecryptStorageValue(enc));
    return 0;
}

Console.WriteLine("=== trancn-proxy : Trae CN 企业版 -> OpenAI/Anthropic 兼容代理 ===");
Console.WriteLine($"数据目录: {TraeAuthStore.DataDir}");

// ---------- 1. 多账号加载与授权 ----------
dataDirectory ??= TraeAuthStore.CacheDir;
ProxyInstanceLock instanceLock;
try { instanceLock = ProxyInstanceLock.Acquire(dataDirectory); }
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
using (instanceLock)
{
var accountStore = new TraeAccountStore(dataDirectory);
var accountManager = new MultiAccountManager(accountStore, chatApiHost);
var oauthLogins = new TraeOAuthLoginManager();
if (!string.IsNullOrWhiteSpace(importPath))
{
    accountManager.ImportJson(await File.ReadAllTextAsync(importPath));
    Console.WriteLine($"已导入 {accountManager.Accounts.Count} 个账号。");
    return 0;
}

if (forceLogin)
{
    var auth = TraeAuthStore.ReadFromStorage();
    var (deviceId, machineId) = TraeAuthStore.ReadDeviceIds();
    accountManager.AddOrReplace(new TraeAccount
    {
        Alias = accountAlias,
        Auth = auth,
        DeviceId = deviceId,
        MachineId = machineId
    });
    Console.WriteLine($"已从 IDE 导入账号: {accountAlias}");
}

if (webLogin || accountManager.Accounts.Count == 0)
{
    var (deviceId, machineId) = TraeAuthStore.ReadDeviceIds();
    var bootstrapClient = new TraeClient(new TraeAuthData(), deviceId, machineId);
    var auth = await StandaloneLogin.LoginAsync(bootstrapClient, machineId, deviceId);
    accountManager.AddOrReplace(new TraeAccount
    {
        Alias = accountAlias,
        Auth = auth,
        DeviceId = deviceId,
        MachineId = machineId
    });
    Console.WriteLine($"已添加网页授权账号: {accountAlias}");
}

if (listAccounts)
{
    foreach (var account in accountManager.Accounts.OrderBy(x => x.Alias))
        Console.WriteLine($"{account.Alias,-16} {(account.Enabled ? "enabled" : "disabled"),-8} {account.Auth.Username ?? account.Auth.UserId}  expires={account.Auth.ExpiredAt:yyyy-MM-dd HH:mm}Z");
    return 0;
}

Console.WriteLine($"账号池就绪: {accountManager.Accounts.Count} 个账号");

if (listChatModels)
{
    using var modelLease = accountManager.AcquireByAlias(accountAlias);
    Console.WriteLine($"--- chat 服务面模型表: {modelLease.Client.ChatApiHost} ---");
    var chatCatalog = await modelLease.Client.GetModelCatalogAsync();
    foreach (var chatModel in chatCatalog.Models)
        Console.WriteLine($"{chatModel.Id,-32} {chatModel.DisplayName}");
    return 0;
}

// ---------- 2. 自测模式 ----------
if (testMode)
{
    Console.WriteLine($"--- 自测:向 {testModel} 发送消息 ---");
    var sb = new StringBuilder();
    using var lease = accountManager.AcquireByAlias(accountAlias);
    try
    {
        var testDescriptor = await lease.Client.ResolveModelAsync(testModel);
        await foreach (var ev in lease.Client.ChatStreamAsync(new[] { ("user", "请只回复四个字:验证成功") }, testDescriptor))
        {
            if (ev.Event == "metadata")
            {
                Console.WriteLine($"[metadata] {ev.Data}");
            }
            else if (ev.Event == "output")
            {
                var j = JsonNode.Parse(ev.Data) as JsonObject;
                string? resp = (string?)j?["response"];
                if (!string.IsNullOrEmpty(resp)) { sb.Append(resp); Console.Write(resp); }
            }
            else if (ev.Event == "token_usage")
            {
                var j = JsonNode.Parse(ev.Data) as JsonObject;
                Console.WriteLine($"\n[usage] prompt={j?["prompt_tokens"]} completion={j?["completion_tokens"]} total={j?["total_tokens"]}");
            }
            else if (ev.Event == "error")
            {
                var payload = JsonNode.Parse(ev.Data) as JsonObject;
                Console.WriteLine($"[error] code={payload?["code"]}");
            }
            else
            {
                var payload = JsonNode.Parse(ev.Data) as JsonObject;
                string keys = payload is null ? "non-json" : string.Join(',', payload.Select(entry => entry.Key));
                Console.WriteLine($"[{ev.Event}] fields={keys}");
            }
        }
    }
    catch (Exception ex) when (ex is TraeModelSelectionException or TraeModelNotFoundException or TraeModelCatalogException)
    {
        Console.WriteLine();
        Console.WriteLine($"自测失败:{ex.Message} ✘");
        return 1;
    }
    Console.WriteLine();
    Console.WriteLine(sb.Length > 0 ? "自测通过 ✔" : "自测失败:未收到回复 ✘");
    return sb.Length > 0 ? 0 : 1;
}

// ---------- 3. API 服务 ----------
var builder = WebApplication.CreateSlimBuilder(argsList.ToArray());
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.TimestampFormat = "HH:mm:ss "; o.SingleLine = true; });

var app = builder.Build();
app.Urls.Add($"http://{listen}:{port}");
app.UseDefaultFiles();
app.UseStaticFiles();

app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (TraeModelSelectionException ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.Clear();
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = new { message = ex.Message, type = "upstream_error", code = "model_selection_mismatch" }
        });
    }
    catch (TraeIdeBridgeException ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.Clear();
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = new { message = ex.Message, type = "upstream_error", code = "ide_bridge_error" }
        });
    }
    catch (Exception ex) when (ex is TraeUpstreamException or TraeIncompleteStreamException && !ctx.Response.HasStarted)
    {
        ctx.Response.Clear();
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = new { message = ex.Message, type = "upstream_error", code = "upstream_incomplete_response" }
        });
    }
});

app.Use(async (ctx, next) =>
{
    bool isBusinessApi = ctx.Request.Path.StartsWithSegments("/v1");
    bool isAdminApi = ctx.Request.Path.StartsWithSegments("/admin/api");
    if ((isBusinessApi && !string.IsNullOrEmpty(gatewayKey)) || (isAdminApi && !string.IsNullOrEmpty(adminKey)))
    {
        string? given = ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "").Trim();
        given = string.IsNullOrEmpty(given) ? ctx.Request.Headers["x-api-key"].ToString().Trim() : given;
        string expected = isAdminApi ? adminKey! : gatewayKey!;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected)))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = new { message = "invalid api key", type = "authentication_error" } });
            return;
        }
    }
    else if (isAdminApi && string.IsNullOrEmpty(adminKey))
    {
        ctx.Response.StatusCode = 404;
        return;
    }
    await next();
});

app.MapGet("/v1/status", async (CancellationToken ct) => new
{
    ok = true,
    chat_upstream = string.IsNullOrWhiteSpace(chatApiHost) ? "ide_bridge" : chatApiHost,
    ide_bridge = new
    {
        enabled = ideBridge is not null,
        available = ideBridge is not null && await ideBridge.IsAvailableAsync(ct),
        account_scope = "current_trae_ide_account"
    },
    accounts = accountManager.Accounts.Select(x => new
    {
        alias = x.Alias,
        enabled = x.Enabled,
        user = x.Auth.Username ?? x.Auth.UserId,
        token_expires = x.Auth.ExpiredAt,
        refresh_expires = x.Auth.RefreshExpiredAt,
        last_error = x.LastError
    })
});

app.MapGet("/v1/models", async (CancellationToken ct) =>
{
    try
    {
        using var lease = accountManager.Acquire(null);
        var catalog = await lease.Client.GetModelCatalogAsync(ct: ct);
        return Results.Json(new JsonObject
        {
            ["object"] = "list",
            ["data"] = new JsonArray(catalog.Models.Select(model => (JsonNode)new JsonObject
            {
                ["id"] = model.Id,
                ["object"] = "model",
                ["display_name"] = model.DisplayName,
                ["config_name"] = model.ConfigName,
                ["variant"] = model.Variant.ToString().ToLowerInvariant(),
                ["owned_by"] = "trae"
            }).ToArray())
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = new { message = ex.Message, type = "upstream_error" } }, statusCode: 502);
    }
});

app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
{
    var ct = ctx.RequestAborted;
    var body = JsonNode.Parse(await new StreamReader(ctx.Request.Body).ReadToEndAsync(ct))!.AsObject();
    string model = (string?)body["model"] ?? TraeClient.DefaultChatModel;
    bool stream = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) && sb;
    var messages = ConvertOpenAIMessages(body["messages"]?.AsArray());
    if (messages.Count == 0)
        return Results.BadRequest(new { error = new { message = "messages is required", type = "invalid_request_error" } });

    using var lease = accountManager.Acquire(SessionKey(ctx, body));
    TraeModelDescriptor descriptor;
    try { descriptor = await lease.Client.ResolveModelAsync(model, ct); }
    catch (TraeModelNotFoundException) { return UnsupportedModel(model); }
    var upstream = ChatUpstream(lease, messages, descriptor, ct);
    if (stream)
    {
        ctx.Response.ContentType = "text/event-stream";
        await WriteOpenAIStream(ctx.Response.Body, upstream, model, ct);
        return Results.Empty;
    }
    return Results.Json(await CollectOpenAI(upstream, model, ct));
});


app.MapPost("/v1/responses", async (HttpContext ctx) =>
{
    var ct = ctx.RequestAborted;
    var body = JsonNode.Parse(await new StreamReader(ctx.Request.Body).ReadToEndAsync(ct))!.AsObject();
    string model = (string?)body["model"] ?? TraeClient.DefaultChatModel;
    bool stream = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) && sb;
    var messages = ConvertResponsesInput(body["input"]);
    if (messages.Count == 0)
        return Results.BadRequest(new { error = new { message = "input is required", type = "invalid_request_error" } });

    using var lease = accountManager.Acquire(SessionKey(ctx, body));
    TraeModelDescriptor descriptor;
    try { descriptor = await lease.Client.ResolveModelAsync(model, ct); }
    catch (TraeModelNotFoundException) { return UnsupportedModel(model); }
    var upstream = ChatUpstream(lease, messages, descriptor, ct);
    string respId = $"resp_{Guid.NewGuid():N}";
    if (stream)
    {
        ctx.Response.ContentType = "text/event-stream";
        await WriteResponsesStream(ctx.Response.Body, upstream, model, respId, ct);
        return Results.Empty;
    }
    return Results.Json(await CollectResponses(upstream, model, respId, ct));
});

app.MapPost("/v1/messages", async (HttpContext ctx) =>
{
    var ct = ctx.RequestAborted;
    var body = JsonNode.Parse(await new StreamReader(ctx.Request.Body).ReadToEndAsync(ct))!.AsObject();
    string model = (string?)body["model"] ?? TraeClient.DefaultChatModel;
    bool stream = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) && sb;
    var messages = ConvertAnthropicMessages(body);
    if (messages.Count == 0)
        return Results.BadRequest(new { type = "error", error = new { message = "messages is required", type = "invalid_request_error" } });

    using var lease = accountManager.Acquire(SessionKey(ctx, body));
    TraeModelDescriptor descriptor;
    try { descriptor = await lease.Client.ResolveModelAsync(model, ct); }
    catch (TraeModelNotFoundException) { return UnsupportedModel(model); }
    var upstream = ChatUpstream(lease, messages, descriptor, ct);
    if (stream)
    {
        ctx.Response.ContentType = "text/event-stream";
        await WriteAnthropicStream(ctx.Response.Body, upstream, model, ct);
        return Results.Empty;
    }
    return Results.Json(await CollectAnthropic(upstream, model, ct));
});

app.MapGet("/admin/api/accounts", () => Results.Json(new
{
    accounts = accountManager.Accounts.OrderBy(x => x.Alias).Select(x => new
    {
        alias = x.Alias,
        enabled = x.Enabled,
        priority = x.Priority,
        max_concurrency = x.MaxConcurrency,
        user = x.Auth.Username ?? x.Auth.UserId,
        token_expires = x.Auth.ExpiredAt,
        refresh_expires = x.Auth.RefreshExpiredAt,
        last_used_at = x.LastUsedAt,
        last_success_at = x.LastSuccessAt,
        last_error = x.LastError
    }),
    settings = new
    {
        load_balancing = accountManager.Settings.LoadBalancing,
        session_ttl_minutes = accountManager.Settings.SessionTtlMinutes,
        default_max_concurrency = accountManager.Settings.DefaultMaxConcurrency
    }
}));

app.MapPost("/admin/api/accounts/import", async (HttpContext ctx) =>
{
    try
    {
        accountManager.ImportJson(await new StreamReader(ctx.Request.Body).ReadToEndAsync(ctx.RequestAborted));
        return Results.Ok(new { ok = true, accounts = accountManager.Accounts.Count });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/admin/api/accounts/{alias}/enable", (string alias) =>
    accountManager.SetEnabled(alias, true) ? Results.Ok(new { ok = true }) : Results.NotFound());
app.MapPost("/admin/api/accounts/{alias}/disable", (string alias) =>
    accountManager.SetEnabled(alias, false) ? Results.Ok(new { ok = true }) : Results.NotFound());
app.MapDelete("/admin/api/accounts/{alias}", (string alias) =>
    accountManager.Remove(alias) ? Results.NoContent() : Results.NotFound());
app.MapPost("/admin/api/accounts/{alias}/priority/{priority:int}", (string alias, int priority) =>
    accountManager.SetPriority(alias, priority) ? Results.Ok(new { ok = true }) : Results.NotFound());
app.MapPost("/admin/api/accounts/{alias}/max-concurrency/{maxConcurrency:int}", (string alias, int maxConcurrency) =>
{
    try
    {
        return accountManager.SetMaxConcurrency(alias, maxConcurrency)
            ? Results.Ok(new { ok = true })
            : Results.NotFound();
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/admin/api/accounts/{alias}/refresh", async (string alias, CancellationToken ct) =>
{
    try { return await accountManager.RefreshAsync(alias, ct) ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = "refresh failed" }); }
    catch (Exception ex) { return Results.NotFound(new { error = ex.Message }); }
});
app.MapPost("/admin/api/accounts/{alias}/test", async (string alias, CancellationToken ct) =>
{
    try
    {
        using var lease = accountManager.AcquireByAlias(alias);
        return await lease.Client.ValidateTokenAsync(ct) ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = "token validation failed" });
    }
    catch (Exception ex) { return Results.NotFound(new { error = ex.Message }); }
});
app.MapPut("/admin/api/settings", async (HttpContext ctx) =>
{
    try
    {
        var body = JsonNode.Parse(await new StreamReader(ctx.Request.Body).ReadToEndAsync(ctx.RequestAborted))?.AsObject();
        accountManager.UpdateSettings((string?)body?["load_balancing"] ?? accountManager.Settings.LoadBalancing,
            (int?)body?["session_ttl_minutes"] ?? accountManager.Settings.SessionTtlMinutes,
            (int?)body?["default_max_concurrency"] ?? accountManager.Settings.DefaultMaxConcurrency);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/admin/api/accounts/login/start", async (HttpContext ctx) =>
{
    try
    {
        var body = JsonNode.Parse(await new StreamReader(ctx.Request.Body).ReadToEndAsync(ctx.RequestAborted))?.AsObject();
        string alias = (string?)body?["alias"] ?? "";
        int maxConcurrency = (int?)body?["max_concurrency"] ?? accountManager.Settings.DefaultMaxConcurrency;
        var (deviceId, machineId) = TraeAuthStore.ReadDeviceIds();
        string baseUrl = publicBaseUrl ?? $"http://{(listen is "0.0.0.0" or "::" ? "127.0.0.1" : listen)}:{port}";
        string callbackUrl = baseUrl.TrimEnd('/') + "/admin/oauth/callback";
        return Results.Ok(new { authorization_url = oauthLogins.Begin(alias, callbackUrl, deviceId, machineId, maxConcurrency) });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/admin/oauth/callback", async (HttpContext ctx) =>
{
    try
    {
        var account = await oauthLogins.CompleteAsync(ctx.Request.Query, ctx.RequestAborted);
        accountManager.AddOrReplace(account);
        return Results.Content("<html><body><h2>授权成功</h2><p>账号已保存，可以关闭此页面返回管理界面。</p></body></html>", "text/html; charset=utf-8");
    }
    catch (Exception ex)
    {
        return Results.Content($"<html><body><h2>授权失败</h2><p>{System.Net.WebUtility.HtmlEncode(ex.Message)}</p></body></html>", "text/html; charset=utf-8", statusCode: 400);
    }
});

Console.WriteLine();
Console.WriteLine($"API 服务: http://{listen}:{port}");
Console.WriteLine("  GET  /v1/status");
Console.WriteLine("  GET  /v1/models");
Console.WriteLine("  POST /v1/chat/completions   (OpenAI 格式)");
Console.WriteLine("  POST /v1/messages           (Anthropic 格式)");
Console.WriteLine($"IDE Bridge: {(ideBridge is null ? "已禁用" : settings.IdeBridge.DebugEndpoint + " (当前 TRAE IDE 账号)")}");
Console.WriteLine($"网关 Key: {(string.IsNullOrEmpty(gatewayKey) ? "(未设置,仅本机访问)" : "已启用")}");
Console.WriteLine($"管理端: {(string.IsNullOrEmpty(adminKey) ? "未启用(TRANCN_ADMIN_KEY)" : $"http://{listen}:{port}/admin")}");
Console.WriteLine();

using var cts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try { await accountManager.RefreshExpiringAccountsAsync(cts.Token); }
        catch (Exception ex) { Console.WriteLine($"[refresh] 账号池刷新失败: {ex.Message}"); }
        try { await Task.Delay(TimeSpan.FromMinutes(30), cts.Token); }
        catch (OperationCanceledException) { break; }
    }
}, cts.Token);

await app.RunAsync();
cts.Cancel();
return 0;
}

// ==================== helpers ====================

string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

bool IsWithinCurrentWorkspace(string candidateDirectory)
{
    string workspace = Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    string candidate = Path.GetFullPath(candidateDirectory);
    return string.Equals(candidate, workspace, StringComparison.Ordinal) ||
           candidate.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}

string? SessionKey(HttpContext ctx, JsonObject body)
{
    string value = ctx.Request.Headers["X-Trancn-Session-Id"].ToString();
    if (string.IsNullOrWhiteSpace(value)) value = (string?)body["user"] ?? "";
    if (string.IsNullOrWhiteSpace(value)) value = (string?)body["metadata"]?["user_id"] ?? "";
    if (string.IsNullOrWhiteSpace(value)) return null;
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

List<(string role, string text)> ConvertOpenAIMessages(JsonArray? arr)
{
    var result = new List<(string, string)>();
    if (arr is null) return result;
    foreach (var m in arr)
    {
        var o = m?.AsObject();
        if (o is null) continue;
        string role = (string?)o["role"] ?? "user";
        string text = o["content"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonArray parts => string.Join("\n", parts.Select(p => (string?)p?["text"] ?? "")),
            _ => ""
        };
        if (!string.IsNullOrEmpty(text)) result.Add((role, text));
    }
    return result;
}

List<(string role, string text)> ConvertAnthropicMessages(JsonObject body)
{
    var result = new List<(string, string)>();
    string? system = body["system"] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : null;
    if (!string.IsNullOrEmpty(system)) result.Add(("system", system!));
    foreach (var m in body["messages"]?.AsArray() ?? new JsonArray())
    {
        var o = m?.AsObject();
        if (o is null) continue;
        string role = (string?)o["role"] ?? "user";
        result.Add((role == "assistant" ? "assistant" : "user", ContentOf(o["content"])));
    }
    return result;
}

string ContentOf(JsonNode? content) => content switch
{
    JsonValue v when v.TryGetValue<string>(out var s) => s,
    JsonArray parts => string.Join("\n", parts.Select(p =>
    {
        string type = (string?)p?["type"] ?? "text";
        return type switch
        {
            "text" => (string?)p?["text"] ?? "",
            "tool_use" => $"[调用工具: {(string?)p?["name"] ?? ""}]\n{p?["input"]?.ToJsonString()}",
            "tool_result" => $"[工具结果]\n{ContentOf(p?["content"])}",
            _ => ""
        };
    })),
    _ => ""
};

// 配置了独立 chat 服务面时直连，否则回落到需要 IDE 在线的 bridge。
// 企业面与独立 chat 面均已可直连，IDE Bridge 不再参与对话。
IAsyncEnumerable<TraeSseEvent> ChatUpstream(
    AccountLease lease, List<(string role, string text)> messages, TraeModelDescriptor descriptor, CancellationToken ct) =>
    lease.Client.ChatStreamAsync(messages, descriptor, ct);

IResult UnsupportedModel(string model) => Results.BadRequest(new
{
    error = new
    {
        message = $"model '{model}' is not available in the current TRAE account catalog; query /v1/models for exact IDs.",
        type = "invalid_request_error",
        code = "model_not_supported"
    }
});

async Task<JsonObject> CollectOpenAI(IAsyncEnumerable<TraeSseEvent> upstream, string model, CancellationToken ct)
{
    var result = await TraeChatResult.CollectAsync(upstream, ct);
    return new JsonObject
    {
        ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
        ["object"] = "chat.completion",
        ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["model"] = model,
        ["choices"] = new JsonArray(new JsonObject
        {
            ["index"] = 0,
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = result.Text },
            ["finish_reason"] = OpenAiFinishReason(result.FinishReason)
        }),
        ["usage"] = new JsonObject
        {
            ["prompt_tokens"] = result.PromptTokens,
            ["completion_tokens"] = result.CompletionTokens,
            ["total_tokens"] = result.TotalTokens
        }
    };
}

async Task WriteOpenAIStream(Stream w, IAsyncEnumerable<TraeSseEvent> upstream, string model, CancellationToken ct)
{
    string id = $"chatcmpl-{Guid.NewGuid():N}";
    int promptTokens = 0, completionTokens = 0, totalTokens = 0;
    bool sawContent = false;
    bool wroteAnything = false;
    string finishReason = "stop";

    async ValueTask SendRaw(string data, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        await w.WriteAsync(bytes, cancellationToken);
        await w.FlushAsync(cancellationToken);
        wroteAnything = true;
    }

    ValueTask SendData(string data, CancellationToken cancellationToken) =>
        SendRaw($"data: {data}\n\n", cancellationToken);

    try
    {
        await SendRaw(": keep-alive\n\n", ct);
        await foreach (var ev in TraeStreamHeartbeat.ReadAsync(
            upstream,
            cancellationToken => SendRaw(": keep-alive\n\n", cancellationToken),
            cancellationToken: ct))
        {
            var j = JsonNode.Parse(ev.Data) as JsonObject;
            if (ev.Event == "output" && j != null)
            {
                string? text = (string?)j["response"];
                if (!string.IsNullOrEmpty(text))
                {
                    bool firstContent = !sawContent;
                    sawContent = true;
                    if (j["finish_reason"] is JsonValue outputReason &&
                        outputReason.TryGetValue<string>(out var parsedOutputReason))
                        finishReason = parsedOutputReason;
                    await SendData(new JsonObject
                    {
                        ["id"] = id,
                        ["object"] = "chat.completion.chunk",
                        ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["model"] = model,
                        ["choices"] = new JsonArray(new JsonObject
                        {
                            ["index"] = 0,
                            ["delta"] = new JsonObject
                            {
                                ["role"] = firstContent ? "assistant" : null,
                                ["content"] = text
                            },
                            ["finish_reason"] = (JsonNode?)null
                        })
                    }.ToJsonString(), ct);
                }
            }
            else if (ev.Event == "token_usage" && j != null)
            {
                promptTokens = (int?)j["prompt_tokens"] ?? promptTokens;
                completionTokens = (int?)j["completion_tokens"] ?? completionTokens;
                totalTokens = (int?)j["total_tokens"] ?? totalTokens;
            }
            else if (ev.Event == "done")
            {
                if (!sawContent) throw new TraeUpstreamException("Trae 上游完成但未返回有效内容。");
                if (j?["finish_reason"] is JsonValue doneReason &&
                    doneReason.TryGetValue<string>(out var parsedDoneReason))
                    finishReason = parsedDoneReason;
                if (totalTokens == 0 && (promptTokens > 0 || completionTokens > 0))
                    totalTokens = promptTokens + completionTokens;
                await SendData(new JsonObject
                {
                    ["id"] = id,
                    ["object"] = "chat.completion.chunk",
                    ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["model"] = model,
                    ["choices"] = new JsonArray(new JsonObject
                    {
                        ["index"] = 0,
                        ["delta"] = new JsonObject(),
                        ["finish_reason"] = OpenAiFinishReason(finishReason)
                    }),
                    ["usage"] = new JsonObject
                    {
                        ["prompt_tokens"] = promptTokens,
                        ["completion_tokens"] = completionTokens,
                        ["total_tokens"] = totalTokens
                    }
                }.ToJsonString(), ct);
                await SendData("[DONE]", ct);
                return;
            }
        }
        throw new TraeIncompleteStreamException();
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception) when (wroteAnything)
    {
        await SendData(new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = "Upstream stream failed before completion.",
                ["type"] = "upstream_error",
                ["code"] = "upstream_incomplete_response"
            }
        }.ToJsonString(), ct);
    }
}

async Task<JsonObject> CollectAnthropic(IAsyncEnumerable<TraeSseEvent> upstream, string model, CancellationToken ct)
{
    var result = await TraeChatResult.CollectAsync(upstream, ct);
    return new JsonObject
    {
        ["id"] = $"msg_{Guid.NewGuid():N}",
        ["type"] = "message",
        ["role"] = "assistant",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }),
        ["model"] = model,
        ["stop_reason"] = AnthropicStopReason(result.FinishReason),
        ["stop_sequence"] = null,
        ["usage"] = new JsonObject { ["input_tokens"] = result.PromptTokens, ["output_tokens"] = result.CompletionTokens }
    };
}

async Task WriteAnthropicStream(Stream w, IAsyncEnumerable<TraeSseEvent> upstream, string model, CancellationToken ct)
{
    string msgId = $"msg_{Guid.NewGuid():N}";
    bool messageStarted = false;
    bool wroteAnything = false;

    async Task WriteEvent(string ev, JsonNode data)
    {
        var bytes = Encoding.UTF8.GetBytes($"event: {ev}\ndata: {data.ToJsonString()}\n\n");
        await w.WriteAsync(bytes, ct);
        await w.FlushAsync(ct);
        wroteAnything = true;
    }

    async Task StartMessage()
    {
        if (messageStarted) return;
        messageStarted = true;
        await WriteEvent("message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = msgId, ["type"] = "message", ["role"] = "assistant",
                ["content"] = new JsonArray(), ["model"] = model,
                ["stop_reason"] = null, ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 }
            }
        });
    }

    bool blockOpen = false;
    int inputTokens = 0;
    int outputTokens = 0;
    string finishReason = "stop";
    try
    {
        await WriteEvent("ping", new JsonObject { ["type"] = "ping" });
        await foreach (var ev in TraeStreamHeartbeat.ReadAsync(
            upstream,
            cancellationToken => new ValueTask(WriteEvent("ping", new JsonObject { ["type"] = "ping" })),
            cancellationToken: ct))
        {
            var j = JsonNode.Parse(ev.Data) as JsonObject;
            if (ev.Event == "output" && j != null)
            {
                string? text = (string?)j["response"];
                if (!string.IsNullOrEmpty(text))
                {
                    await StartMessage();
                    if (!blockOpen)
                    {
                        blockOpen = true;
                        await WriteEvent("content_block_start", new JsonObject
                        {
                            ["type"] = "content_block_start", ["index"] = 0,
                            ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" }
                        });
                    }
                    if (j["finish_reason"] is JsonValue outputReason &&
                        outputReason.TryGetValue<string>(out var parsedOutputReason))
                        finishReason = parsedOutputReason;
                    await WriteEvent("content_block_delta", new JsonObject
                    {
                        ["type"] = "content_block_delta", ["index"] = 0,
                        ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text }
                    });
                }
            }
            else if (ev.Event == "token_usage" && j != null)
            {
                inputTokens = (int?)j["prompt_tokens"] ?? inputTokens;
                outputTokens = (int?)j["completion_tokens"] ?? outputTokens;
            }
            else if (ev.Event == "done")
            {
                if (!blockOpen) throw new TraeUpstreamException("Trae 上游完成但未返回有效内容。");
                if (j?["finish_reason"] is JsonValue doneReason &&
                    doneReason.TryGetValue<string>(out var parsedDoneReason))
                    finishReason = parsedDoneReason;
                await WriteEvent("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 });
                await WriteEvent("message_delta", new JsonObject
                {
                    ["type"] = "message_delta",
                    ["delta"] = new JsonObject { ["stop_reason"] = AnthropicStopReason(finishReason), ["stop_sequence"] = null },
                    ["usage"] = new JsonObject { ["output_tokens"] = outputTokens }
                });
                await WriteEvent("message_stop", new JsonObject { ["type"] = "message_stop" });
                return;
            }
        }
        throw new TraeIncompleteStreamException();
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception) when (wroteAnything)
    {
        await WriteEvent("error", new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject
            {
                ["type"] = "upstream_error",
                ["message"] = "Upstream stream failed before completion."
            }
        });
    }
}

List<(string role, string text)> ConvertResponsesInput(JsonNode? input) => input switch
{
    JsonValue v when v.TryGetValue<string>(out var s) => new List<(string, string)> { ("user", s) },
    JsonArray arr => arr.Select(m =>
    {
        var o = m?.AsObject();
        if (o is null) return ("user", "");
        string role = (string?)o["role"] ?? "user";
        string text = o["content"] switch
        {
            JsonValue cv when cv.TryGetValue<string>(out var cs) => cs,
            JsonArray parts => string.Join("\n", parts.Select(pt => (string?)pt?["text"] ?? "")),
            _ => ""
        };
        return (role, text);
    }).Where(x => !string.IsNullOrEmpty(x.Item2)).ToList(),
    _ => new List<(string, string)>()
};

async Task<JsonObject> CollectResponses(IAsyncEnumerable<TraeSseEvent> upstream, string model, string respId, CancellationToken ct)
{
    var result = await TraeChatResult.CollectAsync(upstream, ct);
    string msgId = $"msg_{Guid.NewGuid():N}";
    return new JsonObject
    {
        ["id"] = respId,
        ["object"] = "response",
        ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["status"] = "completed",
        ["model"] = model,
        ["output"] = new JsonArray(new JsonObject
        {
            ["id"] = msgId,
            ["type"] = "message",
            ["status"] = "completed",
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "output_text",
                ["text"] = result.Text,
                ["annotations"] = new JsonArray()
            })
        }),
        ["usage"] = new JsonObject
        {
            ["input_tokens"] = result.PromptTokens,
            ["output_tokens"] = result.CompletionTokens,
            ["total_tokens"] = result.TotalTokens
        }
    };
}

string OpenAiFinishReason(string reason) => reason switch
{
    "max_tokens" or "length" => "length",
    "content_filter" => "content_filter",
    "tool_use" or "tool_calls" => "tool_calls",
    _ => "stop"
};

string AnthropicStopReason(string reason) => reason switch
{
    "max_tokens" or "length" => "max_tokens",
    "tool_use" or "tool_calls" => "tool_use",
    _ => "end_turn"
};

async Task WriteResponsesStream(Stream w, IAsyncEnumerable<TraeSseEvent> upstream, string model, string respId, CancellationToken ct)
{
    string msgId = $"msg_{Guid.NewGuid():N}";
    int sequenceNumber = 0;
    bool wroteAnything = false;

    async ValueTask WriteRaw(string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await w.WriteAsync(bytes, cancellationToken);
        await w.FlushAsync(cancellationToken);
        wroteAnything = true;
    }

    async Task WriteEvent(string ev, JsonNode data)
    {
        if (data is JsonObject payload && !payload.ContainsKey("sequence_number"))
            payload["sequence_number"] = sequenceNumber++;
        await WriteRaw($"event: {ev}\ndata: {data.ToJsonString()}\n\n", ct);
    }

    var baseResponse = new JsonObject
    {
        ["id"] = respId,
        ["object"] = "response",
        ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["status"] = "in_progress",
        ["model"] = model,
        ["output"] = new JsonArray()
    };
    bool responseStarted = false;
    async Task StartResponse()
    {
        if (responseStarted) return;
        responseStarted = true;
        await WriteEvent("response.created", new JsonObject { ["type"] = "response.created", ["response"] = baseResponse.DeepClone() });
        await WriteEvent("response.in_progress", new JsonObject { ["type"] = "response.in_progress", ["response"] = baseResponse.DeepClone() });
        await WriteEvent("response.output_item.added", new JsonObject
        {
            ["type"] = "response.output_item.added",
            ["output_index"] = 0,
            ["item"] = new JsonObject { ["id"] = msgId, ["type"] = "message", ["status"] = "in_progress", ["role"] = "assistant", ["content"] = new JsonArray() }
        });
        await WriteEvent("response.content_part.added", new JsonObject
        {
            ["type"] = "response.content_part.added",
            ["item_id"] = msgId, ["output_index"] = 0, ["content_index"] = 0,
            ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() }
        });
    }

    var text = new StringBuilder();
    int promptTokens = 0, completionTokens = 0, totalTokens = 0;
    bool sawContent = false;
    try
    {
        await WriteRaw(": keep-alive\n\n", ct);
        await foreach (var ev in TraeStreamHeartbeat.ReadAsync(
            upstream,
            cancellationToken => WriteRaw(": keep-alive\n\n", cancellationToken),
            cancellationToken: ct))
        {
            var j = JsonNode.Parse(ev.Data) as JsonObject;
            if (ev.Event == "output" && j != null)
            {
                string? t = (string?)j["response"];
                if (!string.IsNullOrEmpty(t))
                {
                    await StartResponse();
                    sawContent = true;
                    text.Append(t);
                    await WriteEvent("response.output_text.delta", new JsonObject
                    {
                        ["type"] = "response.output_text.delta",
                        ["item_id"] = msgId, ["output_index"] = 0, ["content_index"] = 0,
                        ["delta"] = t
                    });
                }
            }
            else if (ev.Event == "token_usage" && j != null)
            {
                promptTokens = (int?)j["prompt_tokens"] ?? promptTokens;
                completionTokens = (int?)j["completion_tokens"] ?? completionTokens;
                totalTokens = (int?)j["total_tokens"] ?? totalTokens;
            }
            else if (ev.Event == "done")
            {
                if (!sawContent) throw new TraeUpstreamException("Trae 上游完成但未返回有效内容。");
                if (totalTokens == 0 && (promptTokens > 0 || completionTokens > 0))
                    totalTokens = promptTokens + completionTokens;
                await WriteEvent("response.output_text.done", new JsonObject
                {
                    ["type"] = "response.output_text.done",
                    ["item_id"] = msgId, ["output_index"] = 0, ["content_index"] = 0,
                    ["text"] = text.ToString()
                });
                await WriteEvent("response.content_part.done", new JsonObject
                {
                    ["type"] = "response.content_part.done",
                    ["item_id"] = msgId, ["output_index"] = 0, ["content_index"] = 0,
                    ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = text.ToString(), ["annotations"] = new JsonArray() }
                });
                await WriteEvent("response.output_item.done", new JsonObject
                {
                    ["type"] = "response.output_item.done",
                    ["output_index"] = 0,
                    ["item"] = new JsonObject
                    {
                        ["id"] = msgId, ["type"] = "message", ["status"] = "completed", ["role"] = "assistant",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text.ToString(), ["annotations"] = new JsonArray() })
                    }
                });
                var completed = baseResponse.DeepClone()!.AsObject();
                completed["status"] = "completed";
                completed["output"] = new JsonArray(new JsonObject
                {
                    ["id"] = msgId, ["type"] = "message", ["status"] = "completed", ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text.ToString(), ["annotations"] = new JsonArray() })
                });
                completed["usage"] = new JsonObject
                {
                    ["input_tokens"] = promptTokens,
                    ["output_tokens"] = completionTokens,
                    ["total_tokens"] = totalTokens
                };
                await WriteEvent("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = completed });
                return;
            }
        }
        throw new TraeIncompleteStreamException();
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception) when (wroteAnything)
    {
        var failed = baseResponse.DeepClone()!.AsObject();
        failed["status"] = "failed";
        failed["error"] = new JsonObject
        {
            ["code"] = "upstream_incomplete_response",
            ["message"] = "Upstream stream failed before completion."
        };
        await WriteEvent("response.failed", new JsonObject
        {
            ["type"] = "response.failed",
            ["response"] = failed
        });
    }
}
