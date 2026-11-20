using System.Text.Json;
using System.Text.Json.Nodes;
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
        var measurements = Measurements(original);
        if (measurements == "[]") return original;
        return [.. original, new(ChatRole.System,
            "Use deterministic measurements for literal lengths and base64 validity; do not count long hashes by eye. " +
            "These measurements only describe supplied literals, not registry authenticity, command execution, whole-file validity or approval. " +
            "Keep real source/target, scope and independent QA checks. Consolidate equivalent findings about one root defect; " +
            "do not repeat the same defect as a lockfile, restoration and acceptance-criterion defect. " +
            "Deterministic literal measurements: " + measurements)];
    }

    internal static bool ContradictsEncodingMeasurement(ChatResponse response)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Text);
            if (!document.RootElement.TryGetProperty("blockingFindings", out var findings) &&
                !document.RootElement.TryGetProperty("additionalFindings", out findings)) return false;
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
        var input = Augment(messages).ToArray();
        // The SDK binds judgments by position. Give the provider the same cardinality the validator enforces.
        // The first user message is the SDK's structured review input, including on schema corrections.
        if (options?.ResponseFormat is ChatResponseFormatJson { Schema: { } schema } format &&
            input.Length >= 2 && input[0].Role == ChatRole.System && input[1].Role == ChatRole.User)
        {
            var guidance = "Review this ticket's changed behavior, its requirements and declared verification. " +
                "Use applicable constraints to check how the change is implemented. Missing later roadmap deliverables " +
                "do not establish a defect in this ticket. Report concrete defects introduced by the patch, including violations of applicable constraints.";
            if (schema.GetProperty("properties").TryGetProperty("criteria", out _))
            {
                using var evidence = JsonDocument.Parse(input[1].Text!);
                var data = evidence.RootElement;
                var criteria = data.GetProperty("reference").GetProperty("planning").GetProperty("acceptanceCriteria").GetArrayLength();
                var findings = data.GetProperty("findings").GetArrayLength();
                var bounded = JsonNode.Parse(schema.GetRawText())!.AsObject();
                void SetLength(string name, int length)
                {
                    var array = bounded["properties"]![name]!;
                    array["minItems"] = length;
                    array["maxItems"] = length;
                }
                SetLength("criteria", criteria);
                SetLength("resolutions", findings);
                options = options.Clone();
                options.ResponseFormat = ChatResponseFormat.ForJsonSchema(JsonSerializer.SerializeToElement(bounded), format.SchemaName);
                guidance += $" Return exactly {criteria} criterion judgments, one per entry in reference.planning.acceptanceCriteria in order, " +
                    $"and exactly {findings} finding resolutions. Constraints, notes and receipts supply evidence and never create extra grading rows.";
            }
            input[0] = new(ChatRole.System, input[0].Text + " " + guidance);
        }
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
