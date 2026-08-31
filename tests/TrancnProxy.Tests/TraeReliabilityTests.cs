using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
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
