using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record RoadmapEpic(string Title, string Outcome, IReadOnlyList<string> AcceptanceCriteria,
        IReadOnlyList<int> SourceIndexes, IReadOnlyList<int> PrerequisiteIndexes);
    internal sealed record RoadmapDraft(IReadOnlyList<RoadmapEpic> Epics, IReadOnlyList<string> FeasibilityFindings,
        IReadOnlyList<string> TechnicalConstraints, IReadOnlyList<string> OpenFeasibilityDecisions);
    internal sealed record StorySlice(string Title, string Outcome, IReadOnlyList<string> AcceptanceCriteria,
        IReadOnlyList<int> EpicCriterionIndexes, IReadOnlyList<int> PrerequisiteIndexes);
    internal sealed record StorySlices(IReadOnlyList<StorySlice> Stories);

    internal static ProgressiveScopeIndex BindRoadmap(RoadmapDraft draft, IReadOnlyList<ScopeSource> sources)
    {
        if (draft.Epics is not { Count: > 0 } || draft.FeasibilityFindings is null ||
            draft.TechnicalConstraints is null || draft.OpenFeasibilityDecisions is null)
            throw new InvalidOperationException("Provide an Epic roadmap and the three findings arrays.");
        string Key(int index) => $"roadmap-epic-{index + 1}";
        var items = new List<GameProposedWorkItemV1>();
        var owners = sources.Select(_ => new List<string>()).ToArray();
        foreach (var epic in draft.Epics)
        {
            int index = items.Count;
            if (epic is null || epic.SourceIndexes is not { Count: > 0 } || epic.SourceIndexes.Any(i => i < 0 || i >= sources.Count) ||
                epic.PrerequisiteIndexes is null || epic.PrerequisiteIndexes.Any(i => i < 0 || i >= index))
                throw new InvalidOperationException("Give each Epic valid source indexes and only earlier prerequisites.");
            var item = new GameProposedWorkItemV1(Key(index), VideoGameWorkItemTypeKeys.Milestone, epic.Title, epic.Outcome + "\nApproved sources: " + string.Join(", ", epic.SourceIndexes.Distinct().Select(i => sources[i].SourceKey)),
                epic.AcceptanceCriteria, VideoGameRoleKeys.TechnicalDirector, [], [], [], epic.PrerequisiteIndexes.Distinct().Select(Key).ToArray());
            var issues = PlanningItemMetadataIssues(item);
            if (issues.Count != 0) throw new InvalidOperationException(string.Join(". ", issues));
            items.Add(item);
            foreach (var i in epic.SourceIndexes.Distinct()) owners[i].Add(item.ProposalKey);
        }
        var coverage = sources.Select((s, i) => new ScopeCoverage(s.SourceKey, owners[i])).ToArray();
        ValidateScopeCoverage(coverage, sources, items.Select(i => i.ProposalKey).ToHashSet());
        return new(new(items, draft.FeasibilityFindings, draft.TechnicalConstraints, draft.OpenFeasibilityDecisions), coverage);
    }

    internal static IReadOnlyList<GameProposedWorkItemV1> BindSlices(GameProposedWorkItemV1 epic, StorySlices draft)
    {
        if (draft.Stories is not { Count: > 0 }) throw new InvalidOperationException("Provide at least one independently verifiable Story.");
        var assigned = new HashSet<int>();
        var result = new List<GameProposedWorkItemV1>();
        string Key(int index) => $"{epic.ProposalKey}.story-{index + 1}";
        foreach (var slice in draft.Stories)
        {
            if (slice is null || slice.EpicCriterionIndexes is null || slice.EpicCriterionIndexes.Any(i => i < 0 || i >= epic.AcceptanceCriteria.Count) ||
                slice.PrerequisiteIndexes is null || slice.PrerequisiteIndexes.Any(i => i < 0 || i >= result.Count) ||
                slice.AcceptanceCriteria is null)
                throw new InvalidOperationException("Use valid Epic criteria and earlier Story prerequisite indexes.");
            assigned.UnionWith(slice.EpicCriterionIndexes);
            var item = new GameProposedWorkItemV1(Key(result.Count), VideoGameWorkItemTypeKeys.Feature, slice.Title, slice.Outcome,
                slice.AcceptanceCriteria.Concat(slice.EpicCriterionIndexes.Select(i => epic.AcceptanceCriteria[i])).Distinct().ToArray(),
                VideoGameRoleKeys.TechnicalDirector, [], [], [], slice.PrerequisiteIndexes.Distinct().Select(Key).ToArray()) { ParentProposalKey = epic.ProposalKey };
            var issues = PlanningItemMetadataIssues(item);
            if (issues.Count > 0) throw new InvalidOperationException(string.Join(". ", issues));
            result.Add(item);
        }
        if (assigned.Count != epic.AcceptanceCriteria.Count) throw new InvalidOperationException("Assign every approved Epic criterion to a Story.");
        return result;
    }

    private async Task<ProgressiveScopeIndex> ReadRoadmapAsync(AgentRuntimeContext context,
        Func<IReadOnlyList<ChatMessage>, Type, CancellationToken, Task<ChatResponse>> generate,
        IReadOnlyList<ChatMessage> messages, string key, IReadOnlyList<ScopeSource> sources, bool mayGenerate, CancellationToken token)
    {
        var saved = await context.Platform.ReadOperatingStateAsync<ProgressiveScopeIndex>(key, token);
        if (saved is not null) return saved.Payload;
        if (!mayGenerate) throw new InvalidOperationException("The requested approved roadmap is missing.");
        var draft = await ReadPlanningPageAsync<RoadmapDraft>(generate, messages, "Epic roadmap",
            "Group the supplied accepted sources into outcome Epics. Give each a concise title, outcome, testable acceptanceCriteria, zero-based sourceIndexes and earlier prerequisiteIndexes. " +
            "Cover every source including constraints and non-goals. Choose the smallest first experiment or deliverable that resolves the most consequential uncertainty. " +
            "Keep hypotheses distinct from authority questions. Specialist workload depends on this game's decisions, content and evidence. Return only Epics and the three findings arrays.",
            value => BindRoadmap(value, sources), MaximumPlanningPageCharacters, token);
        return (await SaveProgressiveStateAsync(key, BindRoadmap(draft, sources), context, token)).Payload;
    }

    private async Task<PlanningOutput> ExpandApproachingEpicsAsync(ProgressiveScopeIndex roadmap, ProgressivePlanningRequest request,
        AgentRuntimeContext context, Func<IReadOnlyList<ChatMessage>, Type, CancellationToken, Task<ChatResponse>> generate,
        IReadOnlyList<ChatMessage> messages, string key, CancellationToken token)
    {
        var items = roadmap.Plan.DeliveryItems.ToList();
        var selected = (request.DetailedStoryKeys ?? []).Append(request.StoryKey ?? "").ToArray();
        foreach (var epic in roadmap.Plan.DeliveryItems.Where(e => selected.Any(s => s.StartsWith(e.ProposalKey + ".story-", StringComparison.Ordinal))))
        {
            var sliceKey = key + ":slices:" + epic.ProposalKey;
            var saved = await context.Platform.ReadOperatingStateAsync<StorySlices>(sliceKey, token);
            if (saved is null && request.StoryKey != epic.ProposalKey + ".story-1")
                throw new InvalidOperationException("The requested prior Story definition is missing.");
            var slices = saved?.Payload ?? await ReadPlanningPageAsync<StorySlices>(generate, messages, "approaching Epic",
                "Split ONLY this approaching Epic into independently verifiable Stories. Keep a single Story when it suffices. Give each title, outcome, acceptanceCriteria, zero-based epicCriterionIndexes and earlier prerequisiteIndexes. " +
                "Every Epic criterion must have an owner. One Story may be a research experiment. Epic: " + JsonSerializer.Serialize(epic, PlanningJson),
                value => BindSlices(epic, value), MaximumPlanningPageCharacters, token);
            if (saved is null) slices = (await SaveProgressiveStateAsync(sliceKey, slices, context, token)).Payload;
            items.AddRange(BindSlices(epic, slices));
        }
        return roadmap.Plan with { DeliveryItems = items };
    }

    internal static GameProposedWorkItemV1 BindCriteria(string packageDigest, GameProposedWorkItemV1 story, GameProposedWorkItemV1 task)
    {
        var indexes = (task.InteractionCriteria ?? []).ToHashSet();
        return task with { CriterionBindings = task.AcceptanceCriteria.Select((text, i) =>
        {
            var source = story.AcceptanceCriteria.Contains(text, StringComparer.Ordinal) ? story.ProposalKey : task.ProposalKey;
            var key = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { packageDigest, source, text })));
            return new GameCriterionBinding(key, source, text, indexes.Contains(i));
        }).ToArray() };
    }
}
