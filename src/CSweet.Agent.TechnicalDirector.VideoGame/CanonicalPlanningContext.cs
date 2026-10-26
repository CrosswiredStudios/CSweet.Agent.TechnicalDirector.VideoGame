using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static string CanonicalPlanningContext(IReadOnlyList<WorkItem> items, bool progressive)
    {
        var byId = items.ToDictionary(x => x.Id);
        var active = items.Where(x => x.Status != WorkStatuses.Cancelled && x.ProposalProvenance is not null)
            .OrderBy(x => x.ProposalProvenance!.ProposalItemKey, StringComparer.Ordinal);
        string? Parent(WorkItem x) => x.ParentItemId is { } id ? byId.GetValueOrDefault(id)?.ProposalProvenance?.ProposalItemKey : null;
        IEnumerable<string?>? Dependencies(WorkItem x) => x.Planning?.DependencyItemIds.Select(id => byId.GetValueOrDefault(id)?.ProposalProvenance?.ProposalItemKey);
        if (progressive)
            // The frozen scope and selected Story are validated separately. Repeating
            // every container's full criteria/constraints made the first increment exceed context.
            return JsonSerializer.Serialize(active.Select(x => new {
                x.Id, proposalKey = x.ProposalProvenance!.ProposalItemKey, x.TypeKey,
                title = DisplayTitle(x.Title), parentProposalKey = Parent(x),
                dependencyProposalKeys = Dependencies(x), x.Status, inSprint = x.SprintId is not null
            }));
        return JsonSerializer.Serialize(active.Select(x => new {
            x.Id, proposalKey = x.ProposalProvenance!.ProposalItemKey, x.TypeKey,
            title = DisplayTitle(x.Title), parentProposalKey = Parent(x), x.Description,
            requirements = x.Planning?.Requirements, acceptanceCriteria = x.Planning?.AcceptanceCriteria,
            constraints = x.Planning?.Constraints, dependencies = x.Planning?.DependencyItemIds,
            dependencyProposalKeys = Dependencies(x), currentDelegations = x.Planning?.DelegationRecommendations,
            x.Status, inSprint = x.SprintId is not null
        }));
    }
}
