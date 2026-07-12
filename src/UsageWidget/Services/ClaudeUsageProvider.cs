using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using UsageWidget.Models;

namespace UsageWidget.Services;

public enum FetchStatus { Ok, NoCredentials, Unauthorized, NetworkError }

public sealed record FetchResult(FetchStatus Status, UsageSnapshot? Snapshot, string? Plan, string? Error);

/// <summary>
/// Reads Claude Code's local OAuth token (read-only — Claude Code owns its refresh)
/// and queries Anthropic's usage endpoint for the account's rate limits.
/// </summary>
public sealed class ClaudeUsageProvider
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static string CredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    public async Task<FetchResult> FetchAsync()
    {
        string? token = null;
        string? plan = null;
        try
        {
            if (!File.Exists(CredentialsPath))
                return new(FetchStatus.NoCredentials, null, null, "credentials file not found");

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(CredentialsPath));
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
            {
                if (oauth.TryGetProperty("accessToken", out var at)) token = at.GetString();
                if (oauth.TryGetProperty("subscriptionType", out var st)) plan = st.GetString();
            }
        }
        catch (Exception ex)
        {
            return new(FetchStatus.NoCredentials, null, null, ex.Message);
        }

        if (string.IsNullOrEmpty(token))
            return new(FetchStatus.NoCredentials, null, plan, "no access token in credentials");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

            using var response = await Http.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(FetchStatus.Unauthorized, null, plan, $"HTTP {(int)response.StatusCode}");
            response.EnsureSuccessStatusCode();

            var snapshot = Parse(await response.Content.ReadAsStringAsync());
            return new(FetchStatus.Ok, snapshot, plan, null);
        }
        catch (Exception ex)
        {
            return new(FetchStatus.NetworkError, null, plan, ex.Message);
        }
    }

    private static UsageSnapshot Parse(string json)
    {
        var limits = new List<LimitInfo>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("limits", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                string kind = el.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                double percent = el.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.GetDouble() : 0;
                DateTimeOffset? resetsAt = null;
                if (el.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(r.GetString(), out var dto))
                    resetsAt = dto;

                string label = kind switch
                {
                    "session" => "Session (5h)",
                    "weekly_all" => "Week · all models",
                    "weekly_scoped" => "Week · " + ScopeName(el),
                    _ => Humanize(kind),
                };
                limits.Add(new LimitInfo(kind, label, percent, resetsAt));
            }
        }

        // Older response shape: top-level five_hour / seven_day objects.
        if (limits.Count == 0)
        {
            AddLegacy(root, "five_hour", "Session (5h)", limits);
            AddLegacy(root, "seven_day", "Week · all models", limits);
        }

        return new UsageSnapshot(DateTimeOffset.Now, limits);
    }

    private static string ScopeName(JsonElement limit)
    {
        if (limit.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
            && scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object
            && model.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String)
            return dn.GetString()!;
        return "model";
    }

    private static void AddLegacy(JsonElement root, string prop, string label, List<LimitInfo> limits)
    {
        if (!root.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Object) return;
        double percent = el.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number
            ? u.GetDouble() : 0;
        DateTimeOffset? resetsAt = null;
        if (el.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), out var dto))
            resetsAt = dto;
        limits.Add(new LimitInfo(prop, label, percent, resetsAt));
    }

    private static string Humanize(string kind) =>
        string.IsNullOrEmpty(kind) ? "Limit" : char.ToUpper(kind[0]) + kind[1..].Replace('_', ' ');
}
