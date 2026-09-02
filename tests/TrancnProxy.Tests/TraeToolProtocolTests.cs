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
        prompt.Should().Contain("MUST use the appropriate tools");
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
    public void BuildSystemPrompt_RequestsTaggedThinkingAndMarkdownWhenEnabled()
    {
        string prompt = TraeToolProtocol.BuildSystemPrompt(
            new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "system rules" }),
            new JsonArray(new JsonObject { ["name"] = "run_code" }),
            thinkingEnabled: true);

        prompt.Should().Contain("<thinking>").And.Contain("</thinking>");
        prompt.Should().Contain("must stay short");
        prompt.Should().Contain("Do not draft the full solution");
        prompt.Should().Contain("MUST still emit any tool call");
        prompt.Should().Contain("Markdown").And.Contain("fenced code blocks");
    }

    [TestMethod]
    public void ValidateToolUse_RejectsMissingRequiredArguments()
    {
        var tools = new JsonArray(new JsonObject
        {
            ["name"] = "run_code",
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("code", "description")
            }
        });
        var toolUse = new TraeToolUseBlock("toolu_1", "run_code", new JsonObject());

        TraeToolProtocol.TryValidateToolUse(toolUse, tools, out string error).Should().BeFalse();
        error.Should().Contain("code").And.Contain("description");
    }

    [TestMethod]
    public void ValidateToolUse_AllowsToolsWithoutRequiredArguments()
    {
        var tools = new JsonArray(new JsonObject
        {
            ["name"] = "list_files",
            ["input_schema"] = new JsonObject { ["type"] = "object" }
        });
        var toolUse = new TraeToolUseBlock("toolu_1", "list_files", new JsonObject());

        TraeToolProtocol.TryValidateToolUse(toolUse, tools, out _).Should().BeTrue();
    }

    [TestMethod]
    public void ShouldForceToolUse_RequiresExecutionForWorkspaceActionOnly()
    {
        var tools = new JsonArray(new JsonObject { ["name"] = "write_file" });
        var action = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["content"] = "帮我写一个 H5 赛车游戏"
        });
        var question = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["content"] = "解释一下赛车游戏的难度设计"
        });

        TraeToolProtocol.ShouldForceToolUse(action, tools, new JsonObject { ["type"] = "auto" }).Should().BeTrue();
        TraeToolProtocol.ShouldForceToolUse(question, tools, new JsonObject { ["type"] = "auto" }).Should().BeFalse();
    }

    [TestMethod]
    public void ShouldForceToolUse_StopsAfterRequestedToolCompletes()
    {
        var tools = new JsonArray(new JsonObject { ["name"] = "write_file" });
        var messages = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = "toolu_1",
                ["content"] = "File created"
            })
        });

        TraeToolProtocol.ShouldForceToolUse(messages, tools, new JsonObject { ["type"] = "auto" }).Should().BeFalse();
    }

    [TestMethod]
    public void PreferredExecutionTool_SelectsWriteForFileCreation()
    {
        var tools = new JsonArray(
            new JsonObject { ["name"] = "Read" },
            new JsonObject { ["name"] = "Write" },
            new JsonObject { ["name"] = "Bash" });
        var messages = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["content"] = "在 /tmp 创建一个 H5 游戏，写完后读取"
        });

        TraeToolProtocol.PreferredExecutionTool(messages, tools).Should().Be("Write");
    }

    [TestMethod]
    public void PreferredExecutionTool_SelectsOutstandingReadAfterWriteCompletes()
    {
        var tools = new JsonArray(
            new JsonObject { ["name"] = "Write" },
            new JsonObject { ["name"] = "Read" });
        var messages = new JsonArray(
            new JsonObject { ["role"] = "user", ["content"] = "创建 a.html，再使用 Read 读回" },
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["name"] = "Write" })
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["content"] = "created" })
            });

        TraeToolProtocol.PreferredExecutionTool(messages, tools).Should().Be("Read");
        TraeToolProtocol.ShouldForceToolUse(messages, tools, new JsonObject { ["type"] = "auto" }).Should().BeTrue();

        messages.Add(new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["name"] = "Read" })
        });
        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["content"] = "read" })
        });

        TraeToolProtocol.PreferredExecutionTool(messages, tools).Should().BeNull();
        TraeToolProtocol.ShouldForceToolUse(messages, tools, new JsonObject { ["type"] = "auto" }).Should().BeFalse();
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

    [TestMethod]
    public void StreamParser_RepairsObservedIdFirstSeparatorError()
    {
        var parser = new TraeToolProtocol.StreamParser(streamToolCalls: true);
        const string output = "<tool_call>{\"id\":\"toolu_1\"][\"name\":\"Read\",\"arguments\":{\"file_path\":\"/tmp/a.txt\"}}</tool_call>";

        var blocks = parser.Push(output).Concat(parser.Complete()).ToList();

        var tool = blocks.OfType<TraeToolUseBlock>().Should().ContainSingle().Subject;
        tool.Id.Should().Be("toolu_1");
        tool.Name.Should().Be("Read");
        tool.Input["file_path"]!.GetValue<string>().Should().Be("/tmp/a.txt");
    }

    [TestMethod]
    public void StreamParser_StreamsToolNameAndArgumentsBeforeClosingTag()
    {
        var parser = new TraeToolProtocol.StreamParser(streamToolCalls: true);

        var first = parser.Push("<tool_call>{\"name\":\"write_file\",\"arguments\":{\"path\":\"index.html\",\"content\":\"<html>");

        first.OfType<TraeToolUseStartBlock>().Should().ContainSingle().Which.Name.Should().Be("write_file");
        first.OfType<TraeToolInputDeltaBlock>().Should().NotBeEmpty();

        var remaining = parser.Push("game</html>\"}}</tool_call>").Concat(parser.Complete()).ToList();
        string streamedJson = string.Concat(first.Concat(remaining).OfType<TraeToolInputDeltaBlock>().Select(block => block.PartialJson));

        JsonNode.Parse(streamedJson)!["path"]!.GetValue<string>().Should().Be("index.html");
        JsonNode.Parse(streamedJson)!["content"]!.GetValue<string>().Should().Be("<html>game</html>");
        remaining.OfType<TraeToolUseEndBlock>().Should().ContainSingle();
    }

    [TestMethod]
    public void StreamParser_StreamsBareJsonToolCall()
    {
        var parser = new TraeToolProtocol.StreamParser(streamToolCalls: true);
        var blocks = new List<TraeOutputBlock>();

        blocks.AddRange(parser.Push("{\"na"));
        blocks.AddRange(parser.Push("me\":\"Bash\",\"arguments\":{\"command\":\"pwd\"}}\n"));
        blocks.AddRange(parser.Complete());

        blocks.OfType<TraeToolUseStartBlock>().Should().ContainSingle().Which.Name.Should().Be("Bash");
        string input = string.Concat(blocks.OfType<TraeToolInputDeltaBlock>().Select(block => block.PartialJson));
        JsonNode.Parse(input)!["command"]!.GetValue<string>().Should().Be("pwd");
        blocks.OfType<TraeToolUseEndBlock>().Should().ContainSingle();
        blocks.OfType<TraeTextBlock>().Should().BeEmpty();
    }

    [TestMethod]
    public void StreamParser_KeepsOrdinaryBareJsonAsText()
    {
        var parser = new TraeToolProtocol.StreamParser(streamToolCalls: true);
        var blocks = parser.Push("{\"status\":\"ok\"}").Concat(parser.Complete()).ToList();

        string text = string.Concat(blocks.OfType<TraeTextBlock>().Select(block => block.Text));
        text.Should().Be("{\"status\":\"ok\"}");
        blocks.OfType<TraeToolUseStartBlock>().Should().BeEmpty();
    }

    [TestMethod]
    public void Parse_SeparatesThinkingFromVisibleText()
    {
        var blocks = TraeToolProtocol.Parse("<think>先规划赛道</think>开始实现。");

        blocks.OfType<TraeThinkingStartBlock>().Should().ContainSingle();
        string thinking = string.Concat(blocks.OfType<TraeThinkingDeltaBlock>().Select(block => block.Text));
        thinking.Should().Be("先规划赛道");
        blocks.OfType<TraeThinkingEndBlock>().Should().ContainSingle();
        blocks.OfType<TraeTextBlock>().Single().Text.Should().Be("开始实现。");
    }

    [TestMethod]
    public void StreamParser_StreamsThinkingAcrossChunks()
    {
        var parser = new TraeToolProtocol.StreamParser(streamToolCalls: true);
        var blocks = new List<TraeOutputBlock>();

        blocks.AddRange(parser.Push("<thin"));
        blocks.AddRange(parser.Push("k>分析需求"));
        blocks.AddRange(parser.Push("，选择方案</thi"));
        blocks.AddRange(parser.Push("nk>正文"));
        blocks.AddRange(parser.Complete());

        string thinking = string.Concat(blocks.OfType<TraeThinkingDeltaBlock>().Select(block => block.Text));
        thinking.Should().Be("分析需求，选择方案");
        blocks.OfType<TraeThinkingEndBlock>().Should().ContainSingle();
        string text = string.Concat(blocks.OfType<TraeTextBlock>().Select(block => block.Text));
        text.Should().Be("正文");
    }

    [TestMethod]
    public void StreamParser_SupportsThinkingTagVariantBeforeToolCall()
    {
        var parser = new TraeToolProtocol.StreamParser(streamToolCalls: true);
        var blocks = new List<TraeOutputBlock>();

        blocks.AddRange(parser.Push("<thinking>需要写文件</thinking>"));
        blocks.AddRange(parser.Push("<tool_call>{\"name\":\"write_file\",\"arguments\":{\"path\":\"a.html\"}}</tool_call>"));
        blocks.AddRange(parser.Complete());

        string thinking = string.Concat(blocks.OfType<TraeThinkingDeltaBlock>().Select(block => block.Text));
        thinking.Should().Be("需要写文件");
        blocks.OfType<TraeToolUseStartBlock>().Should().ContainSingle().Which.Name.Should().Be("write_file");
    }

    [TestMethod]
    public void StreamParser_ParsesDeepSeekDsmlToolCall()
    {
        var parser = new TraeToolProtocol.StreamParser(streamToolCalls: true);
        var blocks = new List<TraeOutputBlock>();

        blocks.AddRange(parser.Push("我来看一下当前目录。\n<tool_calls>\n"));
        blocks.AddRange(parser.Push("<｜DSML｜ name=\"Bash\">\n<parameter name=\"command\">ls -la /tmp</parameter>\n"));
        blocks.AddRange(parser.Push("<parameter name=\"description\">List files</parameter>\n</｜DSML｜>"));
        blocks.AddRange(parser.Complete());

        blocks.OfType<TraeToolUseStartBlock>().Should().ContainSingle().Which.Name.Should().Be("Bash");
        string input = string.Concat(blocks.OfType<TraeToolInputDeltaBlock>().Select(block => block.PartialJson));
        JsonNode parsed = JsonNode.Parse(input)!;
        parsed["command"]!.GetValue<string>().Should().Be("ls -la /tmp");
        parsed["description"]!.GetValue<string>().Should().Be("List files");
        blocks.OfType<TraeToolUseEndBlock>().Should().ContainSingle();

        string text = string.Concat(blocks.OfType<TraeTextBlock>().Select(block => block.Text));
        text.Should().NotContain("tool_calls").And.NotContain("DSML");
        text.Trim().Should().Be("我来看一下当前目录。");
    }

    [TestMethod]
    public void StreamParser_ParsesInvokeStyleToolCallWithTypedParameters()
    {
        var blocks = TraeToolProtocol.Parse(
            "<function_calls><invoke name=\"Read\">" +
            "<parameter name=\"file_path\">/tmp/a.txt</parameter>" +
            "<parameter name=\"limit\">50</parameter>" +
            "<parameter name=\"raw\">true</parameter>" +
            "</invoke></function_calls>");

        blocks.OfType<TraeToolUseStartBlock>().Should().ContainSingle().Which.Name.Should().Be("Read");
        JsonNode parsed = JsonNode.Parse(string.Concat(
            blocks.OfType<TraeToolInputDeltaBlock>().Select(block => block.PartialJson)))!;
        parsed["file_path"]!.GetValue<string>().Should().Be("/tmp/a.txt");
        parsed["limit"]!.GetValue<int>().Should().Be(50);
        parsed["raw"]!.GetValue<bool>().Should().BeTrue();
    }

    [TestMethod]
    public void StreamParser_KeepsHtmlTextIntact()
    {
        var parser = new TraeToolProtocol.StreamParser(streamToolCalls: true);
        var blocks = new List<TraeOutputBlock>();

        blocks.AddRange(parser.Push("<!DOCTYPE html>\n<div class=\"a\">hi</div>"));
        blocks.AddRange(parser.Complete());

        string text = string.Concat(blocks.OfType<TraeTextBlock>().Select(block => block.Text));
        text.Should().Be("<!DOCTYPE html>\n<div class=\"a\">hi</div>");
        blocks.OfType<TraeToolUseStartBlock>().Should().BeEmpty();
    }

    [TestMethod]
    public void Parse_HandlesAttributedToolCallTagWithParameters()
    {
        // Shape observed leaking 15KB of HTML as visible text in a real long-running session.
        const string payload = """
            先重写文件：

            <tool_call name="Write">
            <parameter name="file_path">/tmp/whack_a_mole.html</parameter>
            <parameter name="content"><!DOCTYPE html>
            <html lang="zh"><body>hi</body></html></parameter>
            </tool_call>
            """;

        var blocks = TraeToolProtocol.Parse(payload);

        var start = blocks.OfType<TraeToolUseStartBlock>().Single();
        start.Name.Should().Be("Write");
        var input = JsonNode.Parse(blocks.OfType<TraeToolInputDeltaBlock>().Single().PartialJson)!.AsObject();
        input["file_path"]!.ToString().Should().Be("/tmp/whack_a_mole.html");
        input["content"]!.ToString().Should().Contain("<!DOCTYPE html>");
        blocks.OfType<TraeToolUseEndBlock>().Should().HaveCount(1);

        string visible = string.Concat(blocks.OfType<TraeTextBlock>().Select(block => block.Text));
        visible.Should().NotContain("<tool_call");
        visible.Should().NotContain("<parameter");
        visible.Should().NotContain("<!DOCTYPE html>");
    }

    [TestMethod]
    public void Parse_HandlesAttributedToolCallTagWithJsonBody()
    {
        const string payload = """<tool_call name="Read">{"file_path":"/tmp/a.txt"}</tool_call>""";

        var blocks = TraeToolProtocol.Parse(payload);

        blocks.OfType<TraeToolUseStartBlock>().Single().Name.Should().Be("Read");
        JsonNode.Parse(blocks.OfType<TraeToolInputDeltaBlock>().Single().PartialJson)!
            .AsObject()["file_path"]!.ToString().Should().Be("/tmp/a.txt");
        string.Concat(blocks.OfType<TraeTextBlock>().Select(b => b.Text)).Should().NotContain("tool_call");
    }

    [TestMethod]
    public void StreamParser_DoesNotLeakPartialAttributedToolCallTag()
    {
        var parser = new TraeToolProtocol.StreamParser(streamToolCalls: true);

        var emitted = parser.Push("写入文件：\n<tool_call na").ToList();

        string visible = string.Concat(emitted.OfType<TraeTextBlock>().Select(block => block.Text));
        visible.Should().NotContain("<tool_call");
    }

    [TestMethod]
    public void StreamParser_AssemblesValidJsonForBareToolCallWithMultilineContent()
    {
        // Exact shape emitted by qwen3.8-max for a Write call, including the newline before </tool_call>.
        const string payload = "<tool_call>{\"name\":\"Write\",\"arguments\":{\"file_path\":\"/tmp/mini.html\",\"content\":\"<!DOCTYPE html>\\n<html lang=\\\"zh\\\">\\n<style>\\nbody { color: #333; }\\n</style>\\n</html>\\n\"}}\n</tool_call>";
        var parser = new TraeToolProtocol.StreamParser(streamToolCalls: true);

        var blocks = new List<TraeOutputBlock>();
        for (int offset = 0; offset < payload.Length; offset += 17)
            blocks.AddRange(parser.Push(payload.Substring(offset, Math.Min(17, payload.Length - offset))));
        blocks.AddRange(parser.Complete());

        blocks.OfType<TraeToolUseStartBlock>().Single().Name.Should().Be("Write");
        string assembled = string.Concat(blocks.OfType<TraeToolInputDeltaBlock>().Select(b => b.PartialJson));
        var input = JsonNode.Parse(assembled)!.AsObject();
        input["file_path"]!.ToString().Should().Be("/tmp/mini.html");
        input["content"]!.ToString().Should().Contain("<!DOCTYPE html>");
        blocks.OfType<TraeToolUseEndBlock>().Should().HaveCount(1);
    }

    [TestMethod]
    public void StreamParser_AssemblesValidJsonWhenClosingBracesArriveBeforeCloseTag()
    {
        // Upstream SSE often ends one delta right after the JSON and sends </tool_call> in the next.
        var parser = new TraeToolProtocol.StreamParser(streamToolCalls: true);
        var blocks = new List<TraeOutputBlock>();

        blocks.AddRange(parser.Push("""<tool_call>{"name":"Write","arguments":{"file_path":"/tmp/mini.html","content":"hi"}}"""));
        blocks.AddRange(parser.Push("\n</tool_call>"));
        blocks.AddRange(parser.Complete());

        string assembled = string.Concat(blocks.OfType<TraeToolInputDeltaBlock>().Select(b => b.PartialJson));
        var input = JsonNode.Parse(assembled)!.AsObject();
        input["file_path"]!.ToString().Should().Be("/tmp/mini.html");
        input["content"]!.ToString().Should().Be("hi");
    }

    [TestMethod]
    public void StreamParser_KeepsArgumentsIntactWhenModelOmitsWrapperBrace()
    {
        // Observed in production: the model closed arguments but not the outer object,
        // so blindly stripping the last '}' destroyed the arguments object itself.
        const string payload = """<tool_call>{"name":"Bash","arguments":{"command":"ls -la /tmp","description":"List working directory contents"}</tool_call>""";
        var parser = new TraeToolProtocol.StreamParser(streamToolCalls: true);

        var blocks = parser.Push(payload).ToList();
        blocks.AddRange(parser.Complete());

        blocks.OfType<TraeToolUseStartBlock>().Single().Name.Should().Be("Bash");
        string assembled = string.Concat(blocks.OfType<TraeToolInputDeltaBlock>().Select(b => b.PartialJson));
        var input = JsonNode.Parse(assembled)!.AsObject();
        input["command"]!.ToString().Should().Be("ls -la /tmp");
        input["description"]!.ToString().Should().Be("List working directory contents");
    }

    [TestMethod]
    public void StreamParser_DropsWrapperTailWithoutTouchingBracesInsideStrings()
    {
        // The command value contains braces and escaped quotes; only the wrapper tail may be dropped.
        const string payload = """<tool_call>{"name":"Bash","arguments":{"command":"echo \"{a}\" && ls","description":"x"}}</tool_call>""";
        var parser = new TraeToolProtocol.StreamParser(streamToolCalls: true);

        var blocks = parser.Push(payload).ToList();
        blocks.AddRange(parser.Complete());

        string assembled = string.Concat(blocks.OfType<TraeToolInputDeltaBlock>().Select(b => b.PartialJson));
        var input = JsonNode.Parse(assembled)!.AsObject();
        input["command"]!.ToString().Should().Be("""echo "{a}" && ls""");
        input["description"]!.ToString().Should().Be("x");
    }

    [TestMethod]
    public void Parse_RecoversDsmlContaminatedCloseTagWithFlattenedArguments()
    {
        // Exact deepseek output: DSML token leaked into </tool_call> and the params sit next to "name".
        const string payload = """<tool_call>{"name":"AskUserQuestion","questions":[{"question":"玩法?","options":["A","B"]}]}</｜DSML｜tool_call>""";

        var blocks = TraeToolProtocol.Parse(payload);

        var toolUse = blocks.OfType<TraeToolUseBlock>().Single();
        toolUse.Name.Should().Be("AskUserQuestion");
        toolUse.Input["questions"]!.AsArray().Should().HaveCount(1);
        string visible = string.Concat(blocks.OfType<TraeTextBlock>().Select(b => b.Text));
        visible.Should().NotContain("tool_call");
        visible.Should().NotContain("AskUserQuestion");
    }

    [TestMethod]
    public void Parse_RecoversDuplicatedToolCallSeparatedByDsmlMarkers()
    {
        // Observed deepseek output: same call emitted twice, split by </｜DSML｜>, id before name.
        const string payload =
            """<tool_call>{"id":"toolu_c1e4b0b9","name":"AskUserQuestion","questions":[{"question":"目标用户是谁?","options":["学龄前","小学生"]}]}</｜DSML｜>""" +
            "\n" +
            """{"id":"toolu_c1e4b0b9","name":"AskUserQuestion","arguments":{"questions":[{"question":"目标用户是谁?"}]}}</｜DSML｜>""" +
            "\n</tool_call>";

        var blocks = TraeToolProtocol.Parse(payload);

        var toolUse = blocks.OfType<TraeToolUseBlock>().Single();
        toolUse.Name.Should().Be("AskUserQuestion");
        toolUse.Id.Should().Be("toolu_c1e4b0b9");
        toolUse.Input["questions"]!.AsArray().Should().HaveCount(1);
        toolUse.Input["questions"]![0]!["options"]!.AsArray().Should().HaveCount(2);

        string visible = string.Concat(blocks.OfType<TraeTextBlock>().Select(b => b.Text));
        visible.Should().NotContain("tool_call");
        visible.Should().NotContain("DSML");
        visible.Should().NotContain("AskUserQuestion");
    }

    [TestMethod]
    public void Parse_RecoversToolCallWhenCloseTagIsMissingEntirely()
    {
        const string payload = """<tool_call>{"name":"AskUserQuestion","questions":[{"question":"玩法?"}]}</｜DSML｜>""";

        var blocks = TraeToolProtocol.Parse(payload);

        blocks.OfType<TraeToolUseBlock>().Single().Name.Should().Be("AskUserQuestion");
        string.Concat(blocks.OfType<TraeTextBlock>().Select(b => b.Text)).Should().NotContain("tool_call");
    }

    [TestMethod]
    public void Parse_RecoversWhenCloseTagNameDoesNotMatchOpenTag()
    {
        // Observed deepseek output: opened with <tool_call> but closed with </tool_request>.
        const string payload =
            "先确认几个细节。\n\n" +
            """<tool_call>{"name":"AskUserQuestion","questions":[{"question":"核心玩法?","options":["A","B"]}]}</tool_request>""";

        var blocks = TraeToolProtocol.Parse(payload);

        blocks.OfType<TraeToolUseBlock>().Single().Name.Should().Be("AskUserQuestion");
        string visible = string.Concat(blocks.OfType<TraeTextBlock>().Select(b => b.Text));
        visible.Should().Contain("先确认几个细节");
        visible.Should().NotContain("tool_call");
        visible.Should().NotContain("tool_request");
        visible.Should().NotContain("AskUserQuestion");
    }

    [TestMethod]
    public void Parse_KeepsProseThatFollowsAToolCall()
    {
        const string payload = """<tool_call>{"name":"Read","arguments":{"file_path":"/tmp/a"}}</tool_call>接下来我会读取文件。""";

        var blocks = TraeToolProtocol.Parse(payload);

        blocks.OfType<TraeToolUseBlock>().Single().Name.Should().Be("Read");
        string.Concat(blocks.OfType<TraeTextBlock>().Select(b => b.Text)).Should().Be("接下来我会读取文件。");
    }

    [TestMethod]
    public void Parse_RepairsToolCallMissingOuterClosingBrace()
    {
        // Observed deepseek output: valid </tool_call> but the outer '}' was never emitted.
        const string payload = """<tool_call>{"name":"AskUserQuestion","questions":[{"question":"玩法?","options":["A","B"]}]</tool_call>""";

        var blocks = TraeToolProtocol.Parse(payload);

        var toolUse = blocks.OfType<TraeToolUseBlock>().Single();
        toolUse.Name.Should().Be("AskUserQuestion");
        toolUse.Input["questions"]!.AsArray().Should().HaveCount(1);
        string.Concat(blocks.OfType<TraeTextBlock>().Select(b => b.Text)).Should().NotContain("tool_call");
    }

    [TestMethod]
    public void Parse_EmitsFailureBlockInsteadOfLeakingUnparseablePayload()
    {
        // 关键回归：解析不出来时绝不能把 API key、文件内容之类的原始载荷当正文吐给客户端。
        const string payload = """<tool_call>totally not json, secret=sk-live-1234</tool_call>""";

        var blocks = TraeToolProtocol.Parse(payload);

        var failure = blocks.OfType<TraeToolCallFailureBlock>().Single();
        failure.RawPayload.Should().Contain("sk-live-1234");
        string.Concat(blocks.OfType<TraeTextBlock>().Select(b => b.Text)).Should().NotContain("sk-live-1234");
    }

    [TestMethod]
    public void Parse_EmitsFailureBlockWhenToolCallIsTruncated()
    {
        var blocks = TraeToolProtocol.Parse("""<tool_call>{"nam""");

        blocks.OfType<TraeToolCallFailureBlock>().Should().ContainSingle();
        string.Concat(blocks.OfType<TraeTextBlock>().Select(b => b.Text)).Should().NotContain("tool_call");
    }

    [TestMethod]
    public void TryParseArguments_RepairsMalformedArgumentJson()
    {
        TraeToolProtocol.TryParseArguments("""{"command":"ls",""", out JsonObject repaired).Should().BeTrue();
        repaired["command"]!.ToString().Should().Be("ls");

        TraeToolProtocol.TryParseArguments("", out JsonObject empty).Should().BeTrue();
        empty.Should().BeEmpty();

        TraeToolProtocol.TryParseArguments("not json at all", out _).Should().BeFalse();
    }

    [TestMethod]
    public void BuildSystemPrompt_DocumentsRawParameterFormForLongValues()
    {
        var tools = new JsonArray(new JsonObject
        {
            ["name"] = "Write",
            ["input_schema"] = new JsonObject { ["required"] = new JsonArray("file_path", "content") }
        });

        string prompt = TraeToolProtocol.BuildSystemPrompt(null, tools);

        prompt.Should().Contain("""<tool_call>{"name":"TOOL_NAME","arguments":{"PARAM_NAME":"PARAM_VALUE"}}</tool_call>""");
        prompt.Should().Contain("""<tool_call name="TOOL_NAME"><parameter name="PARAM_NAME">PARAM_VALUE</parameter></tool_call>""");
        // 模型抄过 "raw value" 并写进文件，占位符必须一眼看得出是占位符。
        prompt.Should().NotContain("raw value");
        prompt.Should().Contain("Emitting the placeholder text itself is a failure.");
    }

    [TestMethod]
    public void TryValidateToolUse_RejectsUnsubstitutedTemplatePlaceholders()
    {
        var tools = new JsonArray(new JsonObject
        {
            ["name"] = "Write",
            ["input_schema"] = new JsonObject { ["required"] = new JsonArray("file_path", "content") }
        });
        var toolUse = new TraeToolUseBlock("id", "Write", new JsonObject
        {
            ["file_path"] = "/tmp/a.html",
            ["content"] = "raw value"
        });

        TraeToolProtocol.TryValidateToolUse(toolUse, tools, out string error).Should().BeFalse();
        error.Should().Contain("placeholders").And.Contain("content");
    }
}