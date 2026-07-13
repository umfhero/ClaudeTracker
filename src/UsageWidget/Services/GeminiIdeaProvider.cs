using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UsageWidget.Services;

public sealed record IdeaResult(bool Ok, string? Idea, string? Error);

/// <summary>
/// Generates one short project idea via the Gemini API. Called at most once per day
/// (the caller caches the result in settings) so a free tier key is never tired out.
/// </summary>
public static class GeminiIdeaProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<IdeaResult> GenerateAsync(string apiKey, string model)
    {
        try
        {
            string prompt =
                "You write one short spark of a project idea for a hobby programmer to build. " +
                "Reply with exactly one sentence of at most 24 words. " +
                "Use plain English with no hyphens, no dashes, no emojis, no quotation marks, no markdown and no lists. " +
                "Sound casual and curious, in the spirit of these patterns without copying them exactly: " +
                "What if we could make something that does X. " +
                "Wish I had a tool for Y. " +
                "Let's build something new and bold today that helps with Z. " +
                "Make the idea concrete, fresh and buildable in a weekend. " +
                $"Vary it by today's date: {DateTime.Now:yyyy-MM-dd}. " +
                "Reply with the sentence only.";

            var body = new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } },
                generationConfig = new
                {
                    temperature = 1.3,
                    maxOutputTokens = 128,
                    thinkingConfig = new { thinkingBudget = 0 },
                },
            };

            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("x-goog-api-key", apiKey);

            using var response = await Http.SendAsync(request);
            string json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return new(false, null, $"HTTP {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(json);
            var text = new StringBuilder();
            if (doc.RootElement.TryGetProperty("candidates", out var candidates)
                && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0
                && candidates[0].TryGetProperty("content", out var content)
                && content.TryGetProperty("parts", out var parts)
                && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                    if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        text.Append(t.GetString());
            }

            string idea = Sanitise(text.ToString());
            return idea.Length > 0 ? new(true, idea, null) : new(false, null, "empty response");
        }
        catch (Exception ex)
        {
            return new(false, null, ex.Message);
        }
    }

    /// <summary>Enforces the plain English rule even if the model slips: no dashes,
    /// hyphens, quotes or markdown, single line.</summary>
    private static string Sanitise(string raw)
    {
        string s = raw
            .Replace('\r', ' ').Replace('\n', ' ')
            .Replace("—", ", ").Replace("–", ", ")
            .Replace("-", " ")
            .Replace("*", "").Replace("#", "").Replace("`", "")
            .Replace("\"", "").Replace("“", "").Replace("”", "");
        s = Regex.Replace(s, @"\p{Cs}|\p{So}", "");   // emoji and symbol glyphs
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length > 220) s = s[..220].TrimEnd();
        return s;
    }
}
