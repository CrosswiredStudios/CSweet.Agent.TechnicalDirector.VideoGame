using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class ScopeAmendmentPlanningTests
{
    private static T Empty<T>() => JsonSerializer.Deserialize<T>("{}")!;
    private static (WorkItem[] Items, SpecialistAgent.ScopeAmendmentInstruction Instruction) Fixture()
    {
        WorkItem Item(string key, string type, Guid? parent, string? role = null) => Empty<WorkItem>() with
        {
            Id = Guid.NewGuid(), Title = $"[{key}] {key}", TypeKey = type, ParentItemId = parent, Status = "Ready",
            Description = "Description for " + key, ProposalProvenance = new(Guid.NewGuid(), "digest", key),
            Planning = new(["Complete requirement for " + key], ["Chromium, Firefox and Safari"], ["Preserve input behavior"]),
            StageAssignments = role is null ? [] : [new("specialist-execution", "AgentInstallation")
                { Requirements = new(role, [], [], ["work.execution.run"]) }]
        };
        var epic = Item("epic", VideoGameWorkItemTypeKeys.Milestone, null);
        var story = Item("story", VideoGameWorkItemTypeKeys.Feature, epic.Id);
        var source = Item("build", VideoGameWorkItemTypeKeys.Task, story.Id, VideoGameRoleKeys.Engineer) with { Status = "Blocked" };
        var done = Item("done", VideoGameWorkItemTypeKeys.Task, story.Id, VideoGameRoleKeys.Engineer) with { Status = "Completed" };
        source = source with { Planning = source.Planning! with { DependencyItemIds = [done.Id] } };
        var workflow = source with { Id = Guid.NewGuid(), TypeKey = "work.workflow-ticket.v1", Title = "[build] acceptance", ProposalProvenance = null };
        WorkItem[] items = [epic, story, source, done, workflow];
        return (items, new(1, source.Id, Guid.NewGuid(), Digests(items),
            [new(source.Id, "acceptanceCriteria", "Chromium, Firefox and Safari", "Chromium only"),
             new(workflow.Id, "acceptanceCriteria", "Chromium, Firefox and Safari", "Chromium only")]));
    }
    private static IReadOnlyDictionary<Guid, string> Digests(WorkItem[] items) => items.ToDictionary(x => x.Id,
        x => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(x.Planning))));

    [Fact]
    public void ExactOwnerChangePreservesCanonicalHierarchyCompletedWorkAndFullRequirements()
    {
        var (items, instruction) = Fixture();
        var output = SpecialistAgent.BuildScopeAmendmentPlanning(items, instruction);
        Assert.Equal(4, output.DeliveryItems.Count);
        Assert.Equal(new[] { "Chromium only" }, output.DeliveryItems.Single(x => x.ProposalKey == "build").AcceptanceCriteria);
        Assert.Equal(new[] { "done" }, output.DeliveryItems.Single(x => x.ProposalKey == "build").DependencyProposalKeys);
        Assert.Equal("game-engineer", output.DeliveryItems.Single(x => x.ProposalKey == "build").AccountableRoleKey);
        foreach (var item in items.Where(x => x.ProposalProvenance is not null))
        {
            var proposal = output.DeliveryItems.Single(x => x.ProposalKey == item.ProposalProvenance!.ProposalItemKey);
            Assert.All(item.Planning!.Requirements, text => Assert.Contains(text, proposal.Description));
            if (item.Id != instruction.SourceWorkItemId) Assert.Equal(item.Planning.AcceptanceCriteria, proposal.AcceptanceCriteria);
        }
        Assert.Contains("Preserve input behavior", output.TechnicalConstraints!);
        Assert.Equal(new[] { "Chromium, Firefox and Safari" }, items[2].Planning!.AcceptanceCriteria);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("missing-source")]
    [InlineData("missing-authority")]
    [InlineData("completed")]
    [InlineData("bad-original")]
    [InlineData("bad-field")]
    [InlineData("duplicate")]
    [InlineData("wrong-version")]
    public void InvalidOrStaleInstructionsCannotProduceReplacementScope(string fault)
    {
        var (items, instruction) = Fixture();
        switch (fault)
        {
            case "stale": items[2] = items[2] with { Planning = items[2].Planning! with { Requirements = ["New scope"] } }; break;
            case "missing-source": instruction = instruction with { SourceWorkItemId = Guid.NewGuid() }; break;
            case "missing-authority": instruction = instruction with { AuthorizingTurnId = Guid.Empty }; break;
            case "completed": items[2] = items[2] with { Status = "Completed" }; break;
            case "bad-original": instruction = instruction with { Replacements = [instruction.Replacements[0] with { Original = "Invented" }] }; break;
            case "bad-field": instruction = instruction with { Replacements = [instruction.Replacements[0] with { Field = "roles" }] }; break;
            case "duplicate": instruction = instruction with { Replacements = [instruction.Replacements[0], instruction.Replacements[0]] }; break;
            case "wrong-version": instruction = instruction with { SchemaVersion = 2 }; break;
        }
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.BuildScopeAmendmentPlanning(items, instruction));
    }

    [Fact]
    public void LongReplacementIsNeverShortened()
    {
        var (items, instruction) = Fixture();
        var replacement = new string('x', 48000);
        instruction = instruction with { Replacements = [instruction.Replacements[0] with { Replacement = replacement }] };
        Assert.Equal(replacement, SpecialistAgent.BuildScopeAmendmentPlanning(items, instruction).DeliveryItems.Single(x => x.ProposalKey == "build").AcceptanceCriteria[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstructionIsBoundToAuthenticatedCounterpartAndExactCycle(bool wrongSpeaker)
    {
        var (_, instruction) = Fixture();
        var self = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Victor", "Technical Director");
        var producer = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Gabriel", "Producer");
        var cycle = new GameProductionPlanningCycleV1(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), "profile", Guid.NewGuid(), 1, "package", "concept", "vision", "fingerprint");
        var payload = JsonSerializer.SerializeToNode(cycle)!.AsObject();
        payload["scopeAmendment"] = JsonSerializer.SerializeToNode(instruction, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var request = new AgentCoordinationTurnRequest(Guid.NewGuid(), 1, 1, "Amend", "Preserve scope", [], self, producer, false,
            [new(Guid.NewGuid(), 0, wrongSpeaker ? self.OrganizationUserId : producer.OrganizationUserId, "Continue", "Amend", DateTimeOffset.UtcNow,
                new("video-game.production.planning-cycle.v1", "1.0", cycle.PlanningFingerprint, 1, true, JsonSerializer.SerializeToElement(payload), "digest"))]);
        if (wrongSpeaker) Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ReadScopeAmendmentInstruction(request, cycle));
        else Assert.Equal(instruction.AuthorizingTurnId, SpecialistAgent.ReadScopeAmendmentInstruction(request, cycle).AuthorizingTurnId);
    }
}
