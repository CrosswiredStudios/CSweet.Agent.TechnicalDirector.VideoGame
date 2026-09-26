using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class EngineeringDeliveryTests
{
    [Fact]
    public void FinalizationPreservesScopeDependenciesAndAssignments()
    {
        var item = Item(); var board = Guid.NewGuid(); var repository = Guid.NewGuid();
        var setup = new SpecialistAgent.RepositorySetup(Guid.NewGuid(), Guid.NewGuid(), "Ready", repository, null, null, "main");
        var request = Assert.IsType<FinalizeWorkItemDeliveryRequest>(SpecialistAgent.DeliveryFinalizationRequest(board, item, setup));
        Assert.Equal(repository, request.Delivery.RepositoryId);
        Assert.Equal("main", request.Delivery.BaseBranch);
        Assert.Equal(item.Planning!.Requirements, request.Delivery.Requirements);
        Assert.Equal(item.Planning.AcceptanceCriteria, request.Delivery.AcceptanceCriteria);
        Assert.Equal(item.Planning.DependencyItemIds, request.Delivery.DependencyItemIds);
        Assert.Equal(item.StageAssignments, request.StageAssignments);
        Assert.Equal(item.Revision, request.ExpectedRevision);
        Assert.Equal(request.IdempotencyKey, SpecialistAgent.DeliveryFinalizationRequest(board, item, setup)!.IdempotencyKey);
        Assert.Null(SpecialistAgent.DeliveryFinalizationRequest(board, item with { Delivery = request.Delivery }, setup));
        Assert.Null(SpecialistAgent.DeliveryFinalizationRequest(board, item with { Status = "Done" }, setup));
        Assert.Null(SpecialistAgent.DeliveryFinalizationRequest(board, item with { ProposalProvenance = null }, setup));
        Assert.Null(SpecialistAgent.DeliveryFinalizationRequest(board, item with { StageAssignments = [] }, setup));
        Assert.Null(SpecialistAgent.DeliveryFinalizationRequest(board, item, setup with { Status = "AwaitingApproval" }));
    }
    [Fact]
    public void DeclaresMissingReviewRolesWithoutChangingScopeOrExistingOwners()
    {
        var item = Item();
        item = item with { Planning = item.Planning! with { DelegationRecommendations =
            [new("specialist-execution", "game-engineer", ["work.execution.run.v1"], null, true, "Implement gameplay")] } };
        var revision = Assert.IsType<ReviseWorkItemPlanningRequest>(SpecialistAgent.EngineeringPlanningRequest(Guid.NewGuid(), item));
        Assert.Equal(item.Planning.Requirements, revision.Planning.Requirements);
        Assert.Equal(item.Planning.AcceptanceCriteria, revision.Planning.AcceptanceCriteria);
        Assert.Equal(item.Planning.DependencyItemIds, revision.Planning.DependencyItemIds);
        Assert.Null(revision.StageAssignments);
        Assert.Equal(item.ProposalProvenance, revision.ProposalProvenance);
        Assert.Equal(new[] { "specialist-execution", "technical-review", "quality", "merge-decision" },
            revision.Planning.DelegationRecommendations.Select(x => x.StageKey));
        var planned = item with { Planning = revision.Planning };
        Assert.Null(SpecialistAgent.EngineeringPlanningRequest(Guid.NewGuid(), planned));
        var setup = new SpecialistAgent.RepositorySetup(Guid.NewGuid(), Guid.NewGuid(), "Ready", Guid.NewGuid(), null, null, "main");
        Assert.Null(SpecialistAgent.DeliveryFinalizationRequest(Guid.NewGuid(), planned, setup, reviewedDelivery: true));
        var assignments = item.StageAssignments.ToList();
        foreach (var delegation in revision.Planning.DelegationRecommendations.Where(x => x.StageKey != "specialist-execution"))
        {
            var installation = Guid.NewGuid();
            assignments.Add(new(delegation.StageKey, "AgentInstallation", Guid.NewGuid(), installation)
            {
                Requirements = new(delegation.RequiredRoleKey, delegation.RequiredSpecializationKeys, [], delegation.RequiredCapabilityKeys),
                SelectionEvidence = new(installation, 1, "profile", delegation.RequiredSpecializationKeys, "selection", DateTimeOffset.UtcNow)
            });
        }
        var staffed = planned with { StageAssignments = assignments };
        var final = Assert.IsType<FinalizeWorkItemDeliveryRequest>(SpecialistAgent.DeliveryFinalizationRequest(Guid.NewGuid(), staffed, setup, reviewedDelivery: true));
        Assert.Equal(6, final.StageAssignments.Count);
        foreach (var existing in assignments) Assert.Contains(existing, final.StageAssignments);
        Assert.Equal("source-control.merge.execute.v2", Assert.Single(final.StageAssignments, x => x.StageKey == "governed-merge").PlatformAction);
        Assert.Equal("BoardManager", Assert.Single(final.StageAssignments, x => x.StageKey == "producer-review").PrincipalKind);
    }

    [Theory]
    [InlineData("game-technical-director")]
    [InlineData("audio-designer")]
    [InlineData("game-quality-assurance")]
    public void SpecialistTicketsReceiveRepositoryWithoutChangingOwnersOrSkippingProducerReview(string role)
    {
        var original = Item();
        var item = original with { StageAssignments = [original.StageAssignments[0] with
            { Requirements = new(role, [], [], ["work.execution.run.v1"]) }] };
        var setup = new SpecialistAgent.RepositorySetup(Guid.NewGuid(), Guid.NewGuid(), "Ready", Guid.NewGuid(), null, null, "main");
        var request = Assert.IsType<FinalizeWorkItemDeliveryRequest>(
            SpecialistAgent.DeliveryFinalizationRequest(Guid.NewGuid(), item, setup, reviewedDelivery: true));
        Assert.Equal(setup.RepositoryId, request.Delivery.RepositoryId);
        Assert.Equal(item.Planning!.Requirements, request.Delivery.Requirements);
        Assert.Equal(item.Planning.AcceptanceCriteria, request.Delivery.AcceptanceCriteria);
        Assert.Equal(item.Planning.Constraints, request.Delivery.Constraints);
        Assert.Equal(item.Planning.DependencyItemIds, request.Delivery.DependencyItemIds);
        Assert.Equal(item.AccountableOrganizationUserId, request.AccountableOrganizationUserId);
        Assert.Contains(item.StageAssignments[0], request.StageAssignments);
        Assert.Equal("BoardManager", Assert.Single(request.StageAssignments, x => x.StageKey == "producer-review").PrincipalKind);
        Assert.DoesNotContain(request.StageAssignments, x => x.StageKey == "governed-merge");
        foreach (var status in new[] { "Done", "Completed", "Running", "InProgress", "Cancelled" })
            Assert.Null(SpecialistAgent.DeliveryFinalizationRequest(Guid.NewGuid(), item with { Status = status }, setup, true));
        Assert.Null(SpecialistAgent.DeliveryFinalizationRequest(Guid.NewGuid(), item with { Delivery = request.Delivery }, setup, true));
        Assert.Null(SpecialistAgent.DeliveryFinalizationRequest(Guid.NewGuid(), item with { StageAssignments = [] }, setup, true));
    }
    [Fact]
    public void RevisedApprovedScopeRefreshesUnstartedDeliveryAndThenBecomesStable()
    {
        var item = Item();
        var board = Guid.NewGuid();
        var setup = new SpecialistAgent.RepositorySetup(Guid.NewGuid(), Guid.NewGuid(), "Ready", Guid.NewGuid(), null, null, "main");
        var first = SpecialistAgent.DeliveryFinalizationRequest(board, item, setup)!;
        var revised = item with { Revision = item.Revision + 1, Delivery = first.Delivery,
            Planning = item.Planning! with { AcceptanceCriteria = ["New approved criterion"], DependencyItemIds = [] } };
        var update = Assert.IsType<FinalizeWorkItemDeliveryRequest>(SpecialistAgent.DeliveryFinalizationRequest(board, revised, setup));
        Assert.Equal(revised.Planning.AcceptanceCriteria, update.Delivery.AcceptanceCriteria);
        Assert.Empty(update.Delivery.DependencyItemIds);
        Assert.Equal(item.StageAssignments, update.StageAssignments);
        Assert.NotEqual(first.IdempotencyKey, update.IdempotencyKey);
        Assert.Null(SpecialistAgent.DeliveryFinalizationRequest(board, revised with { Delivery = update.Delivery }, setup));
        Assert.Null(SpecialistAgent.DeliveryFinalizationRequest(board, revised with { Status = "Running" }, setup));
    }
    private static WorkItem Item() => JsonSerializer.Deserialize<WorkItem>("{}")! with
    {
        Id = Guid.NewGuid(), Status = "Backlog", ExecutionMode = WorkItemExecutionModes.Executable,
        Revision = 3, AccountableOrganizationUserId = Guid.NewGuid(),
        ProposalProvenance = new(Guid.NewGuid(), "proposal-digest", "move"),
        Planning = new(["Move player"], ["Position changes"], ["Keep accepted scope"]) { DependencyItemIds = [Guid.NewGuid()] },
        StageAssignments = [new("specialist-execution", "AgentInstallation", Guid.NewGuid(), Guid.NewGuid())
            { Requirements = new("game-engineer", [], [], ["work.execution.run.v1"]) }]
    };
}
