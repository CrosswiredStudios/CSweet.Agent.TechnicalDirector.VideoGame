using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    private static async Task FinalizeEngineeringTicketsAsync(RepositorySetup setup, AgentRuntimeContext context, CancellationToken token)
    {
        if (setup.Status != "Ready" || setup.RepositoryId is null || string.IsNullOrWhiteSpace(setup.DefaultBranch)) return;
        var boards = await context.Platform.Work.ListBoardsAsync(cancellationToken: token);
        foreach (var summary in boards.Where(x => !x.IsArchived && x.WorkstreamId == setup.WorkstreamId && x.TeamId == setup.TeamId))
        {
            var board = await context.Platform.Work.ReadBoardAsync(summary.Id, token);
            if (board.Items.Count == 0) continue;
            var workstream = await context.Platform.ReadWorkstreamAsync(new(setup.WorkstreamId), token);
            var reviewedDelivery = workstream.ProfileKey == "video-game-production.v2" && workstream.ProfileVersion >= 5;
            foreach (var original in board.Items)
            {
                var item = original;
                if (reviewedDelivery && EngineeringPlanningRequest(summary.Id, item) is { } planning)
                    item = await context.Platform.Work.RevisePlanningAsync(planning, token);
                var request = EngineeringDeliveryRequest(summary.Id, item, setup, reviewedDelivery);
                if (request is not null) await context.Platform.Work.FinalizeItemDeliveryAsync(request, token);
            }
        }
    }

    internal static FinalizeWorkItemDeliveryRequest? EngineeringDeliveryRequest(Guid boardId, WorkItem item, RepositorySetup setup, bool reviewedDelivery = false)
    {
        if (setup.Status != "Ready" || setup.RepositoryId is not { } repository || string.IsNullOrWhiteSpace(setup.DefaultBranch) ||
            item.Delivery is not null || item.ExecutionMode != WorkItemExecutionModes.Executable || item.Planning is null ||
            item.ProposalProvenance is null || item.AccountableOrganizationUserId is not { } accountable ||
            item.Status is "Done" or "Completed" || !item.StageAssignments.Any(x => x.Requirements?.RequiredRoleKey == "game-engineer")) return null;
        var plan = item.Planning;
        var assignments = item.StageAssignments.ToList();
        if (reviewedDelivery)
        {
            if (ReviewDelegations.Any(r => !plan.DelegationRecommendations.Any(x => x.StageKey == r.StageKey && x.RequiredRoleKey == r.RequiredRoleKey)) ||
                ReviewDelegations.Any(r => !assignments.Any(x => x.StageKey == r.StageKey && x.AgentInstallationId is not null &&
                    x.Requirements?.RequiredRoleKey == r.RequiredRoleKey && x.SelectionEvidence is not null))) return null;
            if (!assignments.Any(x => x.StageKey == "governed-merge"))
                assignments.Add(new WorkStageAssignment("governed-merge", "PlatformAction", null, null, "source-control.merge.execute.v2"));
            if (!assignments.Any(x => x.StageKey == "producer-review"))
                assignments.Add(new WorkStageAssignment("producer-review", "BoardManager", null, null));
        }
        return new FinalizeWorkItemDeliveryRequest(boardId, item.Id,
            new WorkItemDeliverySpecification(repository, plan.Requirements, plan.AcceptanceCriteria, plan.Constraints)
            { BaseBranch = setup.DefaultBranch, DependencyItemIds = plan.DependencyItemIds }, accountable, assignments,
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
        if (item.Delivery is not null || item.ExecutionMode != WorkItemExecutionModes.Executable || item.Planning is null ||
            item.ProposalProvenance is null || item.Status is "Done" or "Completed" ||
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
