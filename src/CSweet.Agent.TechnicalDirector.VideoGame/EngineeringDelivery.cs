using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    private static async Task FinalizeExecutableTicketsAsync(RepositorySetup setup, AgentRuntimeContext context, CancellationToken token)
    {
        if (setup.Status != "Ready" || setup.RepositoryId is null || string.IsNullOrWhiteSpace(setup.DefaultBranch)) return;
        var boards = await context.Platform.Work.ListBoardsAsync(cancellationToken: token);
        foreach (var summary in boards.Where(x => !x.IsArchived && x.WorkstreamId == setup.WorkstreamId && x.TeamId == setup.TeamId))
        {
            var board = await context.Platform.Work.ReadBoardAsync(summary.Id, token);
            if (board.Items.Count == 0) continue;
            var workstream = await context.Platform.ReadWorkstreamAsync(new(setup.WorkstreamId), token);
            // Revised delivery is finalized by the manager against the activated
            // plan's story bindings; legacy setup must not replace them with main.
            if (workstream.ProfileKey == "video-game-production.v2" && workstream.ProfileVersion >= 6) continue;
            var reviewedDelivery = workstream.ProfileKey == "video-game-production.v2" && workstream.ProfileVersion >= 5;
            var plannedSprints = (await context.Platform.Work.ListSprintsAsync(summary.Id, token))
                .Where(x => x.Status == "Planned").Select(x => x.Id).ToHashSet();
            foreach (var original in board.Items.Where(x => x.SprintId is null || plannedSprints.Contains(x.SprintId.Value)))
            {
                var item = original;
                if (reviewedDelivery && EngineeringPlanningRequest(summary.Id, item) is { } planning)
                    item = await context.Platform.Work.RevisePlanningAsync(planning, token);
                var request = DeliveryFinalizationRequest(summary.Id, item, setup, reviewedDelivery);
                if (request is not null) await context.Platform.Work.FinalizeItemDeliveryAsync(request, token);
            }
        }
    }

    internal static FinalizeWorkItemDeliveryRequest? DeliveryFinalizationRequest(Guid boardId, WorkItem item, RepositorySetup setup, bool reviewedDelivery = false)
    {
        var primaryRole = item.StageAssignments.SingleOrDefault(x => x.StageKey == "specialist-execution")?.Requirements?.RequiredRoleKey;
        if (setup.Status != "Ready" || setup.RepositoryId is not { } repository || string.IsNullOrWhiteSpace(setup.DefaultBranch) ||
            item.ExecutionMode != WorkItemExecutionModes.Executable || item.Planning is null ||
            item.ProposalProvenance is null || item.AccountableOrganizationUserId is not { } accountable ||
            item.Status is "Done" or "Completed" or "InProgress" or "Running" or "Cancelled" || string.IsNullOrWhiteSpace(primaryRole)) return null;
        var plan = item.Planning;
        var engineering = RoleTaxonomy.SatisfiesRole([primaryRole], "game-engineer");
        var completeReviewAssignments = !reviewedDelivery ||
            (item.StageAssignments.Any(x => x.StageKey == "producer-review") && (!engineering ||
                (item.StageAssignments.Any(x => x.StageKey == "governed-merge") && ReviewDelegations.All(r =>
                    item.StageAssignments.Any(x => x.StageKey == r.StageKey && x.AgentInstallationId is not null &&
                        x.Requirements?.RequiredRoleKey == r.RequiredRoleKey && x.SelectionEvidence is not null)))));
        if (item.Delivery is { } current && current.RepositoryId == repository && current.BaseBranch == setup.DefaultBranch &&
            current.Requirements.SequenceEqual(plan.Requirements) && current.AcceptanceCriteria.SequenceEqual(plan.AcceptanceCriteria) &&
            (current.Constraints ?? []).SequenceEqual(plan.Constraints ?? []) &&
            current.DependencyItemIds.SequenceEqual(plan.DependencyItemIds) && completeReviewAssignments) return null;
        var assignments = item.StageAssignments.ToList();
        if (reviewedDelivery && RoleTaxonomy.SatisfiesRole([primaryRole], "game-engineer"))
        {
            if (ReviewDelegations.Any(r => !plan.DelegationRecommendations.Any(x => x.StageKey == r.StageKey && x.RequiredRoleKey == r.RequiredRoleKey)) ||
                ReviewDelegations.Any(r => !assignments.Any(x => x.StageKey == r.StageKey && x.AgentInstallationId is not null &&
                    x.Requirements?.RequiredRoleKey == r.RequiredRoleKey && x.SelectionEvidence is not null))) return null;
            if (!assignments.Any(x => x.StageKey == "governed-merge"))
                assignments.Add(new WorkStageAssignment("governed-merge", "PlatformAction", null, null, "source-control.merge.execute.v2"));
        }
        // Document-producing specialists follow the profile's completed -> producer-review path.
        if (reviewedDelivery && !assignments.Any(x => x.StageKey == "producer-review"))
            assignments.Add(new WorkStageAssignment("producer-review", "BoardManager", null, null));
        var delivery = (item.Delivery ?? new WorkItemDeliverySpecification(repository, plan.Requirements, plan.AcceptanceCriteria, plan.Constraints)) with
        {
            RepositoryId = repository, BaseBranch = setup.DefaultBranch, Requirements = plan.Requirements,
            AcceptanceCriteria = plan.AcceptanceCriteria, Constraints = plan.Constraints, DependencyItemIds = plan.DependencyItemIds
        };
        return new FinalizeWorkItemDeliveryRequest(boardId, item.Id, delivery, accountable, assignments,
            item.Revision, $"game-delivery:{item.Id:N}:{item.Revision}:{repository:N}");
    }

    private static readonly IReadOnlyList<WorkTechnicalDelegationRecommendation> ReviewDelegations =
    [
        new("technical-review", "game-technical-director", ["work.execution.run.v1"], null, true,
            "Review the exact candidate implementation before independent QA.") { RequiredSpecializationKeys = ["technical-feasibility"] },
        new("quality", "game-quality-assurance", ["work.execution.run.v1"], null, true,
            "Test the technically reviewed candidate and record exact-commit validation evidence.") { RequiredSpecializationKeys = ["test-planning"] },
        new("merge-decision", "game-technical-director", ["work.execution.run.v1"], null, true,
            "Authorize the same technically approved commit only after passing QA.") { RequiredSpecializationKeys = ["technical-feasibility"] }
    ];

    internal static ReviseWorkItemPlanningRequest? EngineeringPlanningRequest(Guid boardId, WorkItem item)
    {
        if (item.ExecutionMode != WorkItemExecutionModes.Executable || item.Planning is null ||
            item.ProposalProvenance is null || item.Status is "Done" or "Completed" or "Running" or "InProgress" or "Cancelled" ||
            !item.Planning.DelegationRecommendations.Any(x => x.StageKey == "specialist-execution" && x.RequiredRoleKey == "game-engineer")) return null;
        var recommendations = item.Planning.DelegationRecommendations.ToList();
        var missing = ReviewDelegations.Where(r => !recommendations.Any(x => x.StageKey == r.StageKey)).ToArray();
        if (missing.Length == 0) return null;
        recommendations.AddRange(missing);
        return new ReviseWorkItemPlanningRequest(boardId, item.Id, item.Title, item.Description, item.ParentItemId,
            item.Planning with { DelegationRecommendations = recommendations }, item.Revision, item.PlanningRevision,
            $"game-review-plan:{item.Id:N}:{item.Revision}")
        {
            ProposalProvenance = item.ProposalProvenance,
            AccountableOrganizationUserId = item.AccountableOrganizationUserId
            // Omitted StageAssignments preserves every existing owner.
        };
    }

}
