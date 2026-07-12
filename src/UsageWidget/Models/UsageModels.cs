namespace UsageWidget.Models;

/// <summary>One rate limit as reported by the usage endpoint.</summary>
public sealed record LimitInfo(string Kind, string Label, double Percent, DateTimeOffset? ResetsAt);

public sealed record UsageSnapshot(DateTimeOffset FetchedAt, IReadOnlyList<LimitInfo> Limits);

public static class Formatting
{
    public static string FormatReset(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null) return "";
        var remaining = resetsAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "resetting…";
        if (remaining < TimeSpan.FromHours(24))
        {
            int h = (int)remaining.TotalHours;
            int m = remaining.Minutes;
            return h > 0 ? $"resets in {h}h {m}m" : $"resets in {Math.Max(1, m)}m";
        }
        return $"resets {resetsAt.Value.ToLocalTime():ddd HH:mm}";
    }
}
