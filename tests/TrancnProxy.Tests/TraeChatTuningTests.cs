using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TrancnProxy.Tests;

[TestClass]
public sealed class TraeChatTuningTests
{
    [TestMethod]
    public void FromAnthropic_MapsEnabledBudgetToConfirmedEffortLevels()
    {
        TraeChatTuning.FromAnthropic(JsonNode.Parse("""{"thinking":{"type":"enabled","budget_tokens":8192}}""")!.AsObject())
            .ReasoningEffort.Should().Be(TraeReasoningEffort.High);
        TraeChatTuning.FromAnthropic(JsonNode.Parse("""{"thinking":{"type":"enabled","budget_tokens":8193}}""")!.AsObject())
            .ReasoningEffort.Should().Be(TraeReasoningEffort.ExtraHigh);
    }

    [TestMethod]
    public void FromAnthropic_DoesNotOverrideAdaptiveOrDisabledThinking()
    {
        TraeChatTuning.FromAnthropic(JsonNode.Parse("""{"thinking":{"type":"adaptive"}}""")!.AsObject())
            .ReasoningEffort.Should().BeNull();
        TraeChatTuning.FromAnthropic(JsonNode.Parse("""{"thinking":{"type":"disabled"}}""")!.AsObject())
            .ReasoningEffort.Should().BeNull();
    }

    [TestMethod]
    public void OpenAiInputs_MapOnlyConfirmedHighLevels()
    {
        TraeChatTuning.FromOpenAI(JsonNode.Parse("""{"reasoning_effort":"high"}""")!.AsObject())
            .UpstreamReasoningEffort.Should().Be("high");
        TraeChatTuning.FromResponses(JsonNode.Parse("""{"reasoning":{"effort":"xhigh"}}""")!.AsObject())
            .UpstreamReasoningEffort.Should().Be("extra_high");
        TraeChatTuning.FromOpenAI(JsonNode.Parse("""{"reasoning_effort":"medium"}""")!.AsObject())
            .ReasoningEffort.Should().BeNull();
    }
}