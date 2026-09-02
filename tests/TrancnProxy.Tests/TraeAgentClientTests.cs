using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrancnProxy.Agent;

namespace TrancnProxy.Tests;

[TestClass]
public sealed class TraeAgentClientTests
{
    [TestMethod]
    public async Task CreateAgentTaskStreamAsync_UsesConfirmedPathAndFramesSse()
    {
        var handler = new RecordingHandler("event: task_created\ndata: {\"task_id\":\"task-1\"}\n\nevent: turn_completion\ndata: {\"status\":\"done\"}\n\n");
        var upstream = new TraeClient(new TraeAuthData { Token = "test-token", ApiHost = "https://upstream.example" }, httpMessageHandler: handler);
        var client = new TraeAgentClient(upstream);
        var frames = new List<TraeAgentSseFrame>();

        await foreach (var frame in client.CreateAgentTaskStreamAsync(new TraeAgentTaskRequest("session-from-upstream", new())))
            frames.Add(frame);

        handler.Path.Should().Be("/api/cue_agent/v3/create_agent_task");
        handler.Method.Should().Be(HttpMethod.Post);
        handler.Body.Should().Contain("\"session_id\":\"session-from-upstream\"");
        handler.Authorization.Should().Be("Cloud-IDE-JWT test-token");
        frames.Should().Equal(
            new TraeAgentSseFrame("task_created", "{\"task_id\":\"task-1\"}"),
            new TraeAgentSseFrame("turn_completion", "{\"status\":\"done\"}"));
    }

    [TestMethod]
    public async Task CreateAgentTaskStreamAsync_DoesNotAllowCallerToReplaceSessionId()
    {
        var handler = new RecordingHandler("");
        var upstream = new TraeClient(new TraeAuthData { Token = "test-token", ApiHost = "https://upstream.example" }, httpMessageHandler: handler);
        var client = new TraeAgentClient(upstream);

        Func<Task> stream = async () =>
        {
            await foreach (var _ in client.CreateAgentTaskStreamAsync(new TraeAgentTaskRequest("session-a", new() { ["session_id"] = "session-b" })))
            {
            }
        };

        await stream.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*server-issued session ID*");
        handler.WasCalled.Should().BeFalse();
    }

    [TestMethod]
    public async Task SessionRunner_RequiresMatchingModelBeforeOutputAndTerminalEvent()
    {
        var handler = new RecordingHandler("event: model_config\ndata: {\"model_name\":\"glm-5.3__dev\"}\n\nevent: thought\ndata: {\"content\":\"working\"}\n\nevent: turn_completion\ndata: {\"status\":\"done\"}\n\n");
        var upstream = new TraeClient(new TraeAuthData { Token = "test-token", ApiHost = "https://upstream.example" }, httpMessageHandler: handler);
        var runner = new TraeAgentSessionRunner(new TraeAgentClient(upstream));
        var events = new List<TraeAgentStreamEvent>();

        await foreach (var streamEvent in runner.RunAsync(
            new TraeAgentSession("session-from-upstream", "glm-5.3__dev"), new()))
            events.Add(streamEvent);

        events.Select(streamEvent => streamEvent.Event).Should().Equal("model_config", "thought", "turn_completion");
    }

    [TestMethod]
    public async Task SessionRunner_RejectsOutputBeforeModelConfirmation()
    {
        var handler = new RecordingHandler("event: thought\ndata: {\"content\":\"working\"}\n\n");
        var upstream = new TraeClient(new TraeAuthData { Token = "test-token", ApiHost = "https://upstream.example" }, httpMessageHandler: handler);
        var runner = new TraeAgentSessionRunner(new TraeAgentClient(upstream));

        Func<Task> run = async () =>
        {
            await foreach (var _ in runner.RunAsync(new TraeAgentSession("session-from-upstream", "glm-5.3__dev"), new()))
            {
            }
        };

        await run.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*before confirming the actual model*");
    }

    [TestMethod]
    public async Task ChatStreamAsync_UsesConfiguredChatApiHost()
    {
        var handler = new RecordingHandler("event: metadata\ndata: {\"model\":\"glm-5.3__dev\"}\n\nevent: output\ndata: {\"response\":\"ok\"}\n\nevent: done\ndata: {\"finish_reason\":\"stop\"}\n\n");
        var client = new TraeClient(
            new TraeAuthData { Token = "test-token", ApiHost = "https://console.example" },
            httpMessageHandler: handler,
            chatApiHost: "https://chat.example/");

        var events = new List<TraeSseEvent>();
        await foreach (var streamEvent in client.ChatStreamAsync(new[] { ("user", "ping") }, "glm-5.3__dev"))
            events.Add(streamEvent);

        handler.Host.Should().Be("chat.example");
        handler.Path.Should().Be("/api/agent/v3/llm_utils_chat");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("function").GetString().Should().Be("solo_work_lite");
        body.RootElement.GetProperty("config_name").GetString().Should().Be("glm-5.3__dev");
        body.RootElement.GetProperty("request_id").GetString().Should().Be(body.RootElement.GetProperty("session_id").GetString());
        body.RootElement.TryGetProperty("app_version_code", out _).Should().BeFalse();
        handler.Headers["x-ide-token"].Should().Be("test-token");
        handler.Headers["x-device-type"].Should().Be("windows");
        handler.Headers["x-ide-version"].Should().Be("0.1.43");
        handler.Headers["x-ide-version-code"].Should().Be("20260716");
        events.Select(streamEvent => streamEvent.Event).Should().Equal("metadata", "output", "done");
    }

    [TestMethod]
    public async Task ChatStreamAsync_UsesEnterpriseChatChannelWithConfigName()
    {
        var handler = new RecordingHandler("event: metadata\ndata: {\"model\":\"glm-5.3\"}\n\nevent: output\ndata: {\"response\":\"ok\"}\n\nevent: done\ndata: {\"finish_reason\":\"stop\"}\n\n");
        var client = new TraeClient(
            new TraeAuthData { Token = "test-token", ApiHost = "https://console.example" },
            httpMessageHandler: handler);

        var events = new List<TraeSseEvent>();
        await foreach (var streamEvent in client.ChatStreamAsync(
            new[] { ("user", "ping") },
            new TraeModelDescriptor("glm-5.3__dev", "glm-5.3", "GLM-5.3", TraeModelVariant.Dev)))
            events.Add(streamEvent);

        handler.Host.Should().Be("console.example");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("function").GetString().Should().Be("chat_v3");
        body.RootElement.GetProperty("model").GetString().Should().Be("glm-5.3__dev");
        body.RootElement.GetProperty("config_name").GetString().Should().Be("glm-5.3");
        events.Select(streamEvent => streamEvent.Event).Should().Equal("metadata", "output", "done");
    }

    [TestMethod]
    public async Task ChatStreamAsync_SendsEffortAndMaxModelTuning()
    {
        var handler = new RecordingHandler("event: metadata\ndata: {\"model\":\"deepseek-v4-flash__max\"}\n\nevent: output\ndata: {\"response\":\"ok\"}\n\nevent: done\ndata: {\"finish_reason\":\"stop\"}\n\n");
        var client = new TraeClient(
            new TraeAuthData { Token = "test-token", ApiHost = "https://console.example" },
            httpMessageHandler: handler);
        var model = new TraeModelDescriptor(
            "deepseek-v4-flash__max",
            "DeepSeek-V4-Flash",
            "DeepSeek-V4-Flash",
            TraeModelVariant.Max,
            112000,
            200000);

        await foreach (var _ in client.ChatStreamAsync(
            [("user", "ping")],
            model,
            new TraeChatTuning(TraeReasoningEffort.ExtraHigh)))
        {
        }

        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("reasoning_effort_level").GetString().Should().Be("extra_high");
        body.RootElement.GetProperty("model_auto_selection").GetProperty("strategy").GetString().Should().Be("max");
        body.RootElement.GetProperty("context_window_size").GetInt32().Should().Be(200000);
    }

    [TestMethod]
    public void ExtraChatFields_CannotOverrideReservedFields()
    {
        var body = new JsonObject { ["model"] = "expected" };

        Action merge = () => TraeClient.ApplyExtraChatFields(body, "{\"model\":\"wrong\"}");

        merge.Should().Throw<InvalidDataException>().WithMessage("*reserved field 'model'*");
        body["model"]!.GetValue<string>().Should().Be("expected");
    }

    [TestMethod]
    public void ExtraChatFields_RequiresJsonObject()
    {
        Action merge = () => TraeClient.ApplyExtraChatFields(new JsonObject(), "[]");

        merge.Should().Throw<InvalidDataException>().WithMessage("*JSON object*");
    }

    [TestMethod]
    public async Task ChatStreamAsync_AcceptsProviderInternalModelName()
    {
        var handler = new RecordingHandler("event: metadata\ndata: {\"model\":\"ali-deepseek-v4-pro-0813\"}\n\nevent: output\ndata: {\"response\":\"ok\"}\n\nevent: done\ndata: {\"finish_reason\":\"stop\"}\n\n");
        var client = new TraeClient(
            new TraeAuthData { Token = "test-token", ApiHost = "https://console.example" },
            httpMessageHandler: handler);

        var events = new List<TraeSseEvent>();
        await foreach (var streamEvent in client.ChatStreamAsync(
            new[] { ("user", "ping") },
            new TraeModelDescriptor("DeepSeek-V4-Pro-Official__dev", "DeepSeek-V4-Pro-Official", "DeepSeek-V4-Pro 正式版", TraeModelVariant.Dev)))
            events.Add(streamEvent);

        events.Select(streamEvent => streamEvent.Event).Should().Equal("metadata", "output", "done");
    }

    [TestMethod]
    public async Task ChatStreamAsync_RejectsSilentModelDowngrade()
    {
        var handler = new RecordingHandler("event: metadata\ndata: {\"model\":\"Doubao-Seed-Evolving\"}\n\nevent: output\ndata: {\"response\":\"ok\"}\n\n");
        var client = new TraeClient(
            new TraeAuthData { Token = "test-token", ApiHost = "https://console.example" },
            httpMessageHandler: handler);

        Func<Task> stream = async () =>
        {
            await foreach (var _ in client.ChatStreamAsync(
                new[] { ("user", "ping") },
                new TraeModelDescriptor("glm-5.3__dev", "glm-5.3", "GLM-5.3", TraeModelVariant.Dev)))
            {
            }
        };

        await stream.Should().ThrowAsync<TraeModelSelectionException>();
    }

    [TestMethod]
    public async Task ChatStreamAsync_RejectsQuietVersionDowngrade()
    {
        var handler = new RecordingHandler("event: metadata\ndata: {\"model\":\"glm-5\"}\n\nevent: output\ndata: {\"response\":\"ok\"}\n\n");
        var client = new TraeClient(
            new TraeAuthData { Token = "test-token", ApiHost = "https://console.example" },
            httpMessageHandler: handler);

        Func<Task> stream = async () =>
        {
            await foreach (var _ in client.ChatStreamAsync(
                new[] { ("user", "ping") },
                new TraeModelDescriptor("glm-5.3__dev", "glm-5.3", "GLM-5.3", TraeModelVariant.Dev)))
            {
            }
        };

        await stream.Should().ThrowAsync<TraeModelSelectionException>();
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public string? Host { get; private set; }
        public string? Path { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool WasCalled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            Host = request.RequestUri!.Host;
            Path = request.RequestUri!.AbsolutePath;
            Method = request.Method;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            foreach (var header in request.Headers)
                Headers[header.Key] = string.Join(',', header.Value);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream")
            };
        }
    }
}