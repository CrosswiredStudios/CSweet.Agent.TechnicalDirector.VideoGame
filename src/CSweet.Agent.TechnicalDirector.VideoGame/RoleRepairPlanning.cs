using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record RoleRepairInstruction(int SchemaVersion, Guid SourceWorkItemId, string SourcePlanningSha256);

    internal static RoleRepairInstruction ReadRoleRepairInstruction(AgentCoordinationTurnRequest request,
        GameProductionPlanningCycleV1 cycle)
    {
        var turn = request.Transcript.OrderByDescending(x => x.Ordinal).FirstOrDefault(x =>
            x.SpeakerOrganizationUserId == request.Counterpart.OrganizationUserId &&
            x.Artifact is { Type: "video-game.production.planning-cycle.v1" } artifact &&
            artifact.Key == cycle.PlanningFingerprint && artifact.Payload.Deserialize<GameProductionPlanningCycleV1>() == cycle);
        if (turn?.Artifact?.Payload.TryGetProperty("roleRepair", out var payload) != true)
            throw new InvalidOperationException("Structured role repair requires the authenticated Producer's instruction for this exact cycle.");
        var instruction = payload.Deserialize<RoleRepairInstruction>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (instruction is not { SchemaVersion: 1 } || instruction.SourceWorkItemId == Guid.Empty ||
            instruction.SourcePlanningSha256 is not { Length: 64 })
            throw new InvalidOperationException("Unsupported or incomplete structured role repair instruction.");
        return instruction;
    }

    // Scope repair is a lossless transformation of canonical work, not a new creative proposal.
    // The retained TD task still requires an actual technical plan; this proposal does not claim it exists.
    internal static PlanningOutput BuildRoleRepairPlanning(IReadOnlyList<WorkItem> boardItems, RoleRepairInstruction instruction)
    {
        var items = boardItems.Where(x => x.Status != WorkStatuses.Cancelled && x.ProposalProvenance is not null)
            .OrderBy(x => x.ProposalProvenance!.ProposalItemKey, StringComparer.Ordinal).ToArray();
        if (items.Length is 0 or > 198 || items.Select(x => x.Id).Distinct().Count() != items.Length ||
            items.Select(x => x.ProposalProvenance!.ProposalItemKey).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new InvalidOperationException("Role repair requires a bounded board with unique canonical identities.");
        var source = items.SingleOrDefault(x => x.Id == instruction.SourceWorkItemId);
        if (instruction.SchemaVersion != 1 || source?.Planning is null || source.Status is "Done" or "Completed" ||
            source.TypeKey is not (VideoGameWorkItemTypeKeys.Task or VideoGameWorkItemTypeKeys.ResearchSpike or VideoGameWorkItemTypeKeys.Bug) ||
            ExecutionRequirements(source)?.RequiredRoleKey != VideoGameRoleKeys.TechnicalDirector)
            throw new InvalidOperationException("Role repair requires an unfinished Technical Director execution ticket with canonical planning.");
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(source.Planning))).ToLowerInvariant();
        if (!string.Equals(digest, instruction.SourcePlanningSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The mixed ticket's canonical planning changed; reassess the role repair.");
        var byId = items.ToDictionary(x => x.Id);
        string Key(Guid id) => byId.TryGetValue(id, out var item) ? item.ProposalProvenance!.ProposalItemKey :
            throw new InvalidOperationException("Role repair cannot omit a dependency or parent outside the canonical proposal.");
        var implementationKey = $"role-repair-{source.Id:N}-implementation";
        var validationKey = $"role-repair-{source.Id:N}-validation";
        if (items.Any(x => Key(x.Id) == implementationKey || Key(x.Id) == validationKey))
            throw new InvalidOperationException("Role repair output keys already exist; reconcile the existing proposal instead of duplicating it.");
        var proposals = new List<GameProposedWorkItemV1>();
        foreach (var item in items)
        {
            var planning = item.Planning ?? throw new InvalidOperationException("Canonical scope is missing its planning specification.");
            var requirements = ExecutionRequirements(item);
            var container = item.TypeKey is VideoGameWorkItemTypeKeys.Milestone or VideoGameWorkItemTypeKeys.Feature or VideoGameWorkItemTypeKeys.Content;
            if (!container && requirements is null)
                throw new InvalidOperationException("An executable canonical ticket is missing its recorded role requirements.");
            var description = string.Join("\n\n", new[] { item.Description }.Concat(planning.Requirements).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal));
            proposals.Add(new(Key(item.Id), item.TypeKey, DisplayTitle(item.Title), description,
                planning.AcceptanceCriteria, requirements?.RequiredRoleKey ?? VideoGameRoleKeys.TechnicalDirector,
                requirements?.RequiredSpecializationKeys ?? [], requirements?.PreferredSpecializationKeys ?? [],
                requirements?.RequiredCapabilityKeys ?? [],
                planning.DependencyItemIds.Select(id => id == source.Id && item.Status is not ("Done" or "Completed")
                    ? validationKey : Key(id)).Distinct(StringComparer.Ordinal).ToArray())
            { ParentProposalKey = item.ParentItemId is { } parent ? Key(parent) : null });
        }
        var sourceIndex = proposals.FindIndex(x => x.ProposalKey == Key(source.Id));
        var original = proposals[sourceIndex];
        var scope = original.Description + "\n\nOriginal delivery acceptance criteria:\n" +
            string.Join("\n", original.AcceptanceCriteria.Select((text, index) => $"{index + 1}. {text}"));
        proposals[sourceIndex] = original with
        {
            Title = "Technical plan: " + DisplayTitle(source.Title),
            Description = "Produce a technical plan for the following delivery scope. Document decisions, interfaces, implementation steps, " +
                "test methods, performance budgets and unresolved risks. Engineering performs implementation and commits; QA independently verifies it. " +
                "The following source scope is reference material for the plan, not a requirement for the Technical Director to execute it.\n\n" + scope,
            AcceptanceCriteria = [
                "The technical plan maps every original delivery criterion to concrete implementation steps, interfaces, artifacts and engineering handoffs.",
                "The technical plan specifies reproducible validation methods, environments, thresholds and required evidence for every original delivery criterion.",
                "The technical plan records technical decisions, dependencies and unresolved risks without claiming unperformed implementation or measured results."]
        };
        proposals.Add(new(implementationKey, VideoGameWorkItemTypeKeys.Task, "Implement foundation: " + DisplayTitle(source.Title),
            "Implement the original delivery scope using the completed Technical Director plan. Produce the required repository artifacts and measurement evidence; " +
            "report unmet requirements as blockers rather than claiming completion.\n\n" + scope, original.AcceptanceCriteria,
            VideoGameRoleKeys.Engineer, [], [], ["work.execution.run.v1"], [original.ProposalKey]) { ParentProposalKey = original.ParentProposalKey });
        proposals.Add(new(validationKey, VideoGameWorkItemTypeKeys.Task, "Independently validate: " + DisplayTitle(source.Title),
            "Independently verify the engineering deliverables against every original acceptance criterion below, using the Technical Director's validation plan. " +
            "Inspect the delivered revision and execute the required tests; record unavailable environments and failed checks as blockers.\n\n" + scope,
            ["Independent QA records a pass or failure with reproducible evidence for every original delivery acceptance criterion against the exact delivered revision; all required checks pass."],
            VideoGameRoleKeys.QualityAssurance, [], [], ["work.execution.run.v1"], [implementationKey]) { ParentProposalKey = original.ParentProposalKey });
        if (!IsValidPlan(proposals))
            throw new InvalidOperationException("The canonical role split failed hierarchy, role, capability or dependency validation; no replacement scope was produced.");
        return new(proposals, ["The role split defines future work. Technical planning, implementation and independent validation still require execution."],
            items.SelectMany(x => x.Planning!.Constraints ?? []).Distinct(StringComparer.Ordinal).ToArray(), []);
    }

    private static WorkAssignmentRequirements? ExecutionRequirements(WorkItem item)
    {
        var assignment = item.StageAssignments.FirstOrDefault(x => x.StageKey == "specialist-execution")?.Requirements;
        if (assignment is not null) return assignment;
        var recommendation = item.Planning?.DelegationRecommendations.FirstOrDefault(x => x.StageKey == "specialist-execution");
        return recommendation is null ? null : new(recommendation.RequiredRoleKey, recommendation.RequiredSpecializationKeys,
            recommendation.PreferredSpecializationKeys, recommendation.RequiredCapabilityKeys);
    }
}