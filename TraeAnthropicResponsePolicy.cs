internal static class TraeAnthropicResponsePolicy
{
    public const string RequiredToolMissingReason = "required tool was not called";

    public static bool ShouldBufferText(bool toolUseRequired, bool hasToolUse, bool isRecoveryMessage) =>
        toolUseRequired && !hasToolUse && !isRecoveryMessage;

    public static bool TryReserveRetry(IDictionary<string, int> retryCounts, string reason)
    {
        int limit = reason == RequiredToolMissingReason ? 3 : 1;
        int count = retryCounts.TryGetValue(reason, out int current) ? current : 0;
        if (count >= limit) return false;
        retryCounts[reason] = count + 1;
        return true;
    }

    public static string ToolFailureMessage(string? toolName, string reason)
    {
        if (reason == RequiredToolMissingReason)
        {
            string requiredTool = string.IsNullOrWhiteSpace(toolName) ? "execution tool" : $"'{toolName}' tool";
            return $"The model did not call the required {requiredTool} after multiple attempts. Please retry the request.";
        }

        return $"The tool call '{toolName}' was not executed because the model returned invalid arguments ({reason}). Please retry the request.";
    }
}