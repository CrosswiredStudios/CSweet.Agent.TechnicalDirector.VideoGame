using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrosswiredStudios.VideoGame.Contracts;
using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class OutlineMetadataRecoveryTests
{
    [Fact]
    public async Task Coverage_correction_names_out_of_page_sources_and_preserves_every_accepted_source()
    {
        var f = new Fixture(); var client = new Client(); client.Enqueue(f.Corrections);
        var sources = SpecialistAgent.AcceptedScopeSources(f.Documents);
        foreach (var key in f.States.Keys.Where(x => x.Contains(":coverage:")).ToArray()) f.States.Remove(key);
        // Reproduce a response to page zero containing the complete 41-source global ledger.
        client.Enqueue(new SpecialistAgent.OutlineCoverageDraft(sources.Select(x => new SpecialistAgent.ScopeCoverage(x.SourceKey, ["story-0"])).ToArray()));
        foreach (var page in sources.Chunk(12))
            client.Enqueue(new SpecialistAgent.OutlineCoverageDraft(page.Select(x => new SpecialistAgent.ScopeCoverage(x.SourceKey, ["story-0"])).ToArray()));
        var result = await f.Run(client);
        Assert.NotNull(result.Artifact);
        Assert.Equal(41, result.Artifact.Payload.GetProperty("progressivePlanning").GetProperty("scopeCoverage").GetArrayLength());
        var correction = client.Calls[2].Messages.Last().Text;
        Assert.Contains("Unexpected sourceKeys outside THIS page", correction);
        Assert.Contains("document-0:paragraph-12", correction);
        Assert.DoesNotContain(client.Calls[1].Messages, x => x.Text.Contains("Exact accepted source ledger"));
        Assert.Equal(4, f.Writes.Count(x => x.Contains(":coverage:")));
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static string Digest<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static SpecialistAgent.OutlineDraftItem Draft(GameProposedWorkItemV1 item) => new(item.ProposalKey, item.WorkItemTypeKey,
        item.Title, item.Description, item.AcceptanceCriteria, item.AccountableRoleKey, item.RequiredSpecializationKeys, item.PreferredSpecializationKeys);
    private static SpecialistAgent.OutlineMetadata Metadata(GameProposedWorkItemV1 item) => new(item.ProposalKey, item.AccountableRoleKey,
        item.RequiredSpecializationKeys, item.PreferredSpecializationKeys, item.RequiredCapabilityKeys);

    // Same failure as Neon Shatter: ten retained containers, forty-one covered paragraphs,
    // and two Epics on the second page using a capability as a required skill.
    private sealed class Fixture
    {
        public readonly GameProductionPlanningCycleV1 Cycle = new(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), new string('a', 64),
            Guid.NewGuid(), 1, new string('b', 64), "concept", "vision", new string('c', 64));
        public readonly SpecialistAgent.ProgressivePlanningRequest Request = new("Outline", new string('d', 64));
        public readonly string[] Documents = [string.Join("\n\n", Enumerable.Range(0, 41).Select(x => "Accepted requirement " + x))];
        public readonly Dictionary<string, AgentOperatingStateResponse> States = new();
        public readonly List<string> Writes = [];
        public Action<AgentOperatingStateWriteRequest>? BeforeWrite;
        public readonly SpecialistAgent.OutlineManifest Manifest;
        public readonly SpecialistAgent.OutlineItemPage[] Pages;
        public readonly string Key;
        public readonly AgentTestRuntime Runtime;
        public string PageKey => Key + ":outline:1";
        public SpecialistAgent.OutlineMetadataCorrections Corrections => new(Pages[1].Items.Where(x => x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Milestone)
            .Select(x => Metadata(x) with { RequiredSpecializationKeys = [] }).ToArray());

        public Fixture()
        {
            Key = SpecialistAgent.ProgressiveScopeKey(Cycle, Request.DirectionsDigest);
            var example = SpecialistAgent.PlanningContractExample();
            var epicKeys = new[] { "epic.core-framework", "epic.mechanical-expansion", "epic.av-integration", "epic.content-ui", "epic.chromium-verification" };
            Manifest = new(epicKeys.Select((key, index) => new SpecialistAgent.OutlineIncrement(key, "story-" + index,
                "Increment " + index, index == 0 ? [] : [epicKeys[index - 1]])).ToArray(), [], [], []);
            var digest = Digest(Manifest);
            Pages = Manifest.Increments.Chunk(SpecialistAgent.PlanningPageSize).Select((selected, page) => new SpecialistAgent.OutlineItemPage(digest,
                selected.SelectMany(x => new[] {
                    example.DeliveryItems[0] with { ProposalKey = x.EpicKey, Title = x.Title, DependencyProposalKeys = x.DependencyEpicKeys,
                        RequiredSpecializationKeys = page == 1 ? ["work.execution.run"] : [] },
                    example.DeliveryItems[1] with { ProposalKey = x.StoryKey, ParentProposalKey = x.EpicKey }
                }).ToArray())).ToArray();
            Put(Key + ":manifest", Manifest);
            for (var number = 0; number < Pages.Length; number++) Put(Key + ":outline:" + number, Pages[number]);
            foreach (var (sources, number) in SpecialistAgent.AcceptedScopeSources(Documents).Chunk(12).Select((x, i) => (x, i)))
                Put(Key + ":coverage:" + number, new SpecialistAgent.OutlineCoveragePage(digest,
                    sources.Select(x => new SpecialistAgent.ScopeCoverage(x.SourceKey, ["story-0"])).ToArray()));
            Runtime = new AgentTestRuntime()
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(States.GetValueOrDefault(r.StateKey))))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, ct) => {
                    ct.ThrowIfCancellationRequested();
                    BeforeWrite?.Invoke(r);
                    Assert.Equal(States.GetValueOrDefault(r.StateKey)?.Revision, r.ExpectedRevision);
                    Assert.False(States.ContainsKey(r.StateKey), "Original checkpoints and accepted repairs must remain immutable.");
                    Writes.Add(r.StateKey); Put(r.StateKey, r.Payload); return Task.FromResult(States[r.StateKey]);
                });
        }

        public void Put<T>(string key, T payload)
        {
            var now = DateTimeOffset.UtcNow;
            States[key] = new(Guid.NewGuid(), key, "video-game.progressive-planning.v1", 1, "Active", new Dictionary<string, string>(),
                [], "binding", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(payload, Web), 1, now, now);
        }

        public Task<AgentCoordinationTurnResult> Run(Client client, SpecialistAgent.ProgressivePlanningRequest? request = null, CancellationToken token = default) =>
            new SpecialistAgent().GenerateProgressivePlanningAsync(Runtime.CreateContext(), client,
                [new(ChatRole.System, SpecialistAgent.ProgressivePlanningPolicy())], Documents, Cycle, request ?? Request, token);
    }

    private sealed class Client : IChatClient
    {
        public readonly Queue<string> Responses = new();
        public readonly List<(ChatMessage[] Messages, ChatOptions Options)> Calls = [];
        public Action? BeforeResponse;
        public void Enqueue<T>(T value) => Responses.Enqueue(JsonSerializer.Serialize(value, Web));
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((messages.ToArray(), options!)); BeforeResponse?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Responses.Dequeue())) { FinishReason = ChatFinishReason.Stop });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task Saved_invalid_pages_recover_only_metadata_preserve_all_originals_and_replay_without_generation()
    {
        var f = new Fixture(); var originals = f.States.ToDictionary(x => x.Key, x => x.Value.Payload.GetRawText());
        var client = new Client(); client.Enqueue(f.Corrections);
        var result = await f.Run(client);
        Assert.NotNull(result.Artifact); Assert.Single(client.Calls);
        Assert.Equal(new[] { f.PageKey + ":metadata-repair:v1", f.Key + ":outline-completed" }, f.Writes);
        foreach (var original in originals) Assert.Equal(original.Value, f.States[original.Key].Payload.GetRawText());
        var items = result.Artifact.Payload.GetProperty("DeliveryItems").Deserialize<GameProposedWorkItemV1[]>()!;
        Assert.Equal(10, items.Length);
        Assert.Equal(41, result.Artifact.Payload.GetProperty("progressivePlanning").GetProperty("scopeCoverage").GetArrayLength());
        foreach (var original in f.Pages.SelectMany(x => x.Items))
        {
            var expected = original.RequiredSpecializationKeys.Contains("work.execution.run") ? original with { RequiredSpecializationKeys = [] } : original;
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(items.Single(x => x.ProposalKey == original.ProposalKey)));
        }
        var replay = await f.Run(client);
        Assert.Equal(result.Artifact.Payload.GetRawText(), replay.Artifact!.Payload.GetRawText());
        Assert.Single(client.Calls); Assert.Equal(2, f.Writes.Count);
        // A saved repair cannot be applied to changed original text, even if its graph still validates.
        f.Put(f.PageKey, f.Pages[1] with { Items = f.Pages[1].Items.Select(x => x with { Description = "Changed scope" }).ToArray() });
        var tampered = await f.Run(client);
        Assert.Null(tampered.Artifact); Assert.Contains("different original page", tampered.Content); Assert.Single(client.Calls);
    }

    [Theory]
    [InlineData("invalid-skill")]
    [InlineData("valid-role-changed")]
    [InlineData("missing-item")]
    [InlineData("scope-change")]
    public async Task Bad_metadata_repair_is_bounded_diagnostic_and_does_not_modify_checkpoints(string failure)
    {
        var f = new Fixture(); var client = new Client();
        var bad = failure switch {
            "invalid-skill" => f.Corrections with { Items = f.Corrections.Items.Select(x => x with { RequiredSpecializationKeys = new[] { "work.execution.run" } }).ToArray() },
            "valid-role-changed" => f.Corrections with { Items = f.Corrections.Items.Select(x => x with { AccountableRoleKey = VideoGameRoleKeys.Engineer }).ToArray() },
            "missing-item" => f.Corrections with { Items = f.Corrections.Items.Take(1).ToArray() },
            _ => f.Corrections
        };
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var json = JsonSerializer.SerializeToNode(bad, Web)!;
            if (failure == "scope-change") json["items"]![0]!["description"] = "Replace the approved scope";
            client.Responses.Enqueue(json.ToJsonString());
        }
        var result = await f.Run(client);
        Assert.Null(result.Artifact); Assert.Equal(3, client.Calls.Count); Assert.Empty(f.Writes);
        Assert.Contains("Page failed after three attempts", result.Content);
        Assert.Contains("Correct only this page", client.Calls[1].Messages.Last().Text);
        Assert.Contains("epic.content-ui", client.Calls[0].Messages.Last().Text);
        if (failure == "invalid-skill")
        {
            Assert.Contains("requiredSpecializationKeys", result.Content);
            Assert.Contains("capability, not a specialization", result.Content);
        }
    }

    [Fact]
    public async Task Failed_correction_resumes_with_original_pages_and_cancellation_never_commits_partial_repairs()
    {
        var f = new Fixture(); var client = new Client();
        client.Enqueue(f.Corrections); using var cancel = new CancellationTokenSource(); client.BeforeResponse = cancel.Cancel;
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Run(client, token: cancel.Token));
        Assert.Empty(f.Writes);
        client.BeforeResponse = null;
        var result = await f.Run(client); Assert.NotNull(result.Artifact); Assert.Equal(2, client.Calls.Count);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("story")]
    [InlineData("hierarchy")]
    [InlineData("digest")]
    public async Task Recovery_cannot_rewrite_frozen_outlines_or_repair_graph_and_binding_changes(string failure)
    {
        var f = new Fixture(); var client = new Client();
        var page = f.Pages[1];
        if (failure == "completed") f.Put(f.Key + ":outline-completed", new { ManifestDigest = Digest(f.Manifest), OutlineDigest = "frozen" });
        if (failure == "hierarchy") f.Put(f.PageKey, page with { Items = page.Items.Select(x => x with { ParentProposalKey = "other-parent" }).ToArray() });
        if (failure == "digest") f.Put(f.PageKey, page with { ManifestDigest = "other-manifest" });
        var result = await f.Run(client, failure == "story" ? f.Request with { Phase = "Story" } : null);
        Assert.Null(result.Artifact); Assert.Empty(client.Calls); Assert.Empty(f.Writes);
    }

    [Fact]
    public void Repair_preserves_valid_skills_within_a_partially_invalid_array()
    {
        var f = new Fixture(); var page = f.Pages[1] with { Items = f.Pages[1].Items.Select(x => x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Milestone
            ? x with { RequiredSpecializationKeys = [VideoGameSpecializationKeys.TechnicalFeasibility, "work.execution.run"] } : x).ToArray() };
        var error = Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ApplyOutlineMetadataCorrections(page, f.Corrections,
            page.ManifestDigest, f.Manifest.Increments.Skip(3).ToArray()));
        Assert.Contains("lost retained valid skills", error.Message);
        var repaired = f.Corrections with { Items = f.Corrections.Items.Select(x => x with { RequiredSpecializationKeys = new[] { VideoGameSpecializationKeys.TechnicalFeasibility } }).ToArray() };
        SpecialistAgent.ApplyOutlineMetadataCorrections(page, repaired, page.ManifestDigest, f.Manifest.Increments.Skip(3).ToArray());
    }

    [Fact]
    public async Task Concurrent_valid_repair_wins_without_overwrite_or_another_model_call()
    {
        var f = new Fixture(); var client = new Client(); client.Enqueue(f.Corrections);
        var winner = new SpecialistAgent.OutlineMetadataRepair(Digest(f.Pages[1]), f.Corrections with {
            Items = f.Corrections.Items.Select(x => x with { RequiredSpecializationKeys = new[] { VideoGameSpecializationKeys.TechnicalFeasibility } }).ToArray() });
        f.BeforeWrite = request => {
            if (!request.StateKey.EndsWith(":metadata-repair:v1")) return;
            f.BeforeWrite = null; f.Put(request.StateKey, winner);
            throw new PlatformCapabilityException(PlatformCapabilities.AgentOperatingStateWrite, PlatformCapabilityErrorCode.Conflict, "Concurrent repair accepted.");
        };
        var result = await f.Run(client);
        Assert.NotNull(result.Artifact); Assert.Single(client.Calls);
        Assert.Equal(f.Key + ":outline-completed", Assert.Single(f.Writes));
        var items = result.Artifact.Payload.GetProperty("DeliveryItems").Deserialize<GameProposedWorkItemV1[]>()!;
        Assert.Equal(new[] { VideoGameSpecializationKeys.TechnicalFeasibility }, items.Single(x => x.ProposalKey == "epic.content-ui").RequiredSpecializationKeys);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("required-skill")]
    [InlineData("preferred-skill")]
    [InlineData("null-skills")]
    [InlineData("capability")]
    public void Page_and_final_validation_reject_the_same_invalid_metadata(string field)
    {
        var f = new Fixture(); var original = f.Pages[0].Items[0];
        var invalid = field switch {
            "role" => original with { AccountableRoleKey = "invented-role" },
            "required-skill" => original with { RequiredSpecializationKeys = ["work.execution.run"] },
            "preferred-skill" => original with { PreferredSpecializationKeys = ["invented-skill"] },
            "null-skills" => original with { RequiredSpecializationKeys = null! },
            _ => original with { RequiredCapabilityKeys = ["invented-capability"] }
        };
        var page = f.Pages[0] with { Items = [invalid, .. f.Pages[0].Items.Skip(1)] };
        var pageError = Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateOutlineItemPage(page, page.ManifestDigest, f.Manifest.Increments.Take(3).ToArray()));
        var scope = new SpecialistAgent.ProgressiveScopeIndex(new(page.Items, [], [], []), []);
        var finalError = Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateProgressiveOutline(scope, []));
        Assert.Equal(pageError.Message, finalError.Message); Assert.Contains(original.ProposalKey, pageError.Message);
    }

    [Fact]
    public async Task New_outline_retries_invalid_skills_before_checkpoint_and_code_supplies_fixed_fields()
    {
        var f = new Fixture(); f.States.Clear(); var client = new Client(); client.Enqueue(f.Manifest);
        var first = new SpecialistAgent.OutlineDraftPage(f.Pages[0].Items.Select(Draft).ToArray());
        client.Enqueue(first with { Items = first.Items.Select(x => x with { RequiredSpecializationKeys = new[] { "work.execution.run" } }).ToArray() });
        client.Enqueue(first);
        client.Enqueue(new SpecialistAgent.OutlineDraftPage(f.Pages[1].Items.Select(x => Draft(x) with { RequiredSpecializationKeys = [] }).ToArray()));
        foreach (var sources in SpecialistAgent.AcceptedScopeSources(f.Documents).Chunk(12))
            client.Enqueue(new SpecialistAgent.OutlineCoverageDraft(sources.Select(x => new SpecialistAgent.ScopeCoverage(x.SourceKey, ["story-0"])).ToArray()));
        var result = await f.Run(client);
        Assert.NotNull(result.Artifact);
        Assert.Contains("requiredSpecializationKeys", client.Calls[2].Messages.Last().Text);
        Assert.Contains("Allowed skills", client.Calls[2].Messages.Last().Text);
        Assert.DoesNotContain(f.Writes, x => x.EndsWith(":metadata-repair:v1"));
        var saved = f.States[f.Key + ":outline:0"].Payload.Deserialize<SpecialistAgent.OutlineItemPage>()!;
        Assert.Equal(Digest(f.Manifest), saved.ManifestDigest);
        Assert.Equal(f.Manifest.Increments[1].DependencyEpicKeys, saved.Items[2].DependencyProposalKeys);
        Assert.Equal(f.Manifest.Increments[1].EpicKey, saved.Items[3].ParentProposalKey);
        Assert.All(saved.Items, x => Assert.Equal(new[] { "work.execution.run" }, x.RequiredCapabilityKeys));
        var prompts = string.Join("\n", client.Calls[1].Messages.Where(x => x.Role == ChatRole.System).Select(x => x.Text));
        Assert.Contains("Epic/Story outline only", prompts);
        Assert.Contains("Structure example only", prompts);
        Assert.DoesNotContain("Include actionable implementation briefs", prompts);
        Assert.DoesNotContain("Construct the proposal in this order", prompts);
    }

    [Fact]
    public void Planning_schemas_constrain_roles_skills_and_capabilities_and_omit_code_owned_draft_fields()
    {
        var schema = JsonNode.Parse(SpecialistAgent.PlanningResponseSchema(typeof(SpecialistAgent.OutlineDraftPage)).GetRawText())!;
        var properties = schema["properties"]!["items"]!["items"]!["properties"]!;
        var skills = properties["requiredSpecializationKeys"]!["items"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        Assert.Contains("technical-feasibility", skills); Assert.DoesNotContain("work.execution.run", skills);
        Assert.Contains("game-engineer", properties["accountableRoleKey"]!["enum"]!.ToJsonString());
        Assert.Null(properties["requiredCapabilityKeys"]); Assert.Null(properties["parentProposalKey"]); Assert.Null(properties["dependencyProposalKeys"]);
        Assert.Null(schema["properties"]!["manifestDigest"]);
        var repair = JsonNode.Parse(SpecialistAgent.PlanningResponseSchema(typeof(SpecialistAgent.OutlineMetadataCorrections)).GetRawText())!;
        var capabilities = repair["properties"]!["items"]!["items"]!["properties"]!["requiredCapabilityKeys"]!;
        Assert.Equal("work.execution.run", capabilities["items"]!["enum"]![0]!.GetValue<string>());
        Assert.Null(capabilities["minItems"]); // Historical valid container arrays may be empty and must survive correction.
        var plan = JsonNode.Parse(SpecialistAgent.PlanningResponseSchema(typeof(SpecialistAgent.PlanningOutput)).GetRawText())!;
        Assert.Equal(1, plan["properties"]!["deliveryItems"]!["items"]!["properties"]!["requiredCapabilityKeys"]!["minItems"]!.GetValue<int>());
    }

    [Fact]
    public void Metadata_repair_preserves_historical_empty_capability_arrays_and_corrects_only_invalid_arrays()
    {
        var f = new Fixture(); var selected = f.Manifest.Increments.Skip(3).ToArray();
        var page = f.Pages[1] with { Items = f.Pages[1].Items.Select(x => x with { RequiredCapabilityKeys = [] }).ToArray() };
        var corrections = f.Corrections with { Items = f.Corrections.Items.Select(x => x with { RequiredCapabilityKeys = Array.Empty<string>() }).ToArray() };
        var result = SpecialistAgent.ApplyOutlineMetadataCorrections(page, corrections, page.ManifestDigest, selected);
        Assert.All(result.Items, x => Assert.Empty(x.RequiredCapabilityKeys));
        page = f.Pages[1] with { Items = f.Pages[1].Items.Select(x => x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Milestone
            ? x with { RequiredCapabilityKeys = ["unknown-capability"] } : x).ToArray() };
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ApplyOutlineMetadataCorrections(page, corrections, page.ManifestDigest, selected));
        SpecialistAgent.ApplyOutlineMetadataCorrections(page, f.Corrections, page.ManifestDigest, selected);
    }
}
