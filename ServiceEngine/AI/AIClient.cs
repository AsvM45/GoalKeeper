using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServiceEngine.Core;

namespace ServiceEngine.AI;

/// <summary>
/// HTTP client that talks to the Python Groq AI microservice on localhost:8099.
/// Always fails open (returns null) if the service is unavailable, so enforcement
/// falls back to static rules without blocking the UX.
/// </summary>
public sealed class AIClient
{
    private const string BaseUrl = "http://localhost:8099";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly ILogger<AIClient> _log;
    private readonly ScreenTimeLogger _db;

    public AIClient(ILogger<AIClient> log, ScreenTimeLogger db)
    {
        _log = log;
        _db = db;
        _http = new HttpClient { Timeout = Timeout, BaseAddress = new Uri(BaseUrl) };
    }

    /// <summary>
    /// Classifies a website/app against the user's goals.
    /// Returns null on any error (fail-open behavior).
    /// </summary>
    public async Task<AIClassifyResult?> ClassifyAsync(
        string urlOrApp,
        string? windowTitle,
        string appName,
        List<string> userGoals)
    {
        try
        {
            var payload = new ClassifyRequest(urlOrApp, windowTitle, appName, userGoals);
            var response = await _http.PostAsJsonAsync("/classify", payload);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<AIClassifyResult>();
        }
        catch (TaskCanceledException)
        {
            _log.LogDebug("AI service timeout for {App} – failing open", urlOrApp);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _log.LogDebug("AI service unavailable: {E}", ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Unexpected AI client error for {App}", urlOrApp);
            return null;
        }
    }

    /// <summary>
    /// Legacy video-title judgment endpoint.
    /// </summary>
    public async Task<bool?> JudgeVideoAsync(string videoTitle, string userGoal)
    {
        try
        {
            var payload = new { video_title = videoTitle, user_id = 1, goal = userGoal };
            var response = await _http.PostAsJsonAsync("/judge", payload);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            return result.TryGetProperty("allowed", out var allowed) && allowed.GetBoolean();
        }
        catch { return null; }
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record ClassifyRequest(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("window_title")] string? WindowTitle,
    [property: JsonPropertyName("app_name")] string AppName,
    [property: JsonPropertyName("user_goals")] List<string> UserGoals);

public record AIClassifyResult(
    [property: JsonPropertyName("judgment")] string Judgment,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("category")] string? Category);
