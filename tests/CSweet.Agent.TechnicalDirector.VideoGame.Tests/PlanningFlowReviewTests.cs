using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed partial class ProgressivePlanningTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Direction_review_reads_frozen_scope_and_assesses_questions_once_without_regenerating_graph(bool amendment)
    {
        var scope = Scope();
        scope = scope with { Plan = scope.Plan with { OpenFeasibilityDecisions = ["Choose an implementation parameter.", "Resolve idle direction."] } };
        var cycle = Cycle(); var directions = new string('d', 64);
        string[] managerDirections = ["Keep the idle treatment quiet within accepted scope."];
        var reviewed = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(managerDirections)));
        var key = SpecialistAgent.ProgressiveScopeKey(cycle, directions);
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(scope)));
        var states = new Dictionary<string, AgentOperatingStateResponse> { [key] = new(Guid.NewGuid(), key,
            "video-game.progressive-planning.v1", 1, "Active", new Dictionary<string, string>(), [], "scope", [],
            Guid.NewGuid(), JsonSerializer.SerializeToElement(scope), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) };
        var runtime = StateRuntime(states); var client = new Client();
        client.Responses.Enqueue(JsonSerializer.Serialize(new SpecialistAgent.PlanningQuestionAssessment([
            new(0, "delegated", "game-engineer", "Accepted implementation task."),
            new(1, amendment ? "authority" : "resolved", "video-game-director", "Bound manager direction.")], amendment,
            amendment ? "Direction requires different scope." : "No scope amendment needed."), Web));
        var request = new SpecialistAgent.ProgressivePlanningRequest("Direction", directions, key, digest, ReviewDirectionsDigest: reviewed, ManagerDirections: managerDirections);
        var result = await new SpecialistAgent().GenerateProgressivePlanningAsync(runtime.CreateContext(), client,
            [new(ChatRole.User, "Authenticated direction: keep the idle treatment within accepted scope.")],
            ["First playable\n\nAll levels and polish"], cycle, request, default);
        Assert.Equal(1, client.Calls); Assert.Equal(!amendment, result.Artifact is not null);
        if (!amendment)
        {
            var payload = result.Artifact!.Payload;
            Assert.Equal(JsonSerializer.Serialize(scope.Plan.DeliveryItems), JsonSerializer.Serialize(payload.GetProperty("DeliveryItems").Deserialize<
                IReadOnlyList<CrosswiredStudios.VideoGame.Contracts.GameProposedWorkItemV1>>()));
            Assert.Equal(digest, payload.GetProperty("progressivePlanning").GetProperty("outlineDigest").GetString());
            Assert.Equal(0, payload.GetProperty("progressivePlanning").GetProperty("reviewedAuthorityQuestions").GetArrayLength());
            var follow = (Task<IReadOnlyList<ChatMessage>>)typeof(SpecialistAgent).GetMethod("ApplyRetainedPlanningDirectionAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(new SpecialistAgent(),
                [key, digest, request with { Phase = "Story", ManagerDirections = null }, runtime.CreateContext(), Array.Empty<ChatMessage>(), CancellationToken.None])!;
            Assert.Contains(managerDirections[0], string.Join("\n", (await follow).Select(x => x.Text)));

        }
        await new SpecialistAgent().GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [],
            ["First playable\n\nAll levels and polish"], cycle, request, default);
        Assert.Equal(1, client.Calls); Assert.Equal(3, states.Count);
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("missing")] [InlineData("unknown-kind")] [InlineData("no-evidence")]
    public void Question_classification_requires_exact_coverage_and_evidenced_ownership(string scenario)
    {
        var first = new SpecialistAgent.PlanningQuestionDisposition(0, "delegated", "game-engineer", "Accepted task.");
        var second = first with { Index = 1 };
        var questions = scenario switch {
            "duplicate" => new[] { first, first }, "missing" => new[] { first },
            "unknown-kind" => new[] { first, second with { Disposition = "approved" } },
            _ => new[] { first, second with { Reason = "" } } };
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateQuestionAssessment(new(questions, false, "Review"), 2));
    }
}

public sealed class MechanicalReviewEvidenceTests
{
    [Fact]
    public void Valid_encoding_does_not_disprove_a_registry_checksum_mismatch()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(new {
            blockingFindings = new[] { new { problem = "Checksum differs from the expected registry value.",
                evidence = "The candidate has 'sha512-" + Convert.ToBase64String(new byte[64]) + "'." } } })));
        Assert.False(MechanicalReviewChatClient.ContradictsEncodingMeasurement(response));
    }

    [Theory]
    [InlineData(false, false, 2)] [InlineData(true, false, 1)] [InlineData(false, true, 2)]
    public async Task Contradictory_encoding_rejection_gets_one_correction_without_fabricating_approval(bool malformed, bool persistent, int calls)
    {
        var literal = Convert.ToBase64String(new byte[64]); if (malformed) literal = literal[1..];
        var bad = JsonSerializer.Serialize(new { blockingFindings = new[] { new { problem = "Malformed SHA-512 base64 length",
            evidence = "The literal 'sha512-" + literal + "' is invalid base64." } } });
        var model = new ScriptedClient([bad, persistent ? bad : "{\"blockingFindings\":[]}"]);
        using var client = new MechanicalReviewChatClient(model);
        if (persistent) await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new(ChatRole.User, "Review exact candidate.")]));
        else await client.GetResponseAsync([new(ChatRole.User, "Review exact candidate.")]);
        Assert.Equal(calls, model.Calls);
    }

    private sealed class ScriptedClient(IEnumerable<string> responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);
        public int Calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _responses.Dequeue()))); }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Hash_measurements_use_decoded_source_text_and_never_certify_authenticity(bool malformed)
    {
        const string observed = "mlsTRyGaPBjPedk6Bvw+aqbsXDtoAyAzm5MO7JgU+yVRyMQ5O8bD4Kcci7BS85f93veegeCPkL8R4GLClnjLFw==";
        var value = malformed ? observed[1..] : observed;
        ChatMessage[] messages = [new(ChatRole.User, JsonSerializer.Serialize(new { text = "+ \"integrity\": \"sha512-" + value + "\"" }))];
        using var evidence = JsonDocument.Parse(MechanicalReviewChatClient.Measurements(messages));
        var entry = Assert.Single(evidence.RootElement.EnumerateArray());
        Assert.Equal(!malformed, entry.GetProperty("validSha512Encoding").GetBoolean());
        if (!malformed) { Assert.Equal(88, entry.GetProperty("base64Characters").GetInt32()); Assert.Equal(64, entry.GetProperty("decodedBytes").GetInt32()); }
        var augmented = MechanicalReviewChatClient.Augment(messages);
        Assert.Same(messages[0], augmented[0]);
        Assert.Contains("not registry authenticity", augmented.Last().Text);
        Assert.Contains("Consolidate equivalent findings", augmented.Last().Text);
    }
}
