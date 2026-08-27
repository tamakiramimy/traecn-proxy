using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TrancnProxy.Tests;

[TestClass]
public sealed class TraeServiceFaceTests
{
    [TestMethod]
    public async Task ExplicitEnterpriseKind_KeepsControlPlaneChannelOnStandaloneChatHost()
    {
        var handler = new CapturingHandler(Sse("glm-5.3"));
        var client = new TraeClient(
            Auth(),
            httpMessageHandler: handler,
            chatApiHost: "https://chat.example",
            accountKind: TraeAccountKind.Enterprise);

        await Drain(client.ChatStreamAsync(
            [("user", "ping")],
            new TraeModelDescriptor("glm-5.3__dev", "glm-5.3", "GLM-5.3", TraeModelVariant.Dev)));

        client.ServiceFace.Kind.Should().Be(TraeAccountKind.Enterprise);
        handler.Host.Should().Be("chat.example");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("function").GetString().Should().Be("chat_v3");
        body.RootElement.GetProperty("model").GetString().Should().Be("glm-5.3__dev");
        body.RootElement.GetProperty("config_name").GetString().Should().Be("glm-5.3");
        handler.Headers["x-ide-version"].Should().Be("3.3.87");
        handler.Headers.Should().NotContainKey("x-ide-token");
    }

    [TestMethod]
    public async Task ExplicitSoloKind_UsesSoloChannelWithoutStandaloneChatHost()
    {
        var handler = new CapturingHandler(Sse("Doubao-Seed-Evolving"));
        var client = new TraeClient(Auth(), httpMessageHandler: handler, accountKind: TraeAccountKind.Solo);

        await Drain(client.ChatStreamAsync([("user", "ping")], "Doubao-Seed-Evolving"));

        client.ServiceFace.Kind.Should().Be(TraeAccountKind.Solo);
        handler.Host.Should().Be("console.example");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("function").GetString().Should().Be("solo_work_lite");
        handler.Headers["x-ide-version"].Should().Be("0.1.43");
        handler.Headers["x-ide-token"].Should().Be("test-token");
        handler.Headers["x-os-version"].Should().Be("Windows 11 Pro");
    }

    [TestMethod]
    public async Task EnterpriseFace_LoadsCatalogFromBatchEndpointOnControlPlane()
    {
        var handler = new CapturingHandler(EnterpriseCatalog(), "application/json");
        var client = new TraeClient(
            Auth(),
            httpMessageHandler: handler,
            chatApiHost: "https://chat.example",
            accountKind: TraeAccountKind.Enterprise);

        var catalog = await client.GetModelCatalogAsync();

        handler.Host.Should().Be("console.example");
        handler.Path.Should().Be("/api/ide/v1/batch_get_detail_param");
        catalog.TryGetModel("glm-5.3__dev", out _).Should().BeTrue();
    }

    [TestMethod]
    public async Task SoloFace_LoadsCatalogFromDetailEndpointOnChatHost()
    {
        var handler = new CapturingHandler(SoloCatalog(), "application/json");
        var client = new TraeClient(
            Auth(),
            httpMessageHandler: handler,
            chatApiHost: "https://chat.example",
            accountKind: TraeAccountKind.Solo);

        var catalog = await client.GetModelCatalogAsync();

        handler.Host.Should().Be("chat.example");
        handler.Path.Should().Be("/api/ide/v1/get_detail_param");
        handler.Headers["x-ide-version"].Should().Be("0.1.43");
        catalog.TryGetModel("Doubao-Seed-Evolving", out _).Should().BeTrue();
    }

    [TestMethod]
    public async Task ConfiguredClientProfile_OverridesVersionAndDeviceHeaders()
    {
        var handler = new CapturingHandler(Sse("Doubao-Seed-Evolving"));
        var options = new TraeUpstreamOptions(
            SoloProfile: new TraeClientProfileOverrides(
                IdeVersion: "0.9.9",
                IdeVersionCode: "20991231",
                DeviceBrand: "TESTBRAND"));
        var client = new TraeClient(
            Auth(),
            httpMessageHandler: handler,
            accountKind: TraeAccountKind.Solo,
            upstreamOptions: options);

        await Drain(client.ChatStreamAsync([("user", "ping")], "Doubao-Seed-Evolving"));

        handler.Headers["x-ide-version"].Should().Be("0.9.9");
        handler.Headers["x-ide-version-code"].Should().Be("20991231");
        handler.Headers["x-app-version-code"].Should().Be("20991231");
        handler.Headers["x-device-brand"].Should().Be("TESTBRAND");
        handler.Headers["User-Agent"].Should().Be("Trae/0.9.9");
        handler.Headers["x-os-version"].Should().Be("Windows 11 Pro");
    }

    [TestMethod]
    public void DefaultModel_FollowsServiceFaceModelIdSemantics()
    {
        var enterprise = new TraeClient(Auth(), accountKind: TraeAccountKind.Enterprise);
        var solo = new TraeClient(Auth(), accountKind: TraeAccountKind.Solo);

        enterprise.DefaultModelId.Should().Be("Doubao-Seed-Evolving__dev");
        solo.DefaultModelId.Should().Be("Doubao-Seed-Evolving");
    }

    [TestMethod]
    public void AutoKind_InfersSoloOnlyWhenChatHostDiffersFromControlPlane()
    {
        var inferredSolo = new TraeClient(Auth(), chatApiHost: "https://chat.example");
        var inferredEnterprise = new TraeClient(Auth(), chatApiHost: "https://console.example");

        inferredSolo.ServiceFace.Kind.Should().Be(TraeAccountKind.Solo);
        inferredEnterprise.ServiceFace.Kind.Should().Be(TraeAccountKind.Enterprise);
    }

    private static TraeAuthData Auth() => new() { Token = "test-token", ApiHost = "https://console.example" };

    private static string Sse(string actualModel) =>
        $"event: metadata\ndata: {{\"model\":\"{actualModel}\"}}\n\nevent: output\ndata: {{\"response\":\"ok\"}}\n\n";

    private static string EnterpriseCatalog() => new JsonObject
    {
        ["function_configs"] = new JsonArray(new JsonObject
        {
            ["function"] = "chat_v3",
            ["config_info_list"] = new JsonArray(new JsonObject
            {
                ["config_name"] = "glm-5.3",
                ["config_switch"] = true,
                ["display_config"] = new JsonObject { ["display_name"] = "GLM-5.3" },
                ["model_detail_list"] = new JsonArray(new JsonObject { ["model_name"] = "glm-5.3__dev" })
            })
        })
    }.ToJsonString();

    private static string SoloCatalog() => new JsonObject
    {
        ["config_info_list"] = new JsonArray(new JsonObject
        {
            ["config_name"] = "Doubao-Seed-Evolving",
            ["display_config"] = new JsonObject { ["display_name"] = "Doubao Seed Evolving" }
        })
    }.ToJsonString();

    private static async Task Drain(IAsyncEnumerable<TraeSseEvent> events)
    {
        await foreach (var _ in events)
        {
        }
    }

    private sealed class CapturingHandler(string responseBody, string contentType = "text/event-stream") : HttpMessageHandler
    {
        public string? Host { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Host = request.RequestUri!.Host;
            Path = request.RequestUri!.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            foreach (var header in request.Headers)
                Headers[header.Key] = string.Join(',', header.Value);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, contentType)
            };
        }
    }
}
