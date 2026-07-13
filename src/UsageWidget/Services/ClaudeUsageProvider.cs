using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UsageWidget.Models;

namespace UsageWidget.Services;

public enum FetchStatus { Ok, NoCredentials, Unauthorized, NetworkError }

public sealed record FetchResult(FetchStatus Status, UsageSnapshot? Snapshot, string? Plan, string? Error);

/// <summary>
/// Reads Claude Code's local OAuth token and queries Anthropic's usage endpoint for the
/// account's rate limits. When the access token has expired it refreshes it with the
/// stored refresh token (the same grant Claude Code uses) and writes the renewed tokens
/// back so Claude Code picks them up too — this lets the widget come up with live data
/// straight from PC startup without Claude Code ever being opened.
/// </summary>
public sealed class ClaudeUsageProvider
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";
    // Claude Code's public OAuth client id (PKCE public client, not a secret).
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string OAuthBeta = "oauth-2025-04-20";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static DateTimeOffset _lastRefreshAttempt = DateTimeOffset.MinValue;

    private static string CredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    private sealed record Creds(string AccessToken, string? RefreshToken, long ExpiresAtMs, string? Plan);

    public async Task<FetchResult> FetchAsync()
    {
        Creds? creds;
        try
        {
            creds = ReadCreds();
        }
        catch (Exception ex)
        {
            return new(FetchStatus.NoCredentials, null, null, ex.Message);
        }
        if (creds is null)
            return new(FetchStatus.NoCredentials, null, null, "no usable credentials found");

        // Refresh proactively when the token is expired or about to expire.
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (creds.ExpiresAtMs > 0 && nowMs > creds.ExpiresAtMs - 120_000)
            creds = await TryRefreshAsync(creds) ?? creds;

        var result = await FetchUsageAsync(creds);

        // A 401 means our token view was stale; refresh once and retry.
        if (result.Status == FetchStatus.Unauthorized)
        {
            var refreshed = await TryRefreshAsync(creds);
            if (refreshed is not null)
                result = await FetchUsageAsync(refreshed);
        }
        return result;
    }

    private static async Task<FetchResult> FetchUsageAsync(Creds creds)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
            request.Headers.Add("anthropic-beta", OAuthBeta);

            using var response = await Http.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(FetchStatus.Unauthorized, null, creds.Plan, $"HTTP {(int)response.StatusCode}");
            response.EnsureSuccessStatusCode();

            var snapshot = Parse(await response.Content.ReadAsStringAsync());
            return new(FetchStatus.Ok, snapshot, creds.Plan, null);
        }
        catch (Exception ex)
        {
            return new(FetchStatus.NetworkError, null, creds.Plan, ex.Message);
        }
    }

    private static Creds? ReadCreds()
    {
        if (!File.Exists(CredentialsPath)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;

        string? access = oauth.TryGetProperty("accessToken", out var at) ? at.GetString() : null;
        if (string.IsNullOrEmpty(access)) return null;
        string? refresh = oauth.TryGetProperty("refreshToken", out var rt) ? rt.GetString() : null;
        long expiresAt = oauth.TryGetProperty("expiresAt", out var ea) && ea.ValueKind == JsonValueKind.Number
            ? ea.GetInt64() : 0;
        string? plan = oauth.TryGetProperty("subscriptionType", out var st) ? st.GetString() : null;
        return new Creds(access, refresh, expiresAt, plan);
    }

    private static async Task<Creds?> TryRefreshAsync(Creds creds)
    {
        if (string.IsNullOrEmpty(creds.RefreshToken)) return null;
        // Don't hammer the token endpoint if refreshes are failing.
        if (DateTimeOffset.Now - _lastRefreshAttempt < TimeSpan.FromMinutes(5)) return null;
        _lastRefreshAttempt = DateTimeOffset.Now;

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                grant_type = "refresh_token",
                refresh_token = creds.RefreshToken,
                client_id = ClientId,
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("anthropic-beta", OAuthBeta);

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            string? access = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            if (string.IsNullOrEmpty(access)) return null;
            string? refresh = root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
                ? rt.GetString() : creds.RefreshToken;
            long expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
                ? ei.GetInt64() : 3600;
            long expiresAtMs = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds();

            WriteBackTokens(access, refresh, expiresAtMs);
            return creds with { AccessToken = access, RefreshToken = refresh, ExpiresAtMs = expiresAtMs };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Persists renewed tokens for Claude Code, touching only the claudeAiOauth fields
    /// and leaving the rest of the document untouched. Best effort: a failed write only
    /// means the next widget poll refreshes again.
    /// </summary>
    private static void WriteBackTokens(string access, string? refresh, long expiresAtMs)
    {
        try
        {
            string path = CredentialsPath;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return;
            if (root["claudeAiOauth"] is not JsonObject oauth) return;

            oauth["accessToken"] = access;
            if (!string.IsNullOrEmpty(refresh)) oauth["refreshToken"] = refresh;
            oauth["expiresAt"] = expiresAtMs;

            string tmp = path + ".widget-tmp";
            File.WriteAllText(tmp, root.ToJsonString());
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
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
