using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TrancnProxy;

var argsList = args.ToList();
bool forceLogin = argsList.Remove("--login");
bool webLogin = argsList.Remove("--weblogin");
bool testMode = argsList.Remove("--test");
int port = 9220;
string listen = "127.0.0.1";
string? gatewayKey = Environment.GetEnvironmentVariable("TRANCN_API_KEY");
string testModel = "glm-5.2__max";
for (int i = 0; i < argsList.Count; i++)
{
    if (argsList[i] == "--port" && i + 1 < argsList.Count) port = int.Parse(argsList[++i]);
    else if (argsList[i] == "--listen" && i + 1 < argsList.Count) listen = argsList[++i];
    else if (argsList[i] == "--api-key" && i + 1 < argsList.Count) gatewayKey = argsList[++i];
    else if (argsList[i] == "--model" && i + 1 < argsList.Count) testModel = argsList[++i];
}

if (argsList.Remove("--tc-test"))
{
    string enc = TcCrypto.EncryptStorageValue("{\"hello\":\"世界\"}");
    Console.WriteLine("ENC:" + enc);
    Console.WriteLine("DEC:" + TcCrypto.DecryptStorageValue(enc));
    return 0;
}

Console.WriteLine("=== trancn-proxy : Trae CN 企业版 -> OpenAI/Anthropic 兼容代理 ===");
Console.WriteLine($"数据目录: {TraeAuthStore.DataDir}");

// ---------- 1. 引导授权 ----------
TraeAuthData auth = new();
if (!forceLogin && !webLogin)
{
    auth = TraeAuthStore.ReadCache() ?? new TraeAuthData();
    if (string.IsNullOrEmpty(auth.Token))
    {
        try
        {
            Console.WriteLine("缓存无授权,从 Trae CN 本地数据解密 ...");
            auth = TraeAuthStore.ReadFromStorage();
            TraeAuthStore.SaveCache(auth);
            Console.WriteLine($"已解密并缓存(用户: {auth.Username}/{auth.Email ?? auth.UserId})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"本地解密失败: {ex.Message}");
            auth = new TraeAuthData();
        }
    }
    else
    {
        Console.WriteLine($"使用缓存授权(用户: {auth.Username}/{auth.Email ?? auth.UserId})");
    }
}
else
{
    Console.WriteLine(webLogin ? "--weblogin: 强制网页授权(不依赖 Trae CN IDE)" : "--login: 强制重新读取 IDE 本地授权");
}

var (deviceId, machineId) = TraeAuthStore.ReadDeviceIds();
var client = new TraeClient(auth, deviceId, machineId);
Console.WriteLine($"上游: {client.ApiHost}  设备ID: {deviceId[..Math.Min(4, deviceId.Length)]}***");

if (!webLogin && (string.IsNullOrEmpty(auth.Token) || forceLogin))
{
    try { auth = TraeAuthStore.ReadFromStorage(); client = new TraeClient(auth, deviceId, machineId); }
    catch { auth = new TraeAuthData(); }
}

if (string.IsNullOrEmpty(auth.Token) || !await client.ValidateTokenAsync())
{
    if (!string.IsNullOrEmpty(auth.RefreshToken) &&
        (auth.RefreshExpiredAt is null || DateTimeOffset.UtcNow < auth.RefreshExpiredAt))
    {
        try
        {
            Console.WriteLine("token 无效,尝试 refreshToken 续期 ...");
            await client.ExchangeTokenAsync();
            auth.TokenReleaseAt = DateTimeOffset.UtcNow;
            TraeAuthStore.SaveCache(auth);
            if (!auth.Standalone)
            {
                try { TraeAuthStore.WriteBackToStorage(auth); Console.WriteLine("已回写 storage.json"); }
                catch (Exception ex) { Console.WriteLine($"回写失败: {ex.Message}"); }
            }
            Console.WriteLine($"续期成功,新过期时间: {auth.ExpiredAt}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"续期失败: {ex.Message}");
            auth = new TraeAuthData();
        }
    }

    if (string.IsNullOrEmpty(auth.Token) || !await client.ValidateTokenAsync())
    {
        try
        {
            auth = await StandaloneLogin.LoginAsync(client, machineId, deviceId);
            client = new TraeClient(auth, deviceId, machineId);
            TraeAuthStore.SaveCache(auth);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"授权失败: {ex.Message}");
            return 1;
        }
    }
}

Console.WriteLine($"授权就绪: {auth.Username ?? auth.UserId}  token过期: {auth.ExpiredAt:yyyy-MM-dd HH:mm}Z  refresh过期: {auth.RefreshExpiredAt:yyyy-MM-dd}");

// ---------- 2. 自测模式 ----------
if (testMode)
{
    Console.WriteLine($"--- 自测:向 {testModel} 发送消息 ---");
    var sb = new StringBuilder();
    await foreach (var ev in client.ChatStreamAsync(new[] { ("user", "请只回复四个字:验证成功") }, testModel))
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
    }
    Console.WriteLine();
    Console.WriteLine(sb.Length > 0 ? "自测通过 ✔" : "自测失败:未收到回复 ✘");
    return sb.Length > 0 ? 0 : 1;
}

// ---------- 3. API 服务 ----------
var builder = WebApplication.CreateSlimBuilder(argsList.ToArray());
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.TimestampFormat = "HH:mm:ss "; o.SingleLine = true; });
builder.Services.AddSingleton(auth);
builder.Services.AddSingleton(client);

var app = builder.Build();
app.Urls.Add($"http://{listen}:{port}");

app.Use(async (ctx, next) =>
{
    if (!string.IsNullOrEmpty(gatewayKey) && ctx.Request.Path.StartsWithSegments("/v1"))
    {
        string? given = ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "").Trim();
        given = string.IsNullOrEmpty(given) ? ctx.Request.Headers["x-api-key"].ToString().Trim() : given;
        if (given != gatewayKey)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = new { message = "invalid gateway api key", type = "authentication_error" } });
            return;
        }
    }
    await next();
});

app.MapGet("/v1/status", () => new
{
    ok = true,
    user = auth.Username ?? auth.UserId,
    api_host = client.ApiHost,
    token_expires = auth.ExpiredAt,
    refresh_expires = auth.RefreshExpiredAt
});

app.MapGet("/v1/models", async (CancellationToken ct) =>
{
    var catalog = await client.GetModelCatalogAsync(ct: ct);
    var models = new JsonArray();
    foreach (var fc in catalog["function_configs"]?.AsArray() ?? new JsonArray())
    {
        foreach (var cfg in fc?["config_info_list"]?.AsArray() ?? new JsonArray())
        {
            string name = (string?)cfg?["config_name"] ?? "";
            string display = (string?)cfg?["display_config"]?["display_name"] ?? name;
            if (string.IsNullOrEmpty(name) || name.StartsWith("custom_model_") ||
                name is "summary" or "fast_apply" or "fast_apply_new" or "title_generation" or "input_optimization")
                continue;
            models.Add(new JsonObject
            {
                ["id"] = name,
                ["display_name"] = display,
                ["owned_by"] = "trae"
            });
        }
    }
    return Results.Json(new JsonObject { ["object"] = "list", ["data"] = models });
});

app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
{
    var ct = ctx.RequestAborted;
    var body = JsonNode.Parse(await new StreamReader(ctx.Request.Body).ReadToEndAsync(ct))!.AsObject();
    string model = (string?)body["model"] ?? "glm-5.2__max";
    bool stream = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) && sb;
    var messages = ConvertOpenAIMessages(body["messages"]?.AsArray());
    if (messages.Count == 0)
        return Results.BadRequest(new { error = new { message = "messages is required", type = "invalid_request_error" } });

    var upstream = client.ChatStreamAsync(messages, model, ct);
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
    string model = (string?)body["model"] ?? "glm-5.2__max";
    bool stream = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) && sb;
    var messages = ConvertResponsesInput(body["input"]);
    if (messages.Count == 0)
        return Results.BadRequest(new { error = new { message = "input is required", type = "invalid_request_error" } });

    var upstream = client.ChatStreamAsync(messages, model, ct);
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
    string model = (string?)body["model"] ?? "glm-5.2__max";
    bool stream = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) && sb;
    var messages = ConvertAnthropicMessages(body);
    if (messages.Count == 0)
        return Results.BadRequest(new { type = "error", error = new { message = "messages is required", type = "invalid_request_error" } });

    var upstream = client.ChatStreamAsync(messages, model, ct);
    if (stream)
    {
        ctx.Response.ContentType = "text/event-stream";
        await WriteAnthropicStream(ctx.Response.Body, upstream, model, ct);
        return Results.Empty;
    }
    return Results.Json(await CollectAnthropic(upstream, model, ct));
});

Console.WriteLine();
Console.WriteLine($"API 服务: http://{listen}:{port}");
Console.WriteLine("  GET  /v1/status");
Console.WriteLine("  GET  /v1/models");
Console.WriteLine("  POST /v1/chat/completions   (OpenAI 格式)");
Console.WriteLine("  POST /v1/messages           (Anthropic 格式)");
Console.WriteLine($"网关 Key: {(string.IsNullOrEmpty(gatewayKey) ? "(未设置,仅本机访问)" : "已启用")}");
Console.WriteLine();

var refresh = new TokenRefreshService(auth, client);
using var cts = new CancellationTokenSource();
_ = refresh.StartAsync(cts.Token);

await app.RunAsync();
cts.Cancel();
return 0;

// ==================== helpers ====================

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

async Task<JsonObject> CollectOpenAI(IAsyncEnumerable<TraeSseEvent> upstream, string model, CancellationToken ct)
{
    var content = new StringBuilder();
    int promptTokens = 0, completionTokens = 0, totalTokens = 0;
    string finishReason = "stop";
    await foreach (var ev in upstream)
    {
        var j = JsonNode.Parse(ev.Data) as JsonObject;
        if (ev.Event == "output" && j != null)
        {
            content.Append((string?)j["response"] ?? "");
            if (j["finish_reason"] is JsonValue fr && fr.TryGetValue<string>(out var f)) finishReason = f;
        }
        else if (ev.Event == "token_usage" && j != null)
        {
            promptTokens = (int?)j["prompt_tokens"] ?? promptTokens;
            completionTokens = (int?)j["completion_tokens"] ?? completionTokens;
            totalTokens = (int?)j["total_tokens"] ?? totalTokens;
        }
        else if (ev.Event == "done" && j?["finish_reason"] is JsonValue dfr && dfr.TryGetValue<string>(out var df))
            finishReason = df;
    }
    if (content.Length == 0) content.Append("(上游返回空内容)");
    if (totalTokens == 0) { completionTokens = Math.Max(1, content.Length / 4); totalTokens = promptTokens + completionTokens; }
    return new JsonObject
    {
        ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
        ["object"] = "chat.completion",
        ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["model"] = model,
        ["choices"] = new JsonArray(new JsonObject
        {
            ["index"] = 0,
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content.ToString() },
            ["finish_reason"] = finishReason
        }),
        ["usage"] = new JsonObject
        {
            ["prompt_tokens"] = promptTokens,
            ["completion_tokens"] = completionTokens,
            ["total_tokens"] = totalTokens
        }
    };
}

async Task WriteOpenAIStream(Stream w, IAsyncEnumerable<TraeSseEvent> upstream, string model, CancellationToken ct)
{
    string id = $"chatcmpl-{Guid.NewGuid():N}";
    int promptTokens = 0, completionTokens = 0, totalTokens = 0;
    bool sawContent = false;

    static async Task Send(Stream w, string data, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");
        await w.WriteAsync(bytes, ct);
        await w.FlushAsync(ct);
    }

    await foreach (var ev in upstream)
    {
        var j = JsonNode.Parse(ev.Data) as JsonObject;
        if (ev.Event == "output" && j != null)
        {
            string? text = (string?)j["response"];
            if (!string.IsNullOrEmpty(text))
            {
                sawContent = true;
                await Send(w, new JsonObject
                {
                    ["id"] = id,
                    ["object"] = "chat.completion.chunk",
                    ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["model"] = model,
                    ["choices"] = new JsonArray(new JsonObject
                    {
                        ["index"] = 0,
                        ["delta"] = new JsonObject { ["content"] = text },
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
            if (!sawContent)
                await Send(w, new JsonObject
                {
                    ["id"] = id,
                    ["object"] = "chat.completion.chunk",
                    ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["model"] = model,
                    ["choices"] = new JsonArray(new JsonObject
                    {
                        ["index"] = 0,
                        ["delta"] = new JsonObject { ["content"] = "(上游返回空内容)" },
                        ["finish_reason"] = (JsonNode?)null
                    })
                }.ToJsonString(), ct);
            await Send(w, new JsonObject
            {
                ["id"] = id,
                ["object"] = "chat.completion.chunk",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = model,
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject(),
                    ["finish_reason"] = "stop"
                }),
                ["usage"] = totalTokens > 0 ? new JsonObject
                {
                    ["prompt_tokens"] = promptTokens,
                    ["completion_tokens"] = completionTokens,
                    ["total_tokens"] = totalTokens
                } : null
            }.ToJsonString(), ct);
            await Send(w, "[DONE]", ct);
            return;
        }
    }
    await Send(w, "[DONE]", ct);
}

async Task<JsonObject> CollectAnthropic(IAsyncEnumerable<TraeSseEvent> upstream, string model, CancellationToken ct)
{
    var text = new StringBuilder();
    int input = 0, output = 0;
    await foreach (var ev in upstream)
    {
        var j = JsonNode.Parse(ev.Data) as JsonObject;
        if (ev.Event == "output" && j != null)
        {
            text.Append((string?)j["response"] ?? "");
            text.Append((string?)j["reasoning_content"] ?? "");
        }
        else if (ev.Event == "token_usage" && j != null)
        {
            input = (int?)j["prompt_tokens"] ?? input;
            output = (int?)j["completion_tokens"] ?? output;
        }
    }
    if (text.Length == 0) text.Append("(上游返回空内容)");
    if (output == 0) output = Math.Max(1, text.Length / 4);
    return new JsonObject
    {
        ["id"] = $"msg_{Guid.NewGuid():N}",
        ["type"] = "message",
        ["role"] = "assistant",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text.ToString() }),
        ["model"] = model,
        ["stop_reason"] = "end_turn",
        ["stop_sequence"] = null,
        ["usage"] = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output }
    };
}

async Task WriteAnthropicStream(Stream w, IAsyncEnumerable<TraeSseEvent> upstream, string model, CancellationToken ct)
{
    string msgId = $"msg_{Guid.NewGuid():N}";

    async Task WriteEvent(string ev, JsonNode data)
    {
        var bytes = Encoding.UTF8.GetBytes($"event: {ev}\ndata: {data.ToJsonString()}\n\n");
        await w.WriteAsync(bytes, ct);
        await w.FlushAsync(ct);
    }

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

    bool blockOpen = false;
    int output = 0;
    await foreach (var ev in upstream)
    {
        var j = JsonNode.Parse(ev.Data) as JsonObject;
        if (ev.Event == "output" && j != null)
        {
            string? text = (string?)j["response"];
            if (!string.IsNullOrEmpty(text))
            {
                if (!blockOpen)
                {
                    blockOpen = true;
                    await WriteEvent("content_block_start", new JsonObject
                    {
                        ["type"] = "content_block_start", ["index"] = 0,
                        ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" }
                    });
                }
                output += text.Length;
                await WriteEvent("content_block_delta", new JsonObject
                {
                    ["type"] = "content_block_delta", ["index"] = 0,
                    ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text }
                });
            }
        }
        else if (ev.Event == "done")
        {
            if (blockOpen)
                await WriteEvent("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 });
            await WriteEvent("message_delta", new JsonObject
            {
                ["type"] = "message_delta",
                ["delta"] = new JsonObject { ["stop_reason"] = "end_turn", ["stop_sequence"] = null },
                ["usage"] = new JsonObject { ["output_tokens"] = Math.Max(1, output / 4) }
            });
            await WriteEvent("message_stop", new JsonObject { ["type"] = "message_stop" });
            return;
        }
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
    var text = new StringBuilder();
    int promptTokens = 0, completionTokens = 0, totalTokens = 0;
    await foreach (var ev in upstream)
    {
        var j = JsonNode.Parse(ev.Data) as JsonObject;
        if (ev.Event == "output" && j != null)
            text.Append((string?)j["response"] ?? "");
        else if (ev.Event == "token_usage" && j != null)
        {
            promptTokens = (int?)j["prompt_tokens"] ?? promptTokens;
            completionTokens = (int?)j["completion_tokens"] ?? completionTokens;
            totalTokens = (int?)j["total_tokens"] ?? totalTokens;
        }
    }
    if (text.Length == 0) text.Append("(上游返回空内容)");
    if (totalTokens == 0) { completionTokens = Math.Max(1, text.Length / 4); totalTokens = promptTokens + completionTokens; }
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
                ["text"] = text.ToString(),
                ["annotations"] = new JsonArray()
            })
        }),
        ["usage"] = new JsonObject
        {
            ["input_tokens"] = promptTokens,
            ["output_tokens"] = completionTokens,
            ["total_tokens"] = totalTokens
        }
    };
}

async Task WriteResponsesStream(Stream w, IAsyncEnumerable<TraeSseEvent> upstream, string model, string respId, CancellationToken ct)
{
    string msgId = $"msg_{Guid.NewGuid():N}";

    async Task WriteEvent(string ev, JsonNode data)
    {
        var bytes = Encoding.UTF8.GetBytes($"event: {ev}\ndata: {data.ToJsonString()}\n\n");
        await w.WriteAsync(bytes, ct);
        await w.FlushAsync(ct);
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

    var text = new StringBuilder();
    int promptTokens = 0, completionTokens = 0, totalTokens = 0;
    bool sawContent = false;
    await foreach (var ev in upstream)
    {
        var j = JsonNode.Parse(ev.Data) as JsonObject;
        if (ev.Event == "output" && j != null)
        {
            string? t = (string?)j["response"];
            if (!string.IsNullOrEmpty(t))
            {
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
            if (!sawContent)
            {
                sawContent = true;
                text.Append("(上游返回空内容)");
                await WriteEvent("response.output_text.delta", new JsonObject
                {
                    ["type"] = "response.output_text.delta",
                    ["item_id"] = msgId, ["output_index"] = 0, ["content_index"] = 0,
                    ["delta"] = "(上游返回空内容)"
                });
            }
            if (totalTokens == 0) { completionTokens = Math.Max(1, text.Length / 4); totalTokens = promptTokens + completionTokens; }
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
}
