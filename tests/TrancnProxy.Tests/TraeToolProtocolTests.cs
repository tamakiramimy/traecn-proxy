using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TrancnProxy.Tests;

[TestClass]
public sealed class TraeToolProtocolTests
{
    [TestMethod]
    public void BuildSystemPrompt_PreservesBlockSystemAndToolSchema()
    {
        var system = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "system rules" });
        var tools = new JsonArray(new JsonObject
        {
            ["name"] = "write_file",
            ["description"] = "Writes a file",
            ["input_schema"] = new JsonObject { ["type"] = "object" }
        });

        string prompt = TraeToolProtocol.BuildSystemPrompt(system, tools);

        prompt.Should().Contain("system rules");
        prompt.Should().Contain("<tool_call>");
        prompt.Should().Contain("write_file");
        prompt.Should().Contain("input_schema");
    }

    [TestMethod]
    public void BuildSystemPrompt_EnforcesNamedToolChoice()
    {
        var tools = new JsonArray(new JsonObject { ["name"] = "write_file" });
        var choice = new JsonObject { ["type"] = "tool", ["name"] = "write_file" };

        string prompt = TraeToolProtocol.BuildSystemPrompt(null, tools, choice);

        prompt.Should().Contain("MUST call the 'write_file' tool");
        prompt.Should().Contain("Do not answer with prose");
    }

    [TestMethod]
    public void StreamParser_ParsesToolCallAcrossChunks()
    {
        var parser = new TraeToolProtocol.StreamParser();
        var blocks = new List<TraeOutputBlock>();

        blocks.AddRange(parser.Push("I will do it. <tool_"));
        blocks.AddRange(parser.Push("call>{\"name\":\"write_file\",\"arguments\":{\"path\":\"index.html\"}}</tool_"));
        blocks.AddRange(parser.Push("call>Done"));
        blocks.AddRange(parser.Complete());

        blocks.Should().HaveCount(3);
        blocks[0].Should().Be(new TraeTextBlock("I will do it. "));
        var tool = blocks[1].Should().BeOfType<TraeToolUseBlock>().Subject;
        tool.Name.Should().Be("write_file");
        tool.Input["path"]!.GetValue<string>().Should().Be("index.html");
        blocks[2].Should().Be(new TraeTextBlock("Done"));
    }

    [TestMethod]
    public void Parse_HandlesMultipleToolCallsAndStringArguments()
    {
        const string output = "<tool_call>{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}</tool_call>" +
                              "<tool_call>{\"name\":\"write_file\",\"input\":{\"path\":\"b.txt\"}}</tool_call>";

        var tools = TraeToolProtocol.Parse(output).OfType<TraeToolUseBlock>().ToList();

        tools.Select(tool => tool.Name).Should().Equal("read_file", "write_file");
        tools.Select(tool => tool.Input["path"]!.GetValue<string>()).Should().Equal("a.txt", "b.txt");
    }
}