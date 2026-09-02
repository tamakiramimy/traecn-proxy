using System.Text.Json.Nodes;

namespace TrancnProxy;

public enum TraeReasoningEffort
{
    High,
    ExtraHigh
}

public enum TraeModelSelectionStrategy
{
    Manual,
    Max
}

public sealed record TraeChatTuning(
    TraeReasoningEffort? ReasoningEffort = null,
    TraeModelSelectionStrategy? ModelSelectionStrategy = null,
    int? ContextWindowSize = null)
{
    public const int DefaultExtraHighBudgetThreshold = 8192;

    public static TraeChatTuning FromAnthropic(
        JsonObject body,
        int extraHighBudgetThreshold = DefaultExtraHighBudgetThreshold)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (extraHighBudgetThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(extraHighBudgetThreshold));

        if (body["thinking"] is not JsonObject thinking ||
            !string.Equals((string?)thinking["type"], "enabled", StringComparison.OrdinalIgnoreCase))
            return new TraeChatTuning();

        int? budgetTokens = (int?)thinking["budget_tokens"];
        return new TraeChatTuning(
            budgetTokens > extraHighBudgetThreshold
                ? TraeReasoningEffort.ExtraHigh
                : TraeReasoningEffort.High);
    }

    public static TraeChatTuning FromOpenAI(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new TraeChatTuning(ParseEffort((string?)body["reasoning_effort"]));
    }

    public static TraeChatTuning FromResponses(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new TraeChatTuning(ParseEffort((string?)body["reasoning"]?["effort"]));
    }

    internal string? UpstreamReasoningEffort => ReasoningEffort switch
    {
        TraeReasoningEffort.High => "high",
        TraeReasoningEffort.ExtraHigh => "extra_high",
        _ => null
    };

    internal string? UpstreamModelSelectionStrategy => ModelSelectionStrategy switch
    {
        TraeModelSelectionStrategy.Manual => "manual",
        TraeModelSelectionStrategy.Max => "max",
        _ => null
    };

    public TraeChatTuning ApplyModel(TraeModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Variant switch
        {
            TraeModelVariant.Dev => this with
            {
                ModelSelectionStrategy = TraeModelSelectionStrategy.Manual,
                ContextWindowSize = model.DevContextWindow
            },
            TraeModelVariant.Max => this with
            {
                ModelSelectionStrategy = TraeModelSelectionStrategy.Max,
                ContextWindowSize = model.MaxContextWindow
            },
            _ => this
        };
    }

    private static TraeReasoningEffort? ParseEffort(string? effort) => effort?.ToLowerInvariant() switch
    {
        "high" => TraeReasoningEffort.High,
        "xhigh" or "extra_high" => TraeReasoningEffort.ExtraHigh,
        _ => null
    };
}