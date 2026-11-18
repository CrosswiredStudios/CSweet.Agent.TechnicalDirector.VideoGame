using System.Text.Json;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed partial class ProgressivePlanningTests
{
    [Fact]
    public void Prism_break_binds_the_engineers_selected_setup_to_code_and_preserves_the_separate_research_spike()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "prism-break-setup-spike.json"));
        var drafts = JsonSerializer.Deserialize<SpecialistAgent.StoryTaskDrafts>(json, JsonSerializerOptions.Web)!;
        var story = Scope().Plan.DeliveryItems[1] with { AcceptanceCriteria = Enumerable.Range(0, 9).Select(i => $"Story criterion {i}").ToArray() };
        var stack = new SpecialistAgent.ProjectStack("Canvas 2D", "JavaScript", [], "Fits the game", "Launch the graybox.", null);
        var result = SpecialistAgent.BindStoryTasks(drafts, story, [], stack);
        Assert.Equal(VideoGameWorkItemTypeKeys.Task, result.Items[0].WorkItemTypeKey);
        Assert.Equal(VideoGameRoleKeys.Engineer, result.Items[0].AccountableRoleKey);
        Assert.Contains(stack.SetupCriterion, result.Items[0].AcceptanceCriteria);
        Assert.Equal(VideoGameWorkItemTypeKeys.ResearchSpike, result.Items[1].WorkItemTypeKey);
        Assert.DoesNotContain(stack.SetupCriterion, result.Items[1].AcceptanceCriteria);
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.BindStoryTasks(drafts with { SetupTaskIndex = 1 }, story, [], stack));
    }

    [Fact]
    public void Sprint_planning_preserves_technical_authority_over_engine_recommendations()
    {
        Assert.Contains("The Technical Director owns engine choices within the approved game scope", SpecialistAgent.PlanningAuthorityPolicy);
        Assert.Contains("Coordinate the resulting ticket changes with the Producer", SpecialistAgent.PlanningAuthorityPolicy);
        Assert.DoesNotContain("A proposed engine replacement,", SpecialistAgent.PlanningAuthorityPolicy);
    }

    [Fact]
    public void First_increment_binds_executable_tasks_to_the_frozen_Story_and_criteria()
    {
        var story = Scope().Plan.DeliveryItems[1] with { AcceptanceCriteria = ["The scene responds to input.", "Record the observed response."] };
        var scene = new SpecialistAgent.StoryTaskDraft("Build the scene", "Deliver a running scene.", VideoGameWorkItemTypeKeys.Task,
            VideoGameRoleKeys.Engineer, [], [], [0], ["The scene opens."], "canvas", [0], [], []);
        var report = scene with { Title = "Record the observation", StoryCriterionIndexes = [1], EarlierTaskIndexes = [0] };
        var result = SpecialistAgent.BindStoryTasks(new([scene, report]), story, []);
        foreach (var lockfile in new string?[] { "example.lock", null })
        {
            var stack = new SpecialistAgent.ProjectStack("Independent engine", "Example language", [], "Fits the game", "Launch the demo.", lockfile);
            var bound = SpecialistAgent.BindStoryTasks(new([scene, report], 0), story, [], stack);
            Assert.Equal(2, bound.Items.Count);
            Assert.Equal(stack.SetupCriterion, bound.Items[0].AcceptanceCriteria.Last());
            Assert.StartsWith("From a clean checkout, launch", stack.SetupCriterion);
            Assert.Equal(lockfile is not null, stack.SetupCriterion.Contains("example.lock"));
            Assert.Equal(result.Items[1].AcceptanceCriteria, bound.Items[1].AcceptanceCriteria);
            Assert.Throws<InvalidOperationException>(() => SpecialistAgent.BindStoryTasks(new([scene, report], 9), story, [], stack));
        }
        Assert.All(result.Items, t => Assert.Equal(story.ProposalKey, t.ParentProposalKey));
        Assert.Equal(story.AcceptanceCriteria[0], result.Items[0].AcceptanceCriteria[0]);
        Assert.Equal([result.Items[0].ProposalKey], result.Items[1].DependencyProposalKeys);
        Assert.Equal([result.Items[1].ProposalKey], result.Coverage.Single(c => c.SourceKey == "criterion-1").ProposalKeys);
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.BindStoryTasks(new([scene with { WorkItemTypeKey = VideoGameWorkItemTypeKeys.Feature }]), story, []));
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.BindStoryTasks(new([scene with { EarlierTaskIndexes = [0] }]), story, []));
        var schema = SpecialistAgent.PlanningResponseSchema(typeof(SpecialistAgent.StoryTaskDrafts)).GetRawText();
        foreach (var platformField in new[] { "parentProposalKey", "proposalKey", "criterionBindings", "requiredCapabilityKeys", VideoGameWorkItemTypeKeys.Feature })
            Assert.DoesNotContain(platformField, schema);
    }

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
        // Fresh task indexes must declare browser evidence since 6.2.0; the engineering task needs the running scene.
        return new([example.DeliveryItems[2] with { AcceptanceCriteria = story.AcceptanceCriteria, BrowserVerification = GameBrowserVerification.Canvas },
                example.DeliveryItems[3] with { BrowserVerification = GameBrowserVerification.None }],
            [new("description", [example.DeliveryItems[2].ProposalKey]), new("criterion-0", [example.DeliveryItems[2].ProposalKey])]);
    }

    [Fact]
    public async Task Prism_break_roadmap_retries_name_the_bad_reference_and_publish_the_corrected_graph()
    {
        string Reply(int attempt) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures",
            $"prism-break-roadmap-{attempt}.json"));
        var agent = new SpecialistAgent();
        var runtime = StateRuntime(new Dictionary<string, AgentOperatingStateResponse>());
        var client = new Client();
        var cycle = Cycle();
        var sources = Enumerable.Range(0, 407).Select(i => new GameOutcomeSource($"approved-source-{i}", Guid.Empty,
            Guid.Empty, "digest", "author", $"Accepted paragraph {i}")).ToArray();
        var request = new SpecialistAgent.ProgressivePlanningRequest("Outline", new string('d', 64), Adaptive: true);
        client.Responses.Enqueue(JsonSerializer.Serialize(new SpecialistAgent.ApprovedCommitments(sources.Select((s, i) =>
            new SpecialistAgent.ApprovedCommitment(s.ApprovedText, [i])).ToArray()), Web));
        for (int i = 1; i <= 3; i++) client.Responses.Enqueue(Reply(i));
        var blocked = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], [], cycle, request, default, sources);
        Assert.Equal("Blocked", blocked.Disposition);
        Assert.Contains("Epic 0 (Canonical approved scope, constraints, and procedural asset baseline) sourceIndexes contains 407 outside the supplied range 0..406", blocked.Content);
        Assert.Contains("Epic 9 (Acceptance evidence pack and full scope traceability) sourceIndexes contains 407", client.Prompts[3].Last().Text);
        Assert.Contains(client.Prompts[3], m => m.Role == ChatRole.Assistant && m.Text == Reply(2));
        var ledger = client.Prompts[0].Single(m => m.Text!.StartsWith("Exact accepted source ledger"));
        Assert.Contains("\"sourceIndex\":406", ledger.Text);
        var schema = SpecialistAgent.PlanningResponseSchema(typeof(SpecialistAgent.RoadmapDraft), sources.Length);
        Assert.Contains("\"maximum\":406", JsonSerializer.Serialize(schema));

        // Simulate the model correcting the single bad citation in its last reply.
        var draft = JsonSerializer.Deserialize<SpecialistAgent.RoadmapDraft>(Reply(3), Web)!;
        var corrected = draft with { Epics = draft.Epics.Select((epic, i) => i == 0
            ? epic with { SourceIndexes = epic.SourceIndexes.Where(s => s != 407).ToArray() } : epic).ToArray() };
        client.Responses.Enqueue(JsonSerializer.Serialize(corrected, Web));
        client.Responses.Enqueue(JsonSerializer.Serialize(new SpecialistAgent.PlanningQuestionAssessment(
            draft.OpenFeasibilityDecisions.Select((_, i) => new SpecialistAgent.PlanningQuestionDisposition(i, "authority",
                "game-director", "Assess the recorded audio scope decision.")).ToArray(), false, "Preserve the review question."), Web));
        var ready = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], [], cycle, request, default, sources);
        Assert.True(ready.Artifact is not null, ready.Content);
        var payload = ready.Artifact.Payload;
        Assert.Equal(draft.Epics.Count, payload.GetProperty("DeliveryItems").GetArrayLength());
        var coverage = payload.GetProperty("progressivePlanning").GetProperty("scopeCoverage").EnumerateArray().ToArray();
        Assert.Equal(sources.Select(s => s.Key), coverage.Select(s => s.GetProperty("citations")[0].GetString()));
        Assert.All(coverage, source => Assert.NotEmpty(source.GetProperty("proposalKeys").EnumerateArray()));
    }

    [Theory]
    [InlineData("Compact canvas", "package-lock.json", true)]
    [InlineData("Native engine", null, true)]
    [InlineData("Later choice", null, false)]
    public async Task Adaptive_roadmap_and_selected_story_replay_without_model_generation(string engine, string? lockfile, bool stackAtOutline)
    {
        var runtime = StateRuntime(new Dictionary<string, AgentOperatingStateResponse>());
        var client = new Client();
        var agent = new SpecialistAgent();
        var stack = new SpecialistAgent.ProjectStack(engine, "Project language", ["Physics library"], "Fits the approved mechanics.", "Launch the first scene.", lockfile);
        var directions = new string('d', 64);
        var cycle = new GameProductionPlanningCycleV1(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), new string('a',64),
            Guid.NewGuid(), 1, new string('b',64), "concept", "vision", new string('c',64));
        var detail = Detail(Scope());
        var text = detail.Items[0].AcceptanceCriteria.Single();
        var roadmap = new SpecialistAgent.RoadmapDraft([
            new("Playable loop", "Verify the core interaction", [text], [0], []),
            new("Scoring", "Award points for hits", ["Each brick scores once"], [1], [0])], [], [], []);
        var inventory = new SpecialistAgent.ApprovedCommitments([
            new("Prove the rebound", [1]), new("Score each brick once", [1, 2])]);
        client.Responses.Enqueue(JsonSerializer.Serialize(inventory, Web));
        client.Responses.Enqueue(JsonSerializer.Serialize(roadmap, Web));
        string[] docs = ["# History\n\nVerify rebound and scoring\n\nEach brick scores once"];
        var outline = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], docs, cycle,
            new("Outline", directions, Adaptive: true), default, stack: stackAtOutline ? stack : null);
        Assert.True(outline.Artifact != null, outline.Content);
        Assert.Equal(2, client.Calls);
        var semantic = outline.Artifact!.Payload.GetProperty("progressivePlanning").GetProperty("scopeCoverage");
        Assert.Equal(2, semantic.GetArrayLength());
        Assert.Equal("Score each brick once", semantic[1].GetProperty("statement").GetString());
        Assert.DoesNotContain("document-0:paragraph-0", semantic.GetRawText());
        var outlineEvidence = outline.Artifact!.Payload.GetProperty("progressivePlanning");
        var published = outline.Artifact.Payload.Deserialize<GameTechnicalDeliveryProposalV1>()!;
        Assert.Equal(stackAtOutline, published.TechnicalConstraints.Contains(stack.DecisionSummary));
        var key = "roadmap-epic-1.story-1";
        client.Responses.Enqueue(JsonSerializer.Serialize(new SpecialistAgent.StorySlices([
            new("Rebound", "Prove the core loop", [], [0], [])]), Web));
        var drafts = new SpecialistAgent.StoryTaskDrafts(detail.Items.Select((t, i) => new SpecialistAgent.StoryTaskDraft(
            t.Title, t.Description, t.WorkItemTypeKey, t.AccountableRoleKey, t.RequiredSpecializationKeys, t.PreferredSpecializationKeys,
            i == 0 ? [0] : [], i == 0 ? [] : t.AcceptanceCriteria, t.BrowserVerification!, [], i == 0 ? [] : [0], [])).ToArray(), 0);
        client.Responses.Enqueue(JsonSerializer.Serialize(drafts, Web));
        client.Responses.Enqueue(JsonSerializer.Serialize(new SpecialistAgent.PlanningDescriptionPage(detail.Items.Select((t, i) =>
            new SpecialistAgent.PlanningDescription($"{key}.task-{i + 1}", t.Description)).ToArray()), Web));
        var request = new SpecialistAgent.ProgressivePlanningRequest("Story", directions, outlineEvidence.GetProperty("scopeKey").GetString(),
            outlineEvidence.GetProperty("outlineDigest").GetString(), key, [], Adaptive: true, DraftBasis: new string('f',64));
        var ready = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], docs, cycle, request, default, stack: stack);
        Assert.True(ready.Artifact != null, ready.Content);
        var proposal = ready.Artifact!.Payload.Deserialize<GameTechnicalDeliveryProposalV1>()!;
        Assert.Single(proposal.TechnicalConstraints, constraint => constraint == stack.DecisionSummary);
        var setup = Assert.Single(proposal.DeliveryItems, item => item.AcceptanceCriteria.Contains(stack.SetupCriterion));
        Assert.Equal(VideoGameRoleKeys.Engineer, setup.AccountableRoleKey);
        Assert.Equal(lockfile is not null, stack.SetupCriterion.Contains("package-lock.json"));
        var count = client.Calls;
        var replay = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], docs, cycle, request, default, stack: stack);
        Assert.Equal(ready.Artifact!.Payload.GetRawText(), replay.Artifact!.Payload.GetRawText());
        Assert.Equal(count, client.Calls);
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
    public void A_rejected_task_list_names_the_rule_it_broke_and_the_offending_items()
    {
        // Qwen Games Prism Break, November 17: a Story returned as one item of type video-game.feature.v1 was
        // rejected three times with the same sentence about task count.
        var scope = Scope(); var story = scope.Plan.DeliveryItems[1]; var detail = Detail(scope);
        string Rejected(SpecialistAgent.ProgressiveStoryIndex index, int maximum = 12) =>
            Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateStoryRefinement(index, story, scope.Plan, [], maximum)).Message;
        var asStory = Rejected(detail with { Items = [detail.Items[0] with { WorkItemTypeKey = VideoGameWorkItemTypeKeys.Feature }] });
        Assert.Contains("container type", asStory); Assert.Contains(detail.Items[0].ProposalKey, asStory); Assert.Contains(VideoGameWorkItemTypeKeys.Feature, asStory);
        var misplaced = Rejected(detail with { Items = detail.Items.Select(x => x with { ParentProposalKey = "later-story" }).ToArray() });
        Assert.Contains("parentProposalKey must be exactly " + story.ProposalKey, misplaced); Assert.Contains("later-story", misplaced);
        Assert.Contains("at most 1", Rejected(detail with { Items = [.. detail.Items, detail.Items[0] with { ProposalKey = "extra" }] }, 1));
        Assert.Contains("Return items", Rejected(detail with { Items = [] }));
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
        var outlineProgress = Assert.Single(runtime.Progress, x => x.GetProperty("stage").GetString() == "scope-outline");
        Assert.Equal(1, outlineProgress.GetProperty("completedPages").GetInt32());
        var coverageProgress = Assert.Single(runtime.Progress, x => x.GetProperty("stage").GetString() == "scope-coverage");
        Assert.Equal(2, coverageProgress.GetProperty("completedSources").GetInt32());
        Assert.Equal(2, coverageProgress.GetProperty("totalSources").GetInt32());
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
        var storyPrompt = string.Join("\n", client.Prompts[3].Select(x => x.Text));
        Assert.Contains("Frozen shared technical constraints", storyPrompt);
        Assert.Contains("Exact accepted source ledger", storyPrompt);
        Assert.Contains(JsonSerializer.Serialize(scope.Plan.DeliveryItems[1].AcceptanceCriteria[0], Web).Trim('"'), storyPrompt);
        var replay = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], ["First playable\n\nAll levels and polish"], cycle, request, default);
        Assert.NotNull(replay.Artifact); Assert.Equal(6, client.Calls);
        Assert.Equal(second.Artifact.Payload.GetRawText(), replay.Artifact.Payload.GetRawText());
        var stale = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], [], cycle,
            request with { OutlineDigest = new string('e',64) }, default);
        Assert.Null(stale.Artifact); Assert.Equal(6, client.Calls);
    }

    [Fact]
    public async Task Story_replay_keeps_the_authored_tasks_when_an_engine_is_named()
    {
        var scope = Scope();
        // A future scope container legitimately uses the reserved-looking identity.
        scope = scope with { Plan = scope.Plan with { TechnicalConstraints = ["Engine: Phaser"], DeliveryItems = scope.Plan.DeliveryItems.Select(x =>
            x.ProposalKey == "later-story" ? x with { ProposalKey = "dependency-pin-phaser" } : x).ToArray() },
            Coverage = scope.Coverage.Select(x => x with { ProposalKeys = x.ProposalKeys.Select(k => k == "later-story" ? "dependency-pin-phaser" : k).ToArray() }).ToArray() };
        var detail = Detail(scope) with { Items = Detail(scope).Items.Select(x => x.AccountableRoleKey == VideoGameRoleKeys.Engineer
            ? x with { Description = x.Description + " Build the Phaser scene." } : x).ToArray() };
        var story = scope.Plan.DeliveryItems[1];
        var bound = SpecialistAgent.BindStoryDependencyPins(detail, story, scope.Plan, []);
        SpecialistAgent.ValidateStoryRefinement(bound, story, scope.Plan, [], 13);
        Assert.Equal(detail.Items, bound.Items);
        Assert.Equal(detail.Coverage, bound.Coverage);
        var client = new Client(); EnqueueOutline(client, scope);
        var states = new Dictionary<string, AgentOperatingStateResponse>(); var runtime = StateRuntime(states);
        var cycle = Cycle(); var agent = new SpecialistAgent(); var directions = new string('d',64);
        var first = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], ["First playable\n\nAll levels and polish"], cycle, new("Outline", directions), default);
        Assert.NotNull(first.Artifact);
        var evidence = first.Artifact.Payload.GetProperty("progressivePlanning");
        client.Responses.Enqueue(JsonSerializer.Serialize(detail, Web));
        // Exhaustion saves the raw and bound index but does not claim any completed work.
        for (var attempt = 0; attempt < 3; attempt++) client.Responses.Enqueue("{bad-json");
        var request = new SpecialistAgent.ProgressivePlanningRequest("Story", directions, evidence.GetProperty("scopeKey").GetString(),
            evidence.GetProperty("outlineDigest").GetString(), story.ProposalKey, []);
        var failed = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], ["First playable\n\nAll levels and polish"], cycle, request, default);
        Assert.Null(failed.Artifact);
        Assert.DoesNotContain(states.Keys, x => x.Contains(":bound:"));
        var originals = states.ToDictionary(x => x.Key, x => x.Value.Payload.GetRawText());
        foreach (var page in bound.Items.Chunk(SpecialistAgent.PlanningPageSize))
            client.Responses.Enqueue(JsonSerializer.Serialize(new SpecialistAgent.PlanningDescriptionPage(page.Select(x =>
                new SpecialistAgent.PlanningDescription(x.ProposalKey, x.AccountableRoleKey == VideoGameRoleKeys.Engineer ?
                    SpecialistAgent.PlanningContractExample().DeliveryItems[2].Description + "\n" + x.Description : x.Description)).ToArray()), Web));
        var recovered = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], ["First playable\n\nAll levels and polish"], cycle, request, default);
        Assert.True(recovered.Artifact is not null, recovered.Content);
        Assert.Empty(recovered.Artifact.Payload.GetProperty("engineeringPrerequisiteKeys").EnumerateArray());
        foreach (var original in originals) Assert.Equal(original.Value, states[original.Key].Payload.GetRawText());
        var calls = client.Calls;
        var replay = await agent.GenerateProgressivePlanningAsync(runtime.CreateContext(), client, [], ["First playable\n\nAll levels and polish"], cycle,
            request with { DetailedStoryKeys = [story.ProposalKey] }, default);
        Assert.NotNull(replay.Artifact); Assert.Equal(calls, client.Calls);
        Assert.Equal(recovered.Artifact.Payload.GetRawText(), replay.Artifact.Payload.GetRawText());
        var items = replay.Artifact.Payload.GetProperty("DeliveryItems").Deserialize<GameProposedWorkItemV1[]>()!;
        Assert.Equal(JsonSerializer.Serialize(scope.Plan.DeliveryItems.Single(x => x.ProposalKey == "dependency-pin-phaser")),
            JsonSerializer.Serialize(items.Single(x => x.ProposalKey == "dependency-pin-phaser")));
    }

    private static SpecialistAgent.OutlineManifest Manifest(SpecialistAgent.ProgressiveScopeIndex scope) => new(
        scope.Plan.DeliveryItems.Where(x => x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Milestone).Select(x =>
            new SpecialistAgent.OutlineIncrement(x.ProposalKey, scope.Plan.DeliveryItems.Single(s => s.ParentProposalKey == x.ProposalKey).ProposalKey,
                x.Title, x.DependencyProposalKeys)).ToArray(), scope.Plan.FeasibilityFindings!, scope.Plan.TechnicalConstraints!, scope.Plan.OpenFeasibilityDecisions!);

    private static void EnqueueOutline(Client client, SpecialistAgent.ProgressiveScopeIndex scope)
    {
        var manifest = Manifest(scope);
        client.Responses.Enqueue(JsonSerializer.Serialize(manifest, Web));
        foreach (var increments in manifest.Increments.Chunk(SpecialistAgent.PlanningPageSize))
        {
            var keys = increments.SelectMany(x => new[] { x.EpicKey, x.StoryKey }).ToHashSet();
            client.Responses.Enqueue(JsonSerializer.Serialize(new SpecialistAgent.OutlineDraftPage(
                scope.Plan.DeliveryItems.Where(x => keys.Contains(x.ProposalKey)).Select(x => new SpecialistAgent.OutlineDraftItem(
                    x.ProposalKey, x.WorkItemTypeKey, x.Title, x.Description, x.AcceptanceCriteria, x.AccountableRoleKey,
                    x.RequiredSpecializationKeys, x.PreferredSpecializationKeys)).ToArray()), Web));
        }
        foreach (var coverage in scope.Coverage.Chunk(12))
            client.Responses.Enqueue(JsonSerializer.Serialize(new SpecialistAgent.CoverageIndexDraft(coverage.Select((x, index) =>
                new SpecialistAgent.CoverageIndex(index, x.ProposalKeys.Select(key => Array.FindIndex(
                    scope.Plan.DeliveryItems.Where(item => item.WorkItemTypeKey != VideoGameWorkItemTypeKeys.Milestone).ToArray(), item => item.ProposalKey == key)).ToArray())).ToArray()), Web));
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
        Assert.True(recovered.Artifact is not null, recovered.Content); Assert.Equal(9, client.Calls);
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
        public readonly List<ChatMessage[]> Prompts = new();
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { Calls++; Prompts.Add(messages.ToArray()); Assert.NotNull(options?.ResponseFormat); return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Responses.Dequeue())) { FinishReason = ChatFinishReason.Stop }); }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }
}
