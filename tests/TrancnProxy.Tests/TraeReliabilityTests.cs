using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TrancnProxy.Tests;

[TestClass]
public sealed class TraeReliabilityTests
{
    [TestMethod]
    public async Task ChatStreamAsync_ParsesMultilineDataAndRequiresDone()
    {
        var handler = new StaticResponseHandler(
            ": upstream keepalive\r\n" +
            "event: metadata\r\ndata: {\"model\":\"glm-5.3__dev\"}\r\n\r\n" +
            "event: output\r\ndata: {\"response\":\r\ndata: \"ok\"}\r\n\r\n" +
            "event: done\r\ndata: {\"finish_reason\":\"stop\"}\r\n\r\n");
        var client = CreateClient(handler);
        var events = new List<TraeSseEvent>();

        await foreach (var streamEvent in client.ChatStreamAsync(new[] { ("user", "ping") }, "glm-5.3__dev"))
            events.Add(streamEvent);

        events.Select(streamEvent => streamEvent.Event).Should().Equal("metadata", "output", "done");
        events[1].Data.Should().Be("{\"response\":\n\"ok\"}");
    }

    [TestMethod]
    public async Task ChatStreamAsync_RejectsEofBeforeDone()
    {
        var client = CreateClient(new StaticResponseHandler(
            "event: metadata\ndata: {\"model\":\"glm-5.3__dev\"}\n\n" +
            "event: output\ndata: {\"response\":\"partial\"}\n\n"));

        Func<Task> consume = async () =>
        {
            await foreach (var _ in client.ChatStreamAsync(new[] { ("user", "ping") }, "glm-5.3__dev"))
            {
            }
        };

        await consume.Should().ThrowAsync<TraeIncompleteStreamException>();
    }

    [TestMethod]
    public async Task ChatStreamAsync_PropagatesErrorEvent()
    {
        var client = CreateClient(new StaticResponseHandler(
            "event: metadata\ndata: {\"model\":\"glm-5.3__dev\"}\n\n" +
            "event: error\ndata: {\"message\":\"capacity exhausted\"}\n\n"));

        Func<Task> consume = async () =>
        {
            await foreach (var _ in client.ChatStreamAsync(new[] { ("user", "ping") }, "glm-5.3__dev"))
            {
            }
        };

        await consume.Should().ThrowAsync<TraeUpstreamException>()
            .WithMessage("capacity exhausted");
    }

    [TestMethod]
    public async Task ChatResult_UsesRealUsageAndFinishReason()
    {
        var events = Events(
            new TraeSseEvent("metadata", "{}"),
            new TraeSseEvent("output", "{\"response\":\"hello\"}"),
            new TraeSseEvent("token_usage", "{\"prompt_tokens\":12,\"completion_tokens\":3,\"total_tokens\":15}"),
            new TraeSseEvent("done", "{\"finish_reason\":\"length\"}"));

        var result = await TraeChatResult.CollectAsync(events);

        result.Should().Be(new TraeChatResult("hello", 12, 3, 15, "length"));
    }

    [TestMethod]
    public async Task ChatResult_CollectsReasoningSeparatelyFromVisibleText()
    {
        var events = Events(
            new TraeSseEvent("metadata", "{}"),
            new TraeSseEvent("output", "{\"reasoning_content\":\"first \"}"),
            new TraeSseEvent("output", "{\"reasoning_content\":\"second\",\"response\":\"answer\"}"),
            new TraeSseEvent("done", "{\"finish_reason\":\"stop\"}"));

        var result = await TraeChatResult.CollectAsync(events);

        result.Text.Should().Be("answer");
        result.Reasoning.Should().Be("first second");
    }

    [TestMethod]
    public async Task ChatResult_RejectsCompletedStreamWithoutContent()
    {
        var events = Events(
            new TraeSseEvent("metadata", "{}"),
            new TraeSseEvent("done", "{\"finish_reason\":\"stop\"}"));

        Func<Task> collect = async () => await TraeChatResult.CollectAsync(events);

        await collect.Should().ThrowAsync<TraeUpstreamException>()
            .WithMessage("*未返回有效内容*");
    }

    [TestMethod]
    public async Task StreamHeartbeat_WritesHeartbeatWhileWaitingForUpstream()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int heartbeatCount = 0;
        var values = new List<int>();

        await foreach (int value in TraeStreamHeartbeat.ReadAsync(
            DelayedValue(release.Task),
            _ =>
            {
                Interlocked.Increment(ref heartbeatCount);
                release.TrySetResult();
                return ValueTask.CompletedTask;
            },
            TimeSpan.FromMilliseconds(10)))
        {
            values.Add(value);
        }

        heartbeatCount.Should().BeGreaterThanOrEqualTo(1);
        values.Should().Equal(42);
    }

    [TestMethod]
    public async Task StreamHeartbeat_IsNotResetByIgnoredUpstreamEvents()
    {
        int heartbeatCount = 0;

        await foreach (int _ in TraeStreamHeartbeat.ReadAsync(
            BusyValues(),
            _ =>
            {
                Interlocked.Increment(ref heartbeatCount);
                return ValueTask.CompletedTask;
            },
            TimeSpan.FromMilliseconds(15)))
        {
        }

        heartbeatCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [TestMethod]
    public void AnthropicThinking_ContentBlockStartHasEmptySignaturePlaceholder()
    {
        var block = TraeAnthropicThinking.ContentBlockStart();

        block["type"]!.ToString().Should().Be("thinking");
        block["thinking"]!.ToString().Should().Be("");
        block["signature"]!.ToString().Should().Be("");
    }

    [TestMethod]
    public void AnthropicThinking_SignatureDeltaIsNonEmptySoClientsAcceptTheBlock()
    {
        var delta = TraeAnthropicThinking.SignatureDelta();

        delta["type"]!.ToString().Should().Be("signature_delta");
        delta["signature"]!.ToString().Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public void AnthropicThinking_CompletedContentIncludesSignature()
    {
        var content = TraeAnthropicThinking.CompletedContent("because X, therefore Y");

        content["type"]!.ToString().Should().Be("thinking");
        content["thinking"]!.ToString().Should().Be("because X, therefore Y");
        content["signature"]!.ToString().Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public void AnthropicThinking_TreatsAdaptiveAsEnabled()
    {
        // Real Claude Desktop sends exactly this shape.
        var adaptive = JsonNode.Parse("""{"type":"adaptive","display":"omitted"}""");

        TraeAnthropicThinking.IsEnabled(adaptive).Should().BeTrue();
    }

    [TestMethod]
    public void AnthropicThinking_TreatsEnabledAsEnabledAndDisabledAsOff()
    {
        TraeAnthropicThinking.IsEnabled(JsonNode.Parse("""{"type":"enabled","budget_tokens":1024}""")).Should().BeTrue();
        TraeAnthropicThinking.IsEnabled(JsonNode.Parse("""{"type":"disabled"}""")).Should().BeFalse();
        TraeAnthropicThinking.IsEnabled(null).Should().BeFalse();
        TraeAnthropicThinking.IsEnabled(JsonNode.Parse("{}")).Should().BeFalse();
    }

    [TestMethod]
    public async Task ChatStreamAsync_RejectsUnrelatedActualModelUnlessAliasIsApproved()
    {
        const string upstream =
            "event: metadata\ndata: {\"model\":\"trae_tob_seed-code-lite-dev-0602-fixed\"}\n\n" +
            "event: output\ndata: {\"response\":\"ok\"}\n\n" +
            "event: done\ndata: {\"finish_reason\":\"stop\"}\n\n";

        Func<Task> withoutAlias = async () =>
        {
            var client = CreateClient(new StaticResponseHandler(upstream));
            await foreach (var _ in client.ChatStreamAsync(new[] { ("user", "ping") }, "Doubao_1_6__dev")) { }
        };
        await withoutAlias.Should().ThrowAsync<TraeModelSelectionException>();

        var approved = new TraeUpstreamOptions(ModelAliases: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Doubao_1_6"] = ["trae_tob_seed-code-lite-dev-0602-fixed"]
        });
        var aliased = new TraeClient(
            new TraeAuthData { Token = "test-token", ApiHost = "https://upstream.example" },
            httpMessageHandler: new StaticResponseHandler(upstream),
            upstreamOptions: approved);

        var events = new List<TraeSseEvent>();
        await foreach (var streamEvent in aliased.ChatStreamAsync(new[] { ("user", "ping") }, "Doubao_1_6__dev"))
            events.Add(streamEvent);
        events.Select(streamEvent => streamEvent.Event).Should().Equal("metadata", "output", "done");
    }

    private static TraeClient CreateClient(HttpMessageHandler handler) => new(
        new TraeAuthData { Token = "test-token", ApiHost = "https://upstream.example" },
        httpMessageHandler: handler);

    private static async IAsyncEnumerable<TraeSseEvent> Events(params TraeSseEvent[] events)
    {
        await Task.Yield();
        foreach (var streamEvent in events) yield return streamEvent;
    }

    private static async IAsyncEnumerable<int> DelayedValue(
        Task release,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await release.WaitAsync(cancellationToken);
        yield return 42;
    }

    private static async IAsyncEnumerable<int> BusyValues(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int index = 0; index < 20; index++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(3), cancellationToken);
            yield return index;
        }
    }

    private sealed class StaticResponseHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream")
        });
    }

}
