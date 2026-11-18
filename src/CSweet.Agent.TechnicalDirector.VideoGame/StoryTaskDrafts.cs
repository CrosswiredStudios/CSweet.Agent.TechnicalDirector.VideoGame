using CSweet.Agent.SDK.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record StoryTaskDrafts(IReadOnlyList<StoryTaskDraft> Tasks);
    internal sealed record StoryTaskDraft(string Title, string Description, string WorkItemTypeKey, string AccountableRoleKey,
        IReadOnlyList<string> RequiredSpecializationKeys, IReadOnlyList<string> PreferredSpecializationKeys,
        IReadOnlyList<int> StoryCriterionIndexes, IReadOnlyList<string> AdditionalCriteria,
        string BrowserVerification, IReadOnlyList<int> InteractionCriteria,
        IReadOnlyList<int> EarlierTaskIndexes, IReadOnlyList<int> PriorTaskIndexes);

    internal static readonly string[] ExecutableGameTypes = [VideoGameWorkItemTypeKeys.Task, VideoGameWorkItemTypeKeys.Bug,
        VideoGameWorkItemTypeKeys.ResearchSpike, VideoGameWorkItemTypeKeys.CreativeReview];

    internal static ProgressiveStoryIndex BindStoryTasks(StoryTaskDrafts draft, GameProposedWorkItemV1 story,
        IReadOnlyList<GameProposedWorkItemV1> prior)
    {
        if (draft.Tasks is not { Count: > 0 and <= 200 }) throw new InvalidOperationException("Supply one to 200 executable tasks.");
        string Key(int ordinal) => $"{story.ProposalKey}.task-{ordinal + 1}";
        var items = new List<GameProposedWorkItemV1>();
        var owners = story.AcceptanceCriteria.Select(_ => new List<string>()).ToArray();
        foreach (var task in draft.Tasks)
        {
            var ordinal = items.Count;
            if (task is null || !ExecutableGameTypes.Contains(task.WorkItemTypeKey))
                throw new InvalidOperationException($"Task {ordinal} needs an executable workItemTypeKey.");
            if (task.AdditionalCriteria is null || task.AdditionalCriteria.Any(string.IsNullOrWhiteSpace) ||
                task.RequiredSpecializationKeys is null || task.PreferredSpecializationKeys is null)
                throw new InvalidOperationException($"Task {ordinal} needs specialization and additional criteria arrays with valid entries.");
            ValidateIndexes(task.StoryCriterionIndexes, owners.Length, ordinal, "storyCriterionIndexes");
            ValidateIndexes(task.EarlierTaskIndexes, ordinal, ordinal, "earlierTaskIndexes");
            ValidateIndexes(task.PriorTaskIndexes, prior.Count, ordinal, "priorTaskIndexes");
            var criteria = task.StoryCriterionIndexes.Select(i => story.AcceptanceCriteria[i]).Concat(task.AdditionalCriteria).ToArray();
            ValidateIndexes(task.InteractionCriteria, criteria.Length, ordinal, "interactionCriteria");
            foreach (var i in task.StoryCriterionIndexes) owners[i].Add(Key(ordinal));
            items.Add(new(Key(ordinal), task.WorkItemTypeKey, task.Title, task.Description, criteria, task.AccountableRoleKey,
                task.RequiredSpecializationKeys, task.PreferredSpecializationKeys, ["work.execution.run"],
                task.EarlierTaskIndexes.Select(Key).Concat(task.PriorTaskIndexes.Select(i => prior[i].ProposalKey)).ToArray())
                { ParentProposalKey = story.ProposalKey, BrowserVerification = task.BrowserVerification, InteractionCriteria = task.InteractionCriteria });
        }
        return new(items, [new("description", items.Select(i => i.ProposalKey).ToArray()),
            .. owners.Select((keys, i) => new ScopeCoverage("criterion-" + i, keys))]);

        static void ValidateIndexes(IReadOnlyList<int>? values, int count, int ordinal, string field)
        {
            if (values is null || values.Distinct().Count() != values.Count || values.Any(i => i < 0 || i >= count))
                throw new InvalidOperationException($"Task {ordinal}: {field} needs unique indexes in the supplied range (count {count}); use [] for none.");
        }
    }
}
