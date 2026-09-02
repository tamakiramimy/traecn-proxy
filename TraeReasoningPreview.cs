using System.Text;

namespace TrancnProxy;

public sealed class TraeReasoningPreview
{
    private const int MaximumLength = 480;
    private static readonly string[] StopMarkers =
    [
        "\n\n", "```", "<!DOCTYPE", "<html", "<head", "<body", "<style", "<script", "<tool_call"
    ];

    private readonly StringBuilder _pending = new();
    private int _length;

    public bool Stopped { get; private set; }

    public string Push(string text)
    {
        if (Stopped || string.IsNullOrEmpty(text)) return "";
        _pending.Append(text);
        string buffered = _pending.ToString();

        int boundary = buffered.Length;
        foreach (string marker in StopMarkers)
        {
            int index = buffered.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && index < boundary) boundary = index;
        }

        int remaining = MaximumLength - _length;
        int take = boundary < buffered.Length
            ? Math.Min(boundary, remaining)
            : Math.Min(Math.Max(0, buffered.Length - PartialMarkerLength(buffered)), remaining);
        string preview = take > 0 ? buffered[..take] : "";
        _pending.Remove(0, take);
        _length += take;
        if (boundary < buffered.Length || _length >= MaximumLength)
        {
            _pending.Clear();
            Stopped = true;
        }
        return preview;
    }

    private static int PartialMarkerLength(string value)
    {
        int maximum = StopMarkers.Max(marker => Math.Min(value.Length, marker.Length - 1));
        for (int length = maximum; length > 0; length--)
            if (StopMarkers.Any(marker => marker.StartsWith(value[^length..], StringComparison.OrdinalIgnoreCase)))
                return length;
        return 0;
    }
}