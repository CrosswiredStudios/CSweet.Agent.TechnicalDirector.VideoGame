using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class RoleRepairPlanningTests
{
    private static T Empty<T>() => JsonSerializer.Deserialize<T>("{}")!;
    private static (WorkItem[] Items, SpecialistAgent.RoleRepairInstruction Instruction) Fixture()
    {
        WorkItem Item(string key, string type, Guid? parent, string? role = null) => Empty<WorkItem>() with
        {
            Id = Guid.NewGuid(), TypeKey = type, Title = $"[{key}] {key}", Description = $"Scope for {key}", ParentItemId = parent, Status = "Assigned",
            ProposalProvenance = new(Guid.NewGuid(), "digest", key),
            Planning = new([$"Requirement for {key}"], [$"Criterion for {key}"], [$"Constraint for {key}"]),
            StageAssignments = role is null ? [] : [new("specialist-execution", "AgentInstallation")
                { Requirements = new(role, [], [], ["work.execution.run.v1"]) }]
        };
        var epic = Item("epic", VideoGameWorkItemTypeKeys.Milestone, null);
        var story = Item("story", VideoGameWorkItemTypeKeys.Feature, epic.Id);
        var source = Item("foundation", VideoGameWorkItemTypeKeys.ResearchSpike, story.Id, VideoGameRoleKeys.TechnicalDirector);
        source = source with { Status = "Blocked", Planning = source.Planning! with
        {
            AcceptanceCriteria = ["Pin Phaser exactly in package.json.", "Commit wave template unchanged.", "Sustain 60 fps on the specified devices."]
        }};
        var downstream = Item("game", VideoGameWorkItemTypeKeys.Task, story.Id, VideoGameRoleKeys.Engineer);
        downstream = downstream with { Planning = downstream.Planning! with { DependencyItemIds = [source.Id] } };
        var done = Item("completed", VideoGameWorkItemTypeKeys.Task, story.Id, VideoGameRoleKeys.Engineer) with { Status = "Completed" };
        return ([epic, story, source, downstream, done], new(1, source.Id, Digest(source.Planning)));
    }
    private static string Digest(WorkItemPlanningSpecification? planning) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(planning))).ToLowerInvariant();

    [Fact]
    public void SplitCopiesExactScopeAndWaitsForIndependentValidationWithoutModelTranscription()
    {
        var (items, instruction) = Fixture();
        var output = SpecialistAgent.BuildRoleRepairPlanning(items, instruction);
        var source = items.Single(x => x.Id == instruction.SourceWorkItemId);
        var plan = output.DeliveryItems.Single(x => x.ProposalKey == "foundation");
        var engineer = output.DeliveryItems.Single(x => x.ProposalKey.EndsWith("-implementation"));
        var qa = output.DeliveryItems.Single(x => x.ProposalKey.EndsWith("-validation"));
        Assert.True(SpecialistAgent.IsValidPlan(output.DeliveryItems));
        Assert.Equal(VideoGameRoleKeys.TechnicalDirector, plan.AccountableRoleKey);
        Assert.Empty(plan.AcceptanceCriteria.Intersect(source.Planning!.AcceptanceCriteria));
        Assert.Equal(source.Planning.AcceptanceCriteria, engineer.AcceptanceCriteria);
        Assert.Equal(VideoGameRoleKeys.Engineer, engineer.AccountableRoleKey);
        Assert.Equal(VideoGameRoleKeys.QualityAssurance, qa.AccountableRoleKey);
        Assert.Equal(new[] { plan.ProposalKey }, engineer.DependencyProposalKeys);
        Assert.Equal(new[] { engineer.ProposalKey }, qa.DependencyProposalKeys);
        Assert.Equal(new[] { qa.ProposalKey }, output.DeliveryItems.Single(x => x.ProposalKey == "game").DependencyProposalKeys);
        foreach (var original in items)
        {
            var retained = output.DeliveryItems.Single(x => x.ProposalKey == original.ProposalProvenance!.ProposalItemKey);
            Assert.Equal(original.TypeKey, retained.WorkItemTypeKey);
            Assert.All(original.Planning!.Requirements, requirement => Assert.Contains(output.DeliveryItems, p => p.Description.Contains(requirement, StringComparison.Ordinal)));
            Assert.All(original.Planning.Constraints!, constraint => Assert.Contains(constraint, output.TechnicalConstraints!));
            if (original.Id != source.Id) Assert.Equal(original.Planning.AcceptanceCriteria, retained.AcceptanceCriteria);
        }
        Assert.Equal(JsonSerializer.Serialize(output), JsonSerializer.Serialize(SpecialistAgent.BuildRoleRepairPlanning(items.Reverse().ToArray(), instruction)));
    }

    [Theory]
    [InlineData("changed-scope")]
    [InlineData("completed-source")]
    [InlineData("wrong-role")]
    [InlineData("missing-parent")]
    [InlineData("missing-dependency")]
    [InlineData("duplicate-key")]
    [InlineData("output-key-collision")]
    [InlineData("unsupported-version")]
    public void UnsafeOrStaleCanonicalInputsDoNotProduceReplacementScope(string fault)
    {
        var (items, instruction) = Fixture();
        switch (fault)
        {
            case "changed-scope": items[2] = items[2] with { Planning = items[2].Planning! with { AcceptanceCriteria = ["Changed"] } }; break;
            case "completed-source": items[2] = items[2] with { Status = "Completed" }; break;
            case "wrong-role": items[2] = items[2] with { StageAssignments = [new("specialist-execution", "AgentInstallation") { Requirements = new(VideoGameRoleKeys.Engineer, [], [], []) }] }; break;
            case "missing-parent": items[2] = items[2] with { ParentItemId = Guid.NewGuid() }; break;
            case "missing-dependency": items[3] = items[3] with { Planning = items[3].Planning! with { DependencyItemIds = [Guid.NewGuid()] } }; break;
            case "duplicate-key": items[3] = items[3] with { ProposalProvenance = items[2].ProposalProvenance }; break;
            case "output-key-collision": items[3] = items[3] with { ProposalProvenance = items[3].ProposalProvenance! with { ProposalItemKey = $"role-repair-{instruction.SourceWorkItemId:N}-implementation" } }; break;
            case "unsupported-version": instruction = instruction with { SchemaVersion = 2 }; break;
        }
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.BuildRoleRepairPlanning(items, instruction));
    }

    [Fact]
    public void UnassignedWorkPreservesItsRecordedDelegationRequirements()
    {
        var (items, instruction) = Fixture();
        var requirements = items[3].StageAssignments[0].Requirements!;
        items[3] = items[3] with { StageAssignments = [], Planning = items[3].Planning! with
        { DelegationRecommendations = [new("specialist-execution", requirements.RequiredRoleKey, requirements.RequiredCapabilityKeys, null, true, "Original assignment")]} };
        Assert.Equal(requirements.RequiredRoleKey, SpecialistAgent.BuildRoleRepairPlanning(items, instruction).DeliveryItems.Single(x => x.ProposalKey == "game").AccountableRoleKey);
    }

    [Theory]
    [InlineData("current", true)]
    [InlineData("wrong-speaker", false)]
    [InlineData("old-cycle", false)]
    [InlineData("missing", false)]
    [InlineData("unsupported-version", false)]
    public void StructuredInstructionMustComeFromCurrentAuthenticatedProducer(string scenario, bool accepted)
    {
        var (_, instruction) = Fixture();
        var self = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Victor", "Technical Director");
        var producer = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Gabriel", "Producer");
        var cycle = new GameProductionPlanningCycleV1(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), "profile", Guid.NewGuid(), 1, "package", "concept", "vision", "fingerprint");
        var payload = JsonSerializer.SerializeToNode(scenario == "old-cycle" ? cycle with { ApprovedPackageDigest = "old" } : cycle)!.AsObject();
        if (scenario != "missing") payload["roleRepair"] = JsonSerializer.SerializeToNode(scenario == "unsupported-version" ? instruction with { SchemaVersion = 2 } : instruction,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var request = new AgentCoordinationTurnRequest(Guid.NewGuid(), 1, 1, "Repair", "Preserve scope", [], self, producer, false,
            [new(Guid.NewGuid(), 0, scenario == "wrong-speaker" ? self.OrganizationUserId : producer.OrganizationUserId, "Continue", "Repair", DateTimeOffset.UtcNow,
                new("video-game.production.planning-cycle.v1", "1.0", cycle.PlanningFingerprint, 1, true, JsonSerializer.SerializeToElement(payload), "digest"))]);
        if (accepted) Assert.Equal(instruction, SpecialistAgent.ReadRoleRepairInstruction(request, cycle));
        else Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ReadRoleRepairInstruction(request, cycle));
    }
}