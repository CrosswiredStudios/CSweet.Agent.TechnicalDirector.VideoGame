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
                Assert.InRange(r.Payload.GetRawText().Length, 1, 128000);
                var value = new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, "Active",
                    new Dictionary<string,string>(), [], "binding", [], Guid.NewGuid(), r.Payload, states.GetValueOrDefault(r.StateKey)?.Revision + 1 ?? 1, now, now);
                states[r.StateKey] = value; return Task.FromResult(value);
            });
        var client = new Client(); EnqueueOutline(client, scope);
        var cycle = new GameProductionPlanningCycleV1(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), new string('a',64),
            Guid.NewGuid(), 1, new string('b',64), "concept", "vision", new string('c',64));
        var agent = new SpecialistAgent(); var directions = new string('d',64);
        var first = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], ["First playable\n\nAll levels and polish"],
            cycle, new("Outline", directions), default);
        Assert.NotNull(first.Artifact); Assert.Equal(3, client.Calls);
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
        Assert.NotNull(second.Artifact); Assert.Equal(6, client.Calls);
        var replay = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], ["First playable\n\nAll levels and polish"], cycle, request, default);
        Assert.NotNull(replay.Artifact); Assert.Equal(6, client.Calls);
        Assert.Equal(second.Artifact.Payload.GetRawText(), replay.Artifact.Payload.GetRawText());
        var stale = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], [], cycle,
            request with { OutlineDigest = new string('e',64) }, default);
        Assert.Null(stale.Artifact); Assert.Equal(6, client.Calls);
    }

    private static SpecialistAgent.OutlineManifest Manifest(SpecialistAgent.ProgressiveScopeIndex scope) => new(
        scope.Plan.DeliveryItems.Where(x => x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Milestone).Select(x =>
            new SpecialistAgent.OutlineIncrement(x.ProposalKey, scope.Plan.DeliveryItems.Single(s => s.ParentProposalKey == x.ProposalKey).ProposalKey,
                x.Title, x.DependencyProposalKeys)).ToArray(), scope.Plan.FeasibilityFindings!, scope.Plan.TechnicalConstraints!, scope.Plan.OpenFeasibilityDecisions!);

    private static void EnqueueOutline(Client client, SpecialistAgent.ProgressiveScopeIndex scope)
    {
        var manifest = Manifest(scope);
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest)));
        client.Responses.Enqueue(JsonSerializer.Serialize(manifest, Web));
        foreach (var increments in manifest.Increments.Chunk(SpecialistAgent.PlanningPageSize))
        {
            var keys = increments.SelectMany(x => new[] { x.EpicKey, x.StoryKey }).ToHashSet();
            client.Responses.Enqueue(JsonSerializer.Serialize(new SpecialistAgent.OutlineItemPage(digest,
                scope.Plan.DeliveryItems.Where(x => keys.Contains(x.ProposalKey)).ToArray()), Web));
        }
        foreach (var coverage in scope.Coverage.Chunk(12))
            client.Responses.Enqueue(JsonSerializer.Serialize(new SpecialistAgent.OutlineCoveragePage(digest, coverage), Web));
    }

    private static AgentTestRuntime StateRuntime(Dictionary<string, AgentOperatingStateResponse> states) => new AgentTestRuntime()
        .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
            (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
        .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) => {
            Assert.InRange(r.Payload.GetRawText().Length, 1, 128000);
            var value = new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, "Active",
                new Dictionary<string,string>(), [], "binding", [], Guid.NewGuid(), r.Payload, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            Assert.False(states.ContainsKey(r.StateKey), "Accepted pages must not be rewritten.");
            states[r.StateKey] = value; return Task.FromResult(value);
        });

    private static GameProductionPlanningCycleV1 Cycle() => new(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), new string('a',64),
        Guid.NewGuid(), 1, new string('b',64), "concept", "vision", new string('c',64));

    [Theory]
    [InlineData(128000, true)]
    [InlineData(128001, false)]
    public async Task Planning_response_accepts_128000_characters_and_rejects_only_above_the_limit(int length, bool accepted)
    {
        var scope = Scope(); var client = new Client(); EnqueueOutline(client, scope);
        var manifest = client.Responses.Dequeue(); var page = client.Responses.Dequeue(); var coverage = client.Responses.Dequeue();
        client.Responses.Enqueue(manifest);
        for (var attempt = 0; attempt < (accepted ? 1 : 3); attempt++) client.Responses.Enqueue(page.PadRight(length));
        client.Responses.Enqueue(coverage);
        var states = new Dictionary<string, AgentOperatingStateResponse>();
        var result = await new SpecialistAgent().GenerateProgressivePlanningAsync(StateRuntime(states).CreateContext(), client, [],
            ["First playable\n\nAll levels and polish"], Cycle(), new("Outline", new string('d',64)), default);
        Assert.Equal(accepted, result.Artifact is not null);
        if (!accepted) { Assert.Contains("[csweet.planning.generator]", result.Content); Assert.Contains("128000", result.Content); }
    }

    [Fact]
    public async Task Outline_and_coverage_resume_only_missing_pages_after_exhaustion_and_reject_changed_checkpoints()
    {
        var original = Scope(); var epic = original.Plan.DeliveryItems[0]; var story = original.Plan.DeliveryItems[1];
        var scope = new SpecialistAgent.ProgressiveScopeIndex(original.Plan with { DeliveryItems = Enumerable.Range(0,5).SelectMany(i => new[] {
            epic with { ProposalKey = "epic-" + i, DependencyProposalKeys = i == 0 ? [] : ["epic-" + (i-1)] },
            story with { ProposalKey = "story-" + i, ParentProposalKey = "epic-" + i }
        }).ToArray() }, Enumerable.Range(0,25).Select(i => new SpecialistAgent.ScopeCoverage("document-0:paragraph-" + i, ["story-" + i%5])).ToArray());
        var documents = new[] { string.Join("\n\n", Enumerable.Range(0,25).Select(i => "Approved requirement " + i)) };
        var client = new Client(); EnqueueOutline(client, scope); var responses = client.Responses.ToArray(); client.Responses.Clear();
        // The manifest and first detail page persist; the second page exhausts its bounded correction.
        client.Responses.Enqueue(responses[0]); client.Responses.Enqueue(responses[1]);
        for (var i = 0; i < 3; i++) client.Responses.Enqueue("{\"items\":[");
        var states = new Dictionary<string, AgentOperatingStateResponse>(); var runtime = StateRuntime(states); var cycle = Cycle();
        var agent = new SpecialistAgent(); var request = new SpecialistAgent.ProgressivePlanningRequest("Outline", new string('d',64));
        var blocked = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], documents, cycle, request, default);
        Assert.Null(blocked.Artifact); Assert.Equal(2, states.Count); Assert.Equal(5, client.Calls);
        foreach (var response in responses.Skip(2)) client.Responses.Enqueue(response);
        var recovered = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], documents, cycle, request, default);
        Assert.NotNull(recovered.Artifact); Assert.Equal(9, client.Calls);
        Assert.Equal(10, recovered.Artifact.Payload.GetProperty("DeliveryItems").GetArrayLength());
        Assert.Equal(25, recovered.Artifact.Payload.GetProperty("progressivePlanning").GetProperty("scopeCoverage").GetArrayLength());
        var replay = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], documents, cycle, request, default);
        Assert.Equal(recovered.Artifact.Payload.GetRawText(), replay.Artifact!.Payload.GetRawText()); Assert.Equal(9, client.Calls);
        var key = states.Keys.Single(x => x.EndsWith(":outline:0"));
        var page = states[key].Payload.Deserialize<SpecialistAgent.OutlineItemPage>(Web)!;
        states[key] = states[key] with { Payload = JsonSerializer.SerializeToElement(page with {
            Items = page.Items.Select(x => x with { Description = "Unapproved changed scope" }).ToArray() }, Web) };
        var changed = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], documents, cycle, request, default);
        Assert.Null(changed.Artifact); Assert.Equal(9, client.Calls); Assert.Contains("immutable pages", changed.Content);
    }

    [Fact]
    public void Manifest_rejects_duplicate_identity_cycles_and_story_dependencies()
    {
        var manifest = Manifest(Scope()); var first = manifest.Increments[0];
        foreach (var bad in new[] {
            manifest with { Increments = [first, first] },
            manifest with { Increments = [first with { DependencyEpicKeys = [first.EpicKey] }] },
            manifest with { Increments = [first, manifest.Increments[1] with { DependencyEpicKeys = [first.StoryKey] }] }
        }) Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateOutlineManifest(bad));
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
