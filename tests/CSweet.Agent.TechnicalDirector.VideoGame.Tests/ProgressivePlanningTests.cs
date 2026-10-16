using System.Text.Json;
using CrosswiredStudios.VideoGame.Contracts;
using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class ProgressivePlanningTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static SpecialistAgent.ProgressiveScopeIndex Scope()
    {
        var example = SpecialistAgent.PlanningContractExample();
        var epic = example.DeliveryItems[0]; var story = example.DeliveryItems[1];
        return new(example with { DeliveryItems = [epic, story,
            epic with { ProposalKey = "later-epic", Title = "Remaining agreed game", DependencyProposalKeys = [epic.ProposalKey] },
            story with { ProposalKey = "later-story", ParentProposalKey = "later-epic", AcceptanceCriteria = ["All agreed levels and polish remain required"] }] },
            [new("document-0:paragraph-0", [story.ProposalKey]), new("document-0:paragraph-1", ["later-story"])]);
    }
    private static SpecialistAgent.ProgressiveStoryIndex Detail(SpecialistAgent.ProgressiveScopeIndex scope)
    {
        var example = SpecialistAgent.PlanningContractExample(); var story = scope.Plan.DeliveryItems[1];
        return new([example.DeliveryItems[2] with { AcceptanceCriteria = story.AcceptanceCriteria }, example.DeliveryItems[3]],
            [new("description", [example.DeliveryItems[2].ProposalKey]), new("criterion-0", [example.DeliveryItems[2].ProposalKey])]);
    }

    [Fact]
    public void Complete_outline_retains_every_source_without_future_executable_work()
    {
        var scope = Scope(); var sources = SpecialistAgent.AcceptedScopeSources(["First playable\n\nAll levels and polish"]);
        SpecialistAgent.ValidateProgressiveOutline(scope, sources);
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateProgressiveOutline(scope with { Coverage = scope.Coverage.Take(1).ToArray() }, sources));
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateProgressiveOutline(scope with {
            Plan = scope.Plan with { DeliveryItems = [.. scope.Plan.DeliveryItems, SpecialistAgent.PlanningContractExample().DeliveryItems[2]] } }, sources));
        var duplicate = scope.Plan.DeliveryItems[1] with { ProposalKey = "oversized-second-story" };
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateProgressiveOutline(scope with {
            Plan = scope.Plan with { DeliveryItems = [.. scope.Plan.DeliveryItems, duplicate] } }, sources));
    }

    [Fact]
    public void Refinement_is_bounded_to_one_story_and_preserves_criteria_and_dependency_closure()
    {
        var scope = Scope(); var story = scope.Plan.DeliveryItems[1]; var detail = Detail(scope);
        SpecialistAgent.ValidateStoryRefinement(detail, story, scope.Plan, []);
        foreach (var invalid in new[] {
            detail with { Items = detail.Items.Select(x => x with { ParentProposalKey = "later-story" }).ToArray() },
            detail with { Items = detail.Items.Select(x => x with { AcceptanceCriteria = new[] { "Weakened criterion" } }).ToArray() },
            detail with { Items = detail.Items.Select(x => x with { DependencyProposalKeys = new[] { "later-story" } }).ToArray() },
            detail with { Coverage = [] }
        }) Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateStoryRefinement(invalid, story, scope.Plan, []));
    }

    [Fact]
    public async Task Outline_is_returned_before_briefs_and_story_restart_reuses_immutable_checkpoints()
    {
        var scope = Scope(); var detail = Detail(scope); var states = new Dictionary<string, AgentOperatingStateResponse>();
        var now = DateTimeOffset.UtcNow;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) => {
                Assert.InRange(r.Payload.GetRawText().Length, 1, 65536);
                var value = new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, "Active",
                    new Dictionary<string,string>(), [], "binding", [], Guid.NewGuid(), r.Payload, states.GetValueOrDefault(r.StateKey)?.Revision + 1 ?? 1, now, now);
                states[r.StateKey] = value; return Task.FromResult(value);
            });
        var client = new Client(); client.Responses.Enqueue(JsonSerializer.Serialize(scope, Web));
        var cycle = new GameProductionPlanningCycleV1(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), new string('a',64),
            Guid.NewGuid(), 1, new string('b',64), "concept", "vision", new string('c',64));
        var agent = new SpecialistAgent(); var directions = new string('d',64);
        var first = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], ["First playable\n\nAll levels and polish"],
            cycle, new("Outline", directions), default);
        Assert.NotNull(first.Artifact); Assert.Equal(1, client.Calls);
        var evidence = first.Artifact.Payload.GetProperty("progressivePlanning");
        Assert.Empty(evidence.GetProperty("detailedStoryKeys").EnumerateArray());
        Assert.Equal(4, first.Artifact.Payload.GetProperty("DeliveryItems").GetArrayLength());
        client.Responses.Enqueue(JsonSerializer.Serialize(detail, Web));
        client.Responses.Enqueue("{\"items\":["); // real incident failure shape: only this brief page is retried
        client.Responses.Enqueue(JsonSerializer.Serialize(new SpecialistAgent.PlanningDescriptionPage(detail.Items.Select(x =>
            new SpecialistAgent.PlanningDescription(x.ProposalKey, x.Description)).ToArray()), Web));
        var request = new SpecialistAgent.ProgressivePlanningRequest("Story", directions, evidence.GetProperty("scopeKey").GetString(),
            evidence.GetProperty("outlineDigest").GetString(), "example-story", []);
        var second = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], ["First playable\n\nAll levels and polish"], cycle, request, default);
        Assert.NotNull(second.Artifact); Assert.Equal(4, client.Calls);
        var replay = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], ["First playable\n\nAll levels and polish"], cycle, request, default);
        Assert.NotNull(replay.Artifact); Assert.Equal(4, client.Calls);
        Assert.Equal(second.Artifact.Payload.GetRawText(), replay.Artifact.Payload.GetRawText());
        var stale = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], [], cycle,
            request with { OutlineDigest = new string('e',64) }, default);
        Assert.Null(stale.Artifact); Assert.Equal(4, client.Calls);
    }

    private sealed class Client : IChatClient
    {
        public readonly Queue<string> Responses = new(); public int Calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { Calls++; Assert.NotNull(options?.ResponseFormat); return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Responses.Dequeue())) { FinishReason = ChatFinishReason.Stop }); }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }
}
