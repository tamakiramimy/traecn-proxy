using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TrancnProxy.Tests;

[TestClass]
public sealed class TraeOAuthLoginTests
{
    [TestMethod]
    public void Callback_WithoutState_IsMatchedByLoginTraceId()
    {
        var manager = new TraeOAuthLoginManager();
        string traceId = TraceIdOf(manager.Begin("default", CallbackUrl, "device-1", "machine-1"));

        // 上游只回传 loginTraceID，不回传 state
        manager.TryTakePending(Query(("loginTraceID", traceId), ("refreshToken", "token")), out var pending)
            .Should().BeTrue();
        pending.Alias.Should().Be("default");
        pending.DeviceId.Should().Be("device-1");
        pending.MachineId.Should().Be("machine-1");
    }

    [TestMethod]
    public void PendingLogin_IsConsumedOnlyOnce()
    {
        var manager = new TraeOAuthLoginManager();
        string traceId = TraceIdOf(manager.Begin("default", CallbackUrl, "device-1", "machine-1"));

        manager.TryTakePending(Query(("loginTraceID", traceId)), out _).Should().BeTrue();
        manager.TryTakePending(Query(("loginTraceID", traceId)), out _).Should().BeFalse();
    }

    [TestMethod]
    public void Callback_WithUnknownOrMissingTraceId_IsRejected()
    {
        var manager = new TraeOAuthLoginManager();
        manager.Begin("default", CallbackUrl, "device-1", "machine-1");

        manager.TryTakePending(Query(("loginTraceID", "not-a-pending-login")), out _).Should().BeFalse();
        manager.TryTakePending(Query(("refreshToken", "token")), out _).Should().BeFalse();
    }

    [TestMethod]
    public void Begin_RejectsInvalidCallbackUrl()
    {
        var manager = new TraeOAuthLoginManager();

        var begin = () => manager.Begin("default", "not-a-url", "device-1", "machine-1");

        begin.Should().Throw<InvalidOperationException>().WithMessage("*public base URL*");
    }

    private const string CallbackUrl = "http://127.0.0.1:9220/admin/oauth/callback";

    private static string TraceIdOf(string authorizationUrl) =>
        new Uri(authorizationUrl).Query.TrimStart('?')
            .Split('&')
            .Select(pair => pair.Split('=', 2))
            .First(pair => pair[0] == "login_trace_id")[1];

    private static QueryCollection Query(params (string Key, string Value)[] parameters) =>
        new(parameters.ToDictionary(p => p.Key, p => new StringValues(p.Value)));
}
