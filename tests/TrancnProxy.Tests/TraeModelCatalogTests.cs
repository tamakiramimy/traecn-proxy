using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json.Nodes;
using TrancnProxy.Agent;

namespace TrancnProxy.Tests;

[TestClass]
public sealed class TraeModelCatalogTests
{
    [TestMethod]
    public async Task AgentSseReader_PreservesEventOrderingAndMultilinePayloads()
    {
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
            event: task_created
            data: {"task_id":"task-1"}

            : keepalive
            event: thought
            data: {"delta":"first"}
            data: {"delta":"second"}

            event: turn_completion
            data: {"status":"completed"}

            """));

        var frames = new List<TraeAgentSseFrame>();
        await foreach (var frame in TraeAgentSseReader.ReadAsync(stream)) frames.Add(frame);

        frames.Select(frame => frame.Event).Should().Equal("task_created", "thought", "turn_completion");
        frames[0].Data.Should().Be("{\"task_id\":\"task-1\"}");
        frames[1].Data.Should().Be("{\"delta\":\"first\"}\n{\"delta\":\"second\"}");
    }

    [TestMethod]
    public void AgentWorkspace_CreateEphemeralUsesVirtualUniqueFileUris()
    {
        var first = TraeAgentWorkspace.CreateEphemeral();
        var second = TraeAgentWorkspace.CreateEphemeral();

        first.RootUri.Scheme.Should().Be(Uri.UriSchemeFile);
        first.RootUri.AbsolutePath.Should().StartWith("/workspace/");
        first.ProjectId.Should().NotBe(second.ProjectId);
        first.WorkspaceId.Should().NotBe(second.WorkspaceId);
    }

    [TestMethod]
    public void ProtocolEvidenceSanitizer_RedactsSensitiveValuesAndCorrelatesIdentifiers()
    {
        var sanitizer = new TraeProtocolEvidenceSanitizer();
        var evidence = new JsonObject
        {
            ["method"] = "request_stream",
            ["model_name"] = "glm-5.3__dev",
            ["session_id"] = "session-private-value",
            ["authorization"] = "Bearer private-token",
            ["messages"] = new JsonArray(new JsonObject
            {
                ["content"] = "private prompt",
                ["user_id"] = "private-user"
            }),
            ["nested"] = new JsonObject { ["task_id"] = "session-private-value" }
        };

        var sanitized = sanitizer.Sanitize(evidence)!.AsObject();
        string serialized = sanitized.ToJsonString();

        serialized.Should().NotContain("private-token");
        serialized.Should().NotContain("private prompt");
        serialized.Should().NotContain("private-user");
        serialized.Should().NotContain("session-private-value");
        sanitized["method"]!.GetValue<string>().Should().Be("request_stream");
        sanitized["model_name"]!.GetValue<string>().Should().Be("glm-5.3__dev");
        sanitized["session_id"]!.GetValue<string>().Should().StartWith("id_");
        sanitized["nested"]!["task_id"]!.GetValue<string>().Should().Be(sanitized["session_id"]!.GetValue<string>());
        sanitized["messages"]![0]!["content"]!.GetValue<string>().Should().Be("[redacted]");
    }

    [TestMethod]
    public void Parse_ReturnsExactVisibleTargetModelIds()
    {
        var catalog = Catalog(
            Config("glm-5.3", "GLM-5.3", "glm-5.3__dev", "glm-5.3__max"),
            Config("DeepSeek-V4-Pro-Official", "DeepSeek-V4-Pro", "DeepSeek-V4-Pro-Official__dev", "DeepSeek-V4-Pro-Official__max"),
            Config("DeepSeek-V4-Flash-Official", "DeepSeek-V4-Flash", "deepseek_v4_flash_official__dev", "deepseek_v4_flash_official__max"),
            Config("kimi-k2.7-code", "Kimi-K2.7-Code", "kimi-k2.7-code__dev", "kimi-k2.7-code__max"));

        var snapshot = TraeModelCatalogParser.Parse(catalog, DateTimeOffset.UnixEpoch);

        snapshot.Models.Select(model => model.Id).Should().Equal(
            "glm-5.3__dev",
            "glm-5.3__max",
            "DeepSeek-V4-Pro-Official__dev",
            "DeepSeek-V4-Pro-Official__max",
            "deepseek_v4_flash_official__dev",
            "deepseek_v4_flash_official__max",
            "kimi-k2.7-code__dev",
            "kimi-k2.7-code__max");
        snapshot.Models.Select(model => model.Variant).Should().ContainInOrder(
            TraeModelVariant.Dev,
            TraeModelVariant.Max);
    }

    [TestMethod]
    public void Parse_SkipsDisabledHiddenAndInternalConfigurations()
    {
        var catalog = Catalog(
            Config("visible", "Visible", "visible__dev"),
            Config("disabled", "Disabled", false, null, "disabled__dev"),
            Config("hidden", "Hidden", true, true, "hidden__dev"),
            Config("internal", "", "internal_model"));

        var snapshot = TraeModelCatalogParser.Parse(catalog);

        snapshot.Models.Should().ContainSingle()
            .Which.Id.Should().Be("visible__dev");
    }

    [TestMethod]
    public void Parse_DoesNotSynthesizeMissingVariant()
    {
        var snapshot = TraeModelCatalogParser.Parse(Catalog(Config("glm-5.3", "GLM-5.3", "glm-5.3__dev")));

        snapshot.Models.Select(model => model.Id).Should().Equal("glm-5.3__dev");
        snapshot.TryGetModel("glm-5.3__max", out _).Should().BeFalse();
    }

    [TestMethod]
    public void Parse_RejectsConflictingVisibleModelIds()
    {
        var catalog = Catalog(
            Config("first", "First", "shared__dev"),
            Config("second", "Second", "shared__dev"));

        Action parse = () => TraeModelCatalogParser.Parse(catalog);

        parse.Should().Throw<TraeModelCatalogException>()
            .WithMessage("*shared__dev*conflicting*");
    }

    [TestMethod]
    public void Parse_RejectsInvalidSchema()
    {
        Action parse = () => TraeModelCatalogParser.Parse(new JsonObject());

        parse.Should().Throw<TraeModelCatalogException>()
            .WithMessage("*function_configs*");
    }

    [TestMethod]
    public async Task Cache_CoalescesConcurrentRefreshes()
    {
        int loadCount = 0;
        var cache = new TraeModelCatalogCache(async cancellationToken =>
        {
            Interlocked.Increment(ref loadCount);
            await Task.Delay(30, cancellationToken);
            return Catalog(Config("glm-5.3", "GLM-5.3", "glm-5.3__dev"));
        });

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => cache.GetAsync()));

        loadCount.Should().Be(1);
    }

    [TestMethod]
    public async Task Cache_CoalescesConcurrentForcedRefreshes()
    {
        int loadCount = 0;
        var cache = new TraeModelCatalogCache(async cancellationToken =>
        {
            int currentLoad = Interlocked.Increment(ref loadCount);
            await Task.Delay(30, cancellationToken);
            return Catalog(Config("glm-5.3", "GLM-5.3", $"glm-{currentLoad}__dev"));
        });
        await cache.GetAsync();

        var snapshots = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => cache.GetAsync(force: true)));

        loadCount.Should().Be(2);
        snapshots.Select(snapshot => snapshot.Models.Single().Id).Should().OnlyContain(id => id == "glm-2__dev");
    }

    [TestMethod]
    public async Task Cache_RefreshesOnlyAfterTimeToLiveExpires()
    {
        int loadCount = 0;
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new TraeModelCatalogCache(
            _ => Task.FromResult<JsonNode>(Catalog(Config("glm-5.3", "GLM-5.3", $"glm-{++loadCount}__dev"))),
            timeProvider,
            TimeSpan.FromMinutes(5));

        await cache.GetAsync();
        timeProvider.Advance(TimeSpan.FromMinutes(4));
        await cache.GetAsync();
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        var refreshed = await cache.GetAsync();

        loadCount.Should().Be(2);
        refreshed.Models.Single().Id.Should().Be("glm-2__dev");
    }

    [TestMethod]
    public async Task Resolve_ForcesOneRefreshAfterModelMiss()
    {
        int loadCount = 0;
        var cache = new TraeModelCatalogCache(_ =>
        {
            loadCount++;
            return Task.FromResult<JsonNode>(loadCount == 1
                ? Catalog(Config("glm-5.3", "GLM-5.3", "glm-5.3__dev"))
                : Catalog(Config("kimi-k2.7-code", "Kimi-K2.7-Code", "kimi-k2.7-code__max")));
        });

        await cache.GetAsync();
        var resolved = await cache.ResolveAsync("kimi-k2.7-code__max");

        resolved.ConfigName.Should().Be("kimi-k2.7-code");
        loadCount.Should().Be(2);
    }

    [TestMethod]
    public async Task Cache_InstancesKeepAccountCatalogsIsolated()
    {
        var first = new TraeModelCatalogCache(_ => Task.FromResult<JsonNode>(
            Catalog(Config("glm-5.3", "GLM-5.3", "glm-5.3__dev"))));
        var second = new TraeModelCatalogCache(_ => Task.FromResult<JsonNode>(
            Catalog(Config("kimi-k2.7-code", "Kimi-K2.7-Code", "kimi-k2.7-code__dev"))));

        var firstSnapshot = await first.GetAsync();
        var secondSnapshot = await second.GetAsync();

        firstSnapshot.TryGetModel("glm-5.3__dev", out _).Should().BeTrue();
        firstSnapshot.TryGetModel("kimi-k2.7-code__dev", out _).Should().BeFalse();
        secondSnapshot.TryGetModel("kimi-k2.7-code__dev", out _).Should().BeTrue();
        secondSnapshot.TryGetModel("glm-5.3__dev", out _).Should().BeFalse();
    }

    private static JsonObject Catalog(params JsonObject[] configs) => new()
    {
        ["function_configs"] = new JsonArray(
            new JsonObject
            {
                ["function"] = "chat_v3",
                ["config_info_list"] = new JsonArray(configs)
            },
            new JsonObject
            {
                ["function"] = "chat",
                ["config_info_list"] = new JsonArray()
            })
    };

    private static JsonObject Config(string configName, string displayName, params string[] modelIds) =>
        Config(configName, displayName, true, false, modelIds);

    private static JsonObject Config(
        string configName,
        string displayName,
        bool enabled,
        bool? invisible,
        params string[] modelIds) => new()
    {
        ["config_name"] = configName,
        ["config_switch"] = enabled,
        ["is_invisible_to_user"] = invisible,
        ["display_config"] = new JsonObject { ["display_name"] = displayName },
        ["model_detail_list"] = new JsonArray(modelIds.Select(modelId =>
            (JsonNode)new JsonObject { ["model_name"] = modelId }).ToArray())
    };

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan duration) => _current += duration;
    }
}