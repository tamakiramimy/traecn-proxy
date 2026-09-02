using System.Text.Json.Nodes;

namespace TrancnProxy;

public enum TraeOutputChannel
{
    Reasoning,
    Response
}

public enum TraeReasoningPresentation
{
    NativeThinking,
    Carrier
}

public sealed class TraeOutputClassifier
{
    private readonly TraeReasoningPresentation _reasoningPresentation;
    private readonly TraeToolProtocol.StreamParser _reasoningParser;
    private readonly TraeToolProtocol.StreamParser _responseParser;
    private bool _nativeThinkingOpen;

    public TraeOutputClassifier(
        TraeReasoningPresentation reasoningPresentation,
        bool streamToolCalls = false,
        JsonArray? tools = null)
    {
        _reasoningPresentation = reasoningPresentation;
        _reasoningParser = new TraeToolProtocol.StreamParser(streamToolCalls, tools);
        _responseParser = new TraeToolProtocol.StreamParser(streamToolCalls, tools);
    }

    public static TraeReasoningPresentation DefaultPresentation(TraeModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        string identity = $"{model.Id}\n{model.ConfigName}\n{model.DisplayName}";
        return CarrierModelMarkers.Any(marker => identity.Contains(marker, StringComparison.OrdinalIgnoreCase))
            ? TraeReasoningPresentation.Carrier
            : TraeReasoningPresentation.NativeThinking;
    }

    public IReadOnlyList<TraeOutputBlock> Push(TraeOutputChannel channel, string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return [];

        var blocks = new List<TraeOutputBlock>();
        if (channel == TraeOutputChannel.Response) CloseNativeThinking(blocks);
        var parsed = channel == TraeOutputChannel.Reasoning
            ? _reasoningParser.Push(chunk)
            : _responseParser.Push(chunk);
        AppendParsed(blocks, channel, parsed);
        return blocks;
    }

    public IReadOnlyList<TraeOutputBlock> Complete()
    {
        var blocks = new List<TraeOutputBlock>();
        AppendParsed(blocks, TraeOutputChannel.Reasoning, _reasoningParser.Complete());
        CloseNativeThinking(blocks);
        AppendParsed(blocks, TraeOutputChannel.Response, _responseParser.Complete());
        return blocks;
    }

    private void AppendParsed(
        List<TraeOutputBlock> destination,
        TraeOutputChannel channel,
        IReadOnlyList<TraeOutputBlock> parsed)
    {
        if (channel == TraeOutputChannel.Response || _reasoningPresentation == TraeReasoningPresentation.Carrier)
        {
            destination.AddRange(parsed);
            return;
        }

        foreach (TraeOutputBlock block in parsed)
        {
            switch (block)
            {
                case TraeTextBlock text:
                    OpenNativeThinking(destination);
                    destination.Add(new TraeThinkingDeltaBlock(text.Text));
                    break;
                case TraeThinkingStartBlock:
                    OpenNativeThinking(destination);
                    break;
                case TraeThinkingDeltaBlock delta:
                    OpenNativeThinking(destination);
                    destination.Add(delta);
                    break;
                case TraeThinkingEndBlock:
                    CloseNativeThinking(destination);
                    break;
                default:
                    CloseNativeThinking(destination);
                    destination.Add(block);
                    break;
            }
        }
    }

    private void OpenNativeThinking(List<TraeOutputBlock> destination)
    {
        if (_nativeThinkingOpen) return;
        _nativeThinkingOpen = true;
        destination.Add(new TraeThinkingStartBlock());
    }

    private void CloseNativeThinking(List<TraeOutputBlock> destination)
    {
        if (!_nativeThinkingOpen) return;
        _nativeThinkingOpen = false;
        destination.Add(new TraeThinkingEndBlock());
    }

    private static readonly string[] CarrierModelMarkers = ["glm", "kimi", "deepseek", "qwen"];
}