using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static IReadOnlyList<string> PlanningItemMetadataIssues(GameProposedWorkItemV1 item)
    {
        var issues = new List<string>();
        void Error(string field, string message) => issues.Add($"Item '{item.ProposalKey}': {field} {message}");
        if (string.IsNullOrWhiteSpace(item.ProposalKey)) Error("proposalKey", "is required.");
        if (string.IsNullOrWhiteSpace(item.Title)) Error("title", "is required.");
        if (string.IsNullOrWhiteSpace(item.Description)) Error("description", "is required.");
        if (item.AcceptanceCriteria is null || item.AcceptanceCriteria.Count == 0 || item.AcceptanceCriteria.Any(string.IsNullOrWhiteSpace))
            Error("acceptanceCriteria", "must contain nonblank, testable criteria.");
        var roles = Constants(typeof(VideoGameRoleKeys));
        if (!roles.Contains(item.AccountableRoleKey)) Error("accountableRoleKey", $"'{item.AccountableRoleKey}' is unsupported. Allowed roles: {string.Join(", ", roles.Order())}.");
        var types = Constants(typeof(VideoGameWorkItemTypeKeys));
        if (!types.Contains(item.WorkItemTypeKey)) Error("workItemTypeKey", $"'{item.WorkItemTypeKey}' is unsupported. Allowed types: {string.Join(", ", types.Order())}.");
        var skills = Constants(typeof(VideoGameSpecializationKeys));
        foreach (var (field, values) in new[] { ("requiredSpecializationKeys", item.RequiredSpecializationKeys), ("preferredSpecializationKeys", item.PreferredSpecializationKeys) })
        {
            if (values is null) { Error(field, "must be an array (use [] when empty)."); continue; }
            var invalid = values.Where(x => !skills.Contains(x)).Distinct().ToArray();
            if (invalid.Length > 0) Error(field, $"contains unsupported values [{string.Join(", ", invalid)}]. work.execution.run is a capability, not a specialization. Allowed skills: {string.Join(", ", skills.Order())}. Use [] when empty.");
        }
        if (item.RequiredCapabilityKeys is null || item.RequiredCapabilityKeys.Any(x => x != "work.execution.run"))
            Error("requiredCapabilityKeys", "must be an array containing only work.execution.run.");
        return issues;
    }

    // Use the same diagnostics for acceptance, model correction, and the durable blocked result.
    internal static IReadOnlyList<string> PlanValidationIssues(IReadOnlyList<GameProposedWorkItemV1>? items)
    {
        if (items is null || items.Count is 0 or > 200)
            return ["deliveryItems must contain 1 to 200 items."];
        if (items.Any(x => x is null)) return ["deliveryItems contains a null item; replace it with a complete work item."];
        var issues = new List<string>();
        var stories = new HashSet<string>([VideoGameWorkItemTypeKeys.Feature, VideoGameWorkItemTypeKeys.Content], StringComparer.Ordinal);
        var byKey = items.Where(x => !string.IsNullOrWhiteSpace(x.ProposalKey))
            .GroupBy(x => x.ProposalKey, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        foreach (var duplicate in items.GroupBy(x => x.ProposalKey).Where(x => x.Count() > 1))
            issues.Add($"proposalKey '{duplicate.Key}' is duplicated; every item must have a unique key.");
        foreach (var item in items)
        {
            var key = string.IsNullOrWhiteSpace(item.ProposalKey) ? "<missing key>" : item.ProposalKey;
            void Error(string message) => issues.Add($"Item '{key}': {message}");
            issues.AddRange(PlanningItemMetadataIssues(item));
            if (item.DependencyProposalKeys is null) Error("dependencyProposalKeys must be an array (use [] when empty).");
            else foreach (var dependency in item.DependencyProposalKeys)
            {
                if (dependency == item.ProposalKey) Error("dependencyProposalKeys names the item itself; remove the self dependency.");
                else if (dependency is null || !byKey.ContainsKey(dependency)) Error($"dependencyProposalKeys references missing item '{dependency}'.");
            }
            if (item.ParentProposalKey == item.ProposalKey && item.ParentProposalKey is not null)
                Error("parentProposalKey names the item itself. A story must belong to a milestone; a task must belong to a story.");
            else if (item.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Milestone)
            {
                if (item.ParentProposalKey is not null) Error("a milestone must have parentProposalKey null.");
            }
            else if (item.ParentProposalKey is null || !byKey.TryGetValue(item.ParentProposalKey, out var parent))
                Error($"parentProposalKey '{item.ParentProposalKey}' must resolve to an existing {(stories.Contains(item.WorkItemTypeKey) ? "milestone" : "feature/content story")}.");
            else if (stories.Contains(item.WorkItemTypeKey) ? parent.WorkItemTypeKey != VideoGameWorkItemTypeKeys.Milestone : !stories.Contains(parent.WorkItemTypeKey))
                Error($"parentProposalKey '{item.ParentProposalKey}' has type '{parent.WorkItemTypeKey}'; expected {(stories.Contains(item.WorkItemTypeKey) ? "milestone" : "feature/content story")}.");
        }
        if (!items.Any(x => x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Milestone)) issues.Add("Add a milestone (Epic).");
        if (!items.Any(x => stories.Contains(x.WorkItemTypeKey))) issues.Add("Add a feature or content story under a milestone.");
        if (!items.Any(x => x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Task && x.AccountableRoleKey == VideoGameRoleKeys.Engineer))
            issues.Add("Add an engineering task with accountableRoleKey game-engineer.");
        if (!items.Any(x => x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Task && x.AccountableRoleKey == VideoGameRoleKeys.QualityAssurance))
            issues.Add("Add a separate QA task with accountableRoleKey game-quality-assurance.");
        // Find an actual cycle, not the unrelated descendants that merely wait on it.
        if (byKey.Count == items.Count && items.All(x => x.DependencyProposalKeys is not null))
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var path = new List<string>();
            bool Visit(string key)
            {
                var index = path.IndexOf(key);
                if (index >= 0)
                {
                    issues.Add($"Parent/dependency cycle: {string.Join(" -> ", path.Skip(index).Append(key))}. Remove the cycle; a story cannot depend on its descendants.");
                    return true;
                }
                if (!visited.Add(key)) return false;
                path.Add(key);
                var item = byKey[key];
                var edges = item.DependencyProposalKeys.Concat(item.ParentProposalKey is null ? [] : new[] { item.ParentProposalKey });
                foreach (var edge in edges.Where(x => x is not null && byKey.ContainsKey(x)))
                    if (Visit(edge)) return true;
                path.RemoveAt(path.Count - 1);
                return false;
            }
            foreach (var key in byKey.Keys) if (Visit(key)) break;
        }
        return issues;
    }
}
