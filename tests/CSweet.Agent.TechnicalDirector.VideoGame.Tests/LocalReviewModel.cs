using System.Net.Http.Json;
using System.Text.Json;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class LocalReviewFactAttribute : FactAttribute
{
    public LocalReviewFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_REVIEW_MODEL_URL")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_REVIEW_MODEL")))
            Skip = "Set CSWEET_REVIEW_MODEL_URL and CSWEET_REVIEW_MODEL to exercise a local inference server.";
    }
}

// Test-only adapter. The real agent emits the messages and structured response schema. Only the
// synthetic audio fixture uses this opt-in path; all durable work and Git capabilities stay in memory.
internal sealed class LocalReviewModel : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(4) };
    public int Calls { get; private set; }
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public void ResetUsage() => Calls = InputTokens = OutputTokens = 0;

    public async Task<JsonElement> ReplyAsync(JsonElement request, CancellationToken ct)
    {
        var endpoint = Environment.GetEnvironmentVariable("CSWEET_REVIEW_MODEL_URL")!.TrimEnd('/') + "/chat/completions";
        var messages = request.GetProperty("messages").EnumerateArray().Select(m => new
            { role = m.GetProperty("role").GetString(), content = m.GetProperty("text").GetString() }).ToArray();
        var body = new { model = Environment.GetEnvironmentVariable("CSWEET_REVIEW_MODEL"), messages, temperature = 0,
            max_tokens = 4096, response_format = new { type = "json_schema", json_schema = new { name = "technical_review",
                schema = request.GetProperty("responseSchema") } } };
        using var response = await http.PostAsJsonAsync(endpoint, body, ct);
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var choice = reply.GetProperty("choices")[0];
        var usage = reply.GetProperty("usage");
        Calls++;
        InputTokens += usage.GetProperty("prompt_tokens").GetInt32();
        OutputTokens += usage.GetProperty("completion_tokens").GetInt32();
        return JsonSerializer.SerializeToElement(new { text = choice.GetProperty("message").GetProperty("content").GetString(),
            role = "assistant", finishReason = choice.GetProperty("finish_reason").GetString() });
    }

    public void Dispose() => http.Dispose();
}
