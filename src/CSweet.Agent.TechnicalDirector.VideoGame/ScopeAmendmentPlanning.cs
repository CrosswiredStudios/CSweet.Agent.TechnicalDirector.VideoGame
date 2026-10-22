using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record ScopeReplacement(Guid ItemId, string Field, string Original, string Replacement);
    internal sealed record ScopeAmendmentInstruction(int SchemaVersion, Guid SourceWorkItemId,
        Guid AuthorizingTurnId, IReadOnlyDictionary<Guid, string> SourcePlanningDigests,
        IReadOnlyList<ScopeReplacement> Replacements);

    internal static ScopeAmendmentInstruction ReadScopeAmendmentInstruction(
        AgentCoordinationTurnRequest request, GameProductionPlanningCycleV1 cycle)
    {
        var turn = request.Transcript.OrderByDescending(x => x.Ordinal).FirstOrDefault(x =>
            x.SpeakerOrganizationUserId == request.Counterpart.OrganizationUserId &&
            x.Artifact is { Type: "video-game.production.planning-cycle.v1" } artifact &&
            artifact.Key == cycle.PlanningFingerprint && artifact.Payload.Deserialize<GameProductionPlanningCycleV1>() == cycle);
        if (turn?.Artifact?.Payload.TryGetProperty("scopeAmendment", out var payload) != true)
            throw new InvalidOperationException("Scope amendment requires the authenticated Producer's instruction for this exact cycle.");
        return payload.Deserialize<ScopeAmendmentInstruction>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The scope amendment is empty.");
    }

    // The owner already chose the exact change. Rephrasing an entire backlog is neither
    // necessary nor authority to change unrelated scope. This transformation makes no test claims.
    internal static PlanningOutput BuildScopeAmendmentPlanning(IReadOnlyList<WorkItem> board,
        ScopeAmendmentInstruction instruction)
    {
        if (instruction.SchemaVersion != 1 || instruction.AuthorizingTurnId == Guid.Empty ||
            instruction.SourceWorkItemId == Guid.Empty || instruction.SourcePlanningDigests is null ||
            instruction.Replacements is null || instruction.Replacements.Count is < 1 or > 128 ||
            board.Count is 0 or > 200 || board.Select(x => x.Id).Distinct().Count() != board.Count ||
            !board.Any(x => x.Id == instruction.SourceWorkItemId && x.Status is not ("Done" or "Completed" or "Cancelled")) ||
            instruction.SourcePlanningDigests.Count != board.Count || board.Any(x =>
                !instruction.SourcePlanningDigests.TryGetValue(x.Id, out var digest) ||
                digest != Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(x.Planning)))))
            throw new InvalidOperationException("The scope amendment's authority or source planning binding is invalid or stale.");
        var revised = board.ToDictionary(x => x.Id);
        var seen = new HashSet<(Guid, string, string)>();
        foreach (var edit in instruction.Replacements)
        {
            if (edit is null || !revised.TryGetValue(edit.ItemId, out var item) ||
                item.Status is "Done" or "Completed" or "Cancelled" || item.Planning is not { } planning ||
                string.IsNullOrWhiteSpace(edit.Original) || string.IsNullOrWhiteSpace(edit.Replacement) ||
                edit.Original == edit.Replacement || !seen.Add((edit.ItemId, edit.Field, edit.Original)))
                throw new InvalidOperationException("Invalid, duplicate or completed-work scope replacement.");
            IReadOnlyList<string> Entries(WorkItemPlanningSpecification p) => edit.Field switch
            {
                "requirements" => p.Requirements, "acceptanceCriteria" => p.AcceptanceCriteria,
                "constraints" => p.Constraints ?? [],
                _ => throw new InvalidOperationException("Scope replacement field is unsupported.")
            };
            var originals = Entries(board.Single(x => x.Id == edit.ItemId).Planning!);
            var current = Entries(planning);
            if (originals.Count(x => x == edit.Original) != 1 || current.Count(x => x == edit.Original) != 1 ||
                originals.Contains(edit.Replacement, StringComparer.Ordinal))
                throw new InvalidOperationException("Scope replacement does not match one immutable source entry.");
            var values = current.Select(x => x == edit.Original ? edit.Replacement : x).ToArray();
            planning = edit.Field switch
            {
                "requirements" => planning with { Requirements = values },
                "acceptanceCriteria" => planning with { AcceptanceCriteria = values },
                _ => planning with { Constraints = values }
            };
            revised[item.Id] = item with { Planning = planning,
                Description = edit.Field == "requirements"
                    ? (item.Description ?? "").Replace(edit.Original, edit.Replacement, StringComparison.Ordinal) : item.Description };
        }
        var items = revised.Values.Where(x => x.Status != "Cancelled" && x.ProposalProvenance is not null &&
            x.TypeKey != "work.workflow-ticket.v1").OrderBy(x => x.ProposalProvenance!.ProposalItemKey, StringComparer.Ordinal).ToArray();
        if (items.Length == 0 || items.Select(x => x.ProposalProvenance!.ProposalItemKey).Distinct().Count() != items.Length)
            throw new InvalidOperationException("Scope amendment requires unique canonical proposal identities.");
        var byId = items.ToDictionary(x => x.Id);
        string Key(Guid id) => byId.TryGetValue(id, out var item) ? item.ProposalProvenance!.ProposalItemKey
            : throw new InvalidOperationException("A proposal parent or dependency is outside the canonical scope.");
        var proposals = items.Select(item =>
        {
            var planning = item.Planning ?? throw new InvalidOperationException("Canonical scope has no planning specification.");
            var role = ExecutionRequirements(item);
            if (!IsPlanningContainer(item) && role is null)
                throw new InvalidOperationException("An executable ticket has no recorded role requirements.");
            var description = item.Description ?? "";
            foreach (var requirement in planning.Requirements)
                if (!description.Contains(requirement, StringComparison.Ordinal)) description += "\n\n" + requirement;
            return new GameProposedWorkItemV1(Key(item.Id), item.TypeKey, DisplayTitle(item.Title), description,
                planning.AcceptanceCriteria, role?.RequiredRoleKey ?? VideoGameRoleKeys.TechnicalDirector,
                role?.RequiredSpecializationKeys ?? [], role?.PreferredSpecializationKeys ?? [],
                role?.RequiredCapabilityKeys ?? [], planning.DependencyItemIds.Select(Key).ToArray())
                { ParentProposalKey = item.ParentItemId is { } parent ? Key(parent) : null };
        }).ToArray();
        return new(proposals, ["Applied only the authenticated exact scope replacements; independent QA remains required."],
            revised.Values.Where(x => x.Status is not ("Done" or "Completed" or "Cancelled"))
                .SelectMany(x => x.Planning?.Constraints ?? []).Distinct(StringComparer.Ordinal).ToArray(), []);
    }

    private static bool IsPlanningContainer(WorkItem item) => item.TypeKey is
        VideoGameWorkItemTypeKeys.Milestone or VideoGameWorkItemTypeKeys.Feature or VideoGameWorkItemTypeKeys.Content;
}
