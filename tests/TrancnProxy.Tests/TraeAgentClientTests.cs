using System.Net;
using System.Text;
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

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        public bool WasCalled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            Path = request.RequestUri!.AbsolutePath;
            Method = request.Method;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream")
            };
        }
    }
}