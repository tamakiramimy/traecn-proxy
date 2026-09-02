using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrancnProxy;

namespace TrancnProxy.Tests;

// 覆盖率由真实流量驱动：线上每条被拒绝的工具调用最终都落到 corpus/ 下，这里全量回放。
[TestClass]
public class TraeToolCorpusTests
{
    private static string CorpusDirectory => Path.Combine(AppContext.BaseDirectory, "corpus");

    public static IEnumerable<object[]> CorpusCases =>
        Directory.EnumerateFiles(CorpusDirectory, "*.txt")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new object[] { Path.GetFileName(path) });

    [TestMethod]
    public void Corpus_IsNotEmpty()
    {
        Directory.Exists(CorpusDirectory).Should().BeTrue("corpus 目录必须随测试一起发布");
        CorpusCases.Should().NotBeEmpty();
    }

    [TestMethod]
    [DynamicData(nameof(CorpusCases))]
    public void Replay_ParsesRecordedToolCallWithoutLeakingPayload(string fileName)
    {
        string path = Path.Combine(CorpusDirectory, fileName);
        (string expectedTool, string payload) = Load(path);

        var blocks = TraeToolProtocol.Parse(payload, ToolSchema(path));

        blocks.OfType<TraeToolCallFailureBlock>().Should()
            .BeEmpty($"{fileName} 应能被解析为工具调用");
        blocks.OfType<TraeToolUseBlock>().Select(block => block.Name)
            .Concat(blocks.OfType<TraeToolUseStartBlock>().Select(block => block.Name))
            .Should().Contain(expectedTool, $"{fileName} 声明的工具是 {expectedTool}");

        string visible = string.Concat(blocks.OfType<TraeTextBlock>().Select(block => block.Text));
        foreach (string marker in (string[])["tool_call", "tool_calls", "parameter", "arg_value", "function_call"])
            visible.Should().NotContain(marker, $"{fileName} 不能把 <{marker}> 泄漏成正文");
        visible.Should().NotContain(expectedTool);
    }

    // 语料声明的工具定义，用于把 <arg_value> 这类匿名参数回填成真实参数名。
    private static JsonArray? ToolSchema(string path)
    {
        string spec = Header(path, "# tools:");
        if (spec.Length == 0) return null;

        var tools = new JsonArray();
        foreach (string entry in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split(':', 2);
            var required = new JsonArray();
            if (parts.Length == 2)
                foreach (string property in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    required.Add(property.Trim());
            tools.Add(new JsonObject
            {
                ["name"] = parts[0].Trim(),
                ["input_schema"] = new JsonObject { ["required"] = required }
            });
        }
        return tools;
    }

    public static IEnumerable<object[]> ArgumentCases =>
        Directory.EnumerateFiles(Path.Combine(CorpusDirectory, "arguments"), "*.txt")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new object[] { Path.GetFileName(path) });

    // 这些载荷是线上抓到的：标准解析与 jsonrepair 都失败，只能靠结构化抢救，或者判定为不可信而整体作废。
    [TestMethod]
    [DynamicData(nameof(ArgumentCases))]
    public void Replay_SalvagesRecordedArgumentPayload(string fileName)
    {
        string path = Path.Combine(CorpusDirectory, "arguments", fileName);
        string keys = Header(path, "# keys:");
        string payload = Body(path);

        bool salvaged = TraeToolProtocol.TryParseArguments(payload, out JsonObject arguments);

        if (keys == "<none>")
        {
            salvaged.Should().BeFalse($"{fileName} 的参数不可信，必须整体作废并触发重试");
            return;
        }
        salvaged.Should().BeTrue($"{fileName} 至少要抢救出畸形点之前的参数");
        arguments.Select(pair => pair.Key).Should()
            .BeEquivalentTo(keys.Split(',', StringSplitOptions.RemoveEmptyEntries));
    }

    [TestMethod]
    public void Record_AppendsFailurePayloadAsJsonLine()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"trancn-corpus-{Guid.NewGuid():N}");
        try
        {
            TraeToolCorpus.Configure(directory);
            TraeToolCorpus.Record("Write", "arguments are not valid JSON", """{"name":"Write","arguments":{""");

            string file = Directory.EnumerateFiles(directory, "*.jsonl").Single();
            var entry = JsonNode.Parse(File.ReadAllLines(file).Single())!.AsObject();
            entry["tool"]!.ToString().Should().Be("Write");
            entry["reason"]!.ToString().Should().Be("arguments are not valid JSON");
            entry["payload"]!.ToString().Should().Be("""{"name":"Write","arguments":{""");
        }
        finally
        {
            TraeToolCorpus.Configure(null);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Record_IsInertUntilConfigured()
    {
        TraeToolCorpus.Configure(null);
        Action record = () => TraeToolCorpus.Record("Write", "reason", "payload");
        record.Should().NotThrow();
    }

    private static (string Tool, string Payload) Load(string path) =>
        (Header(path, "# tool:"), Body(path));

    private static string Header(string path, string prefix) =>
        File.ReadLines(path).FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))
            ?[prefix.Length..].Trim() ?? "";

    private static string Body(string path) =>
        string.Join("\n", File.ReadLines(path).Where(line => !line.StartsWith('#'))).Trim('\n');
}
