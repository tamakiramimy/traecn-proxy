using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TrancnProxy.Tests;

[TestClass]
public sealed class TraeOutputClassifierTests
{
    [TestMethod]
    public void CarrierReasoning_PromotesTextAndExtractsStreamingToolCalls()
    {
        var classifier = new TraeOutputClassifier(TraeReasoningPresentation.Carrier, streamToolCalls: true);
        var blocks = new List<TraeOutputBlock>();

        blocks.AddRange(classifier.Push(TraeOutputChannel.Reasoning, "## 方案\n<tool_call>{\"name\":\"Write\",\"arguments\":{\"file_path\":\"a.cs\","));
        blocks.AddRange(classifier.Push(TraeOutputChannel.Reasoning, "\"content\":\"class A {}\"}}</tool_call>"));
        blocks.AddRange(classifier.Push(TraeOutputChannel.Response, "\n完成。"));
        blocks.AddRange(classifier.Complete());

        string text = string.Concat(blocks.OfType<TraeTextBlock>().Select(block => block.Text));
        text.Should().Be("## 方案\n\n完成。");
        blocks.OfType<TraeToolUseStartBlock>().Should().ContainSingle().Which.Name.Should().Be("Write");
        JsonNode.Parse(string.Concat(blocks.OfType<TraeToolInputDeltaBlock>().Select(block => block.PartialJson)))!
            ["content"]!.GetValue<string>().Should().Be("class A {}");
        blocks.OfType<TraeToolUseEndBlock>().Should().ContainSingle();
        blocks.OfType<TraeThinkingDeltaBlock>().Should().BeEmpty();
    }

    [TestMethod]
    public void NativeReasoning_KeepsTextAsThinkingButStillExtractsTools()
    {
        var classifier = new TraeOutputClassifier(TraeReasoningPresentation.NativeThinking, streamToolCalls: true);
        var blocks = classifier.Push(
                TraeOutputChannel.Reasoning,
                "plan<tool_call>{\"name\":\"Bash\",\"arguments\":{\"command\":\"pwd\"}}</tool_call>")
            .Concat(classifier.Complete())
            .ToList();

        string thinking = string.Concat(blocks.OfType<TraeThinkingDeltaBlock>().Select(block => block.Text));
        thinking.Should().Be("plan");
        blocks.OfType<TraeThinkingStartBlock>().Should().ContainSingle();
        blocks.OfType<TraeThinkingEndBlock>().Should().ContainSingle();
        blocks.OfType<TraeToolUseStartBlock>().Should().ContainSingle().Which.Name.Should().Be("Bash");
    }

    [TestMethod]
    public void CarrierReasoning_OnlyKeepsExplicitThinkingTagsAsThinking()
    {
        var classifier = new TraeOutputClassifier(TraeReasoningPresentation.Carrier);
        var blocks = classifier.Push(TraeOutputChannel.Reasoning, "<think>短计划</think>正文")
            .Concat(classifier.Complete())
            .ToList();

        string.Concat(blocks.OfType<TraeThinkingDeltaBlock>().Select(block => block.Text)).Should().Be("短计划");
        string.Concat(blocks.OfType<TraeTextBlock>().Select(block => block.Text)).Should().Be("正文");
    }

    [TestMethod]
    public void DefaultPresentation_UsesCarrierForAffectedModelFamilies()
    {
        TraeOutputClassifier.DefaultPresentation(
                new TraeModelDescriptor("glm-5.3__dev", "glm-5.3", "GLM-5.3", TraeModelVariant.Dev))
            .Should().Be(TraeReasoningPresentation.Carrier);
        TraeOutputClassifier.DefaultPresentation(
                new TraeModelDescriptor("other__dev", "other", "Other", TraeModelVariant.Dev))
            .Should().Be(TraeReasoningPresentation.NativeThinking);
    }

    [TestMethod]
    public void ConfiguredPresentation_NativeOverrideTakesPriority()
    {
        var settings = new ProxySettings.ReasoningSettings
        {
            CarrierModelPatterns = ["glm"],
            NativeThinkingModelPatterns = ["glm-5.3"]
        };

        settings.ResolvePresentation(
                new TraeModelDescriptor("glm-5.3__dev", "glm-5.3", "GLM-5.3", TraeModelVariant.Dev))
            .Should().Be(TraeReasoningPresentation.NativeThinking);
    }

    [TestMethod]
    public void ConditionalNativePresentation_OnlyAppliesWhenThinkingIsEnabled()
    {
        var settings = new ProxySettings.ReasoningSettings
        {
            CarrierModelPatterns = ["kimi"],
            NativeThinkingModelPatterns = [],
            NativeThinkingWhenEnabledModelPatterns = ["kimi-k3"]
        };
        var model = new TraeModelDescriptor(
            "kimi-k3__max", "kimi-k3", "Kimi K3", TraeModelVariant.Max);

        settings.ResolvePresentation(model, thinkingEnabled: true)
            .Should().Be(TraeReasoningPresentation.NativeThinking);
        settings.ResolvePresentation(model, thinkingEnabled: false)
            .Should().Be(TraeReasoningPresentation.Carrier);
    }

    [TestMethod]
    public void ReasoningPreview_StopsBeforeDraftedCode()
    {
        var preview = new TraeReasoningPreview();

        string first = preview.Push("I will create the requested file.\n");
        string second = preview.Push("\n<!DOCTYPE html><html>draft</html>");

        (first + second).Should().Be("I will create the requested file.");
        preview.Stopped.Should().BeTrue();
    }

    [TestMethod]
    public void ReasoningPreview_RecognizesCodeMarkerAcrossChunks()
    {
        var preview = new TraeReasoningPreview();

        string first = preview.Push("Brief plan.<ht");
        string second = preview.Push("ml><body>draft</body>");

        (first + second).Should().Be("Brief plan.");
        preview.Stopped.Should().BeTrue();
    }

    [TestMethod]
    public void ReasoningPreview_CapsUnstructuredPlanningText()
    {
        var preview = new TraeReasoningPreview();

        string result = preview.Push(new string('x', 800));

        result.Should().HaveLength(480);
        preview.Stopped.Should().BeTrue();
    }
}