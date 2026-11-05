using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

// Measurements supplement the independent review; they never approve a candidate or run code.
internal sealed class MechanicalReviewChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    internal static string Measurements(IEnumerable<ChatMessage> messages)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        void Scan(string text)
        {
            foreach (Match match in Regex.Matches(text, "sha512-([A-Za-z0-9+/=]{1,2048})(?=[\\\"'\\s])"))
                values.Add(match.Groups[1].Value);
        }
        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String) Scan(element.GetString()!);
            else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) Visit(child);
            else if (element.ValueKind == JsonValueKind.Object) foreach (var child in element.EnumerateObject()) Visit(child.Value);
        }
        foreach (var message in messages)
        {
            try { using var document = JsonDocument.Parse(message.Text ?? ""); Visit(document.RootElement); }
            catch (JsonException) { Scan(message.Text ?? ""); }
        }
        return JsonSerializer.Serialize(values.Order(StringComparer.Ordinal).Take(128).Select(value => {
            int? bytes = null;
            try { bytes = Convert.FromBase64String(value).Length; } catch (FormatException) { }
            return new { literal = "sha512-" + value, base64Characters = value.Length, decodedBytes = bytes,
                validSha512Encoding = bytes == 64 && Convert.ToBase64String(Convert.FromBase64String(value)) == value };
        }));
    }

    internal static IReadOnlyList<ChatMessage> Augment(IEnumerable<ChatMessage> messages)
    {
        var original = messages.ToArray();
        return [.. original, new(ChatRole.System,
            "Use deterministic measurements for literal lengths and base64 validity; do not count long hashes by eye. " +
            "These measurements only describe supplied literals, not registry authenticity, command execution, whole-file validity or approval. " +
            "Keep real source/target, scope and independent QA checks. Consolidate equivalent findings about one root defect; " +
            "do not repeat the same defect as a lockfile, restoration and acceptance-criterion defect. " +
            "Deterministic literal measurements: " + Measurements(original))];
    }

    internal static bool ContradictsEncodingMeasurement(ChatResponse response)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Text);
            if (!document.RootElement.TryGetProperty("blockingFindings", out var findings)) return false;
            foreach (var finding in findings.EnumerateArray())
            {
                var text = string.Join(" ", new[] { "problem", "evidence" }.Select(key =>
                    finding.TryGetProperty(key, out var value) ? value.GetString() : ""));
                if (!Regex.IsMatch(text, "malformed.{0,40}SHA-512|invalid.{0,40}base64|invalid.{0,40}SHA-512.{0,40}(encoding|length)", RegexOptions.IgnoreCase)) continue;
                using var measured = JsonDocument.Parse(Measurements([new(ChatRole.User, text)]));
                var literals = measured.RootElement.EnumerateArray().ToArray();
                if (literals.Length > 0 && literals.All(x => x.GetProperty("validSha512Encoding").GetBoolean())) return true;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { } // The shared reviewer validates its full response contract.
        return false;
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var input = Augment(messages);
        var response = await base.GetResponseAsync(input, options, cancellationToken);
        if (!ContradictsEncodingMeasurement(response)) return response;
        response = await base.GetResponseAsync([.. input, new(ChatRole.Assistant, response.Text), new(ChatRole.System,
            "Your encoding finding contradicts deterministic measurement of its own quoted SHA-512 literal. " +
            "Reassess that claim once using measured base64 length and decoded bytes. Keep genuine defects and exact evidence requirements; " +
            "this correction does not imply approval. Return the same complete response contract.")], options, cancellationToken);
        if (ContradictsEncodingMeasurement(response))
            throw new InvalidOperationException("Review encoding finding still contradicts measured literal evidence; no rejection or approval was recorded.");
        return response;
    }
}
