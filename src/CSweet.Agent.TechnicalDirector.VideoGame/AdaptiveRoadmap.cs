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

    // constraintSources are binding constraints and exclusions. They restrict work and are checked on the work
    // they restrict; they are never the reason an Epic exists. Without this rule the only place to put approved
    // non-goals is a governance Epic, which then becomes the roadmap's sole dependency-free first increment.
    internal static ProgressiveScopeIndex BindRoadmap(RoadmapDraft draft, IReadOnlyList<ScopeSource> sources,
        IReadOnlySet<int>? constraintSources = null)
    {
        var enforceOutcomes = constraintSources is { Count: > 0 } && constraintSources.Count < sources.Count;
        if (draft.Epics is not { Count: > 0 } || draft.FeasibilityFindings is null ||
            draft.TechnicalConstraints is null || draft.OpenFeasibilityDecisions is null)
            throw new InvalidOperationException("Provide an Epic roadmap and the three findings arrays.");
        string Key(int index) => $"roadmap-epic-{index + 1}";
        var items = new List<GameProposedWorkItemV1>();
        var owners = sources.Select(_ => new List<string>()).ToArray();
        var constraintOnly = new List<string>();
        foreach (var epic in draft.Epics)
        {
            int index = items.Count;
            if (epic is null) throw new InvalidOperationException($"Epic at index {index} is missing.");
            var label = $"Epic {index} ({epic.Title})";
            if (epic.SourceIndexes is not { Count: > 0 })
                throw new InvalidOperationException($"{label} needs at least one sourceIndexes entry from the numbered ledger.");
            var invalidSources = epic.SourceIndexes.Where(i => i < 0 || i >= sources.Count).Distinct().Take(12).ToArray();
            if (invalidSources.Length > 0)
                throw new InvalidOperationException($"{label} sourceIndexes contains {string.Join(", ", invalidSources)} outside the supplied range 0..{sources.Count - 1}.");
            if (enforceOutcomes && epic.SourceIndexes.All(constraintSources!.Contains))
                constraintOnly.Add($"{label} [{string.Join(", ", epic.SourceIndexes.Distinct().Take(12))}]");
            if (epic.PrerequisiteIndexes is null)
                throw new InvalidOperationException($"{label} needs prerequisiteIndexes. Use [] for independent work.");
            foreach (var prerequisite in epic.PrerequisiteIndexes)
                if (prerequisite < 0 || prerequisite >= draft.Epics.Count || prerequisite == index)
                    throw new InvalidOperationException($"{label} prerequisiteIndexes contains {prerequisite}. Use another Epic index in 0..{draft.Epics.Count - 1}.");
            var item = new GameProposedWorkItemV1(Key(index), VideoGameWorkItemTypeKeys.Milestone, epic.Title, epic.Outcome,
                epic.AcceptanceCriteria, VideoGameRoleKeys.TechnicalDirector, [], [], [], epic.PrerequisiteIndexes.Distinct().Select(Key).ToArray());
            var issues = PlanningItemMetadataIssues(item);
            if (issues.Count != 0) throw new InvalidOperationException(string.Join(". ", issues));
            items.Add(item);
            foreach (var i in epic.SourceIndexes.Distinct()) owners[i].Add(item.ProposalKey);
        }
        // Name every such Epic in one correction, so one reply can fix them all.
        if (constraintOnly.Count > 0)
            throw new InvalidOperationException($"{constraintOnly.Count} Epic(s) own only constraints or exclusions: {string.Join("; ", constraintOnly.Take(12))}. " +
                "A constraint restricts work; it is not work. Remove each of these Epics and attach its sourceIndexes, with a criterion that can be checked, " +
                "to the outcome Epics they restrict. Every Epic must deliver at least one committed outcome.");
        var issuesWithDependencies = DependencyCycleIssues(items.ToDictionary(item => item.ProposalKey));
        if (issuesWithDependencies.Count > 0) throw new InvalidOperationException(string.Join(". ", issuesWithDependencies));
        var missing = Enumerable.Range(0, owners.Length).Where(i => owners[i].Count == 0).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Assign all {missing.Length} uncovered sources to relevant Epics. Missing sourceIndexes include {string.Join(", ", missing.Take(24))}.");
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

    internal sealed record ApprovedCommitment(string Statement, IReadOnlyList<int> SourceIndexes)
    {
        // "Outcome" or "Constraint". Inventories retained from earlier releases have no kind and are treated as outcomes.
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Kind { get; init; }
    }
    internal sealed record ApprovedCommitments(IReadOnlyList<ApprovedCommitment> Commitments);

    internal static void ValidateCommitments(ApprovedCommitments value, int sourceCount)
    {
        if (value.Commitments is not { Count: > 0 })
            throw new InvalidOperationException("Identify the approved deliverable outcomes and binding constraints.");
        var statements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in value.Commitments)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Statement) || !statements.Add(entry.Statement.Trim()))
                throw new InvalidOperationException("Each commitment needs a distinct substantive statement.");
            if (entry.Kind is not (null or "Outcome" or "Constraint"))
                throw new InvalidOperationException($"Use kind Outcome or Constraint for '{entry.Statement}'.");
        }
        // Report every uncited entry together. Naming one per attempt let a reply with four of them
        // run out of attempts while it was still converging.
        var uncited = value.Commitments.Where(entry => !IsCited(entry, sourceCount)).Select(entry => entry.Statement.Trim()).ToArray();
        if (uncited.Length > 0)
            throw new InvalidOperationException($"{uncited.Length} commitment(s) do not cite a valid accepted source index in 0..{sourceCount - 1}. " +
                "A commitment must come from the numbered accepted sources. For each one, give its sourceIndexes, or remove it if it comes from " +
                "manager direction, resource notes or other context instead of an accepted source: " +
                string.Join(" | ", uncited.Take(12).Select(statement => statement.Length <= 120 ? statement : statement[..120])));
    }

    private static bool IsCited(ApprovedCommitment entry, int sourceCount) =>
        entry.SourceIndexes is { Count: > 0 } && entry.SourceIndexes.All(i => i >= 0 && i < sourceCount);

    // An approved commitment is by definition supported by an accepted source. A statement the model cannot
    // tie to one, after being asked, is context and is left out; the Producer's ownership review reads the
    // original sources for omissions independently.
    internal static ApprovedCommitments WithoutUncited(ApprovedCommitments value, int sourceCount, out IReadOnlyList<string> omitted)
    {
        omitted = (value.Commitments ?? []).Where(entry => entry is not null && !IsCited(entry, sourceCount))
            .Select(entry => entry.Statement?.Trim() ?? "").ToArray();
        return omitted.Count == 0 ? value : new((value.Commitments ?? []).Where(entry => entry is not null && IsCited(entry, sourceCount)).ToArray());
    }

    private async Task<ProgressiveScopeIndex> ReadRoadmapAsync(AgentRuntimeContext context,
        Func<IReadOnlyList<ChatMessage>, Type, CancellationToken, Task<ChatResponse>> generate,
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatMessage> projectMessages, string key,
        IReadOnlyList<ScopeSource> sources, bool mayGenerate, Action<int> setRoadmapSourceCount, CancellationToken token)
    {
        var saved = await context.Platform.ReadOperatingStateAsync<ProgressiveScopeIndex>(key, token);
        if (saved is not null) return saved.Payload;
        if (!mayGenerate) throw new InvalidOperationException("The requested approved roadmap is missing.");
        var inventoryKey = key + ":commitments";
        var inventory = (await context.Platform.ReadOperatingStateAsync<ApprovedCommitments>(inventoryKey, token))?.Payload;
        if (inventory == null)
        {
            var inventoryAttempts = 0;
            inventory = await ReadPlanningPageAsync<ApprovedCommitments>(generate, messages, "approved outcomes and constraints",
                "Read the complete accepted product sources as evidence, independently of the currently requested increment. Identify every distinct committed player or delivery outcome and binding constraint. " +
                "Deferring detail is not omitting a later outcome from the product roadmap. A first-increment request does not narrow approved product scope. " +
                "Keep testable thresholds, quantities, exclusions and conditions. Consolidate repetitions. A paragraph may contain several commitments, " +
                "and several passages may support one. Headings, rationale, examples, history and unapproved suggestions are context. " +
                "Return commitments with a concise statement, the sourceIndexes supporting it and a kind. Distinguish a binding exclusion from a future suggestion. " +
                "Kind is Outcome for something the team must build, produce or deliver, including a document the source explicitly requires as a deliverable. " +
                "Kind is Constraint for a rule, limit, exclusion, non-goal, authority boundary or evidence requirement that restricts how outcomes are delivered. " +
                "Every commitment must cite at least one numbered accepted source. Manager directions, resource or asset notes and other conversation context are not commitments; do not list them.",
                // The first two attempts ask the model to cite or remove uncited entries. The last attempt
                // accepts the cited ones, so a few uncitable statements cannot fail the whole roadmap.
                value => ValidateCommitments(++inventoryAttempts < 3 ? value : WithoutUncited(value, sources.Count, out _), sources.Count),
                MaximumPlanningPageCharacters, token, includeRejectedDraft: true);
            inventory = WithoutUncited(inventory, sources.Count, out var omitted);
            if (omitted.Count > 0)
                await context.ReportProgressAsync(new { roadmapInventory = "statements without an accepted source were omitted", omitted }, token);
            inventory = (await SaveProgressiveStateAsync(inventoryKey, inventory, context, token)).Payload;
        }
        ValidateCommitments(inventory, sources.Count);
        var obligations = inventory.Commitments.Select((entry, index) => new ScopeSource($"commitment-{index}", entry.Statement)).ToArray();
        setRoadmapSourceCount(obligations.Length);
        var roadmapMessages = projectMessages.Append(new ChatMessage(ChatRole.User, "Approved commitments for this roadmap: " +
            JsonSerializer.Serialize(obligations.Select((o, sourceIndex) => new { sourceIndex, o.Text,
                kind = inventory.Commitments[sourceIndex].Kind ?? "Outcome" }), PlanningJson))).ToArray();
        var constraintSources = inventory.Commitments.Select((entry, index) => (entry, index))
            .Where(x => x.entry.Kind == "Constraint").Select(x => x.index).ToHashSet();
        var draft = await ReadPlanningPageAsync<RoadmapDraft>(generate, roadmapMessages, "Epic roadmap",
            "Group the complete approved product commitments into outcome Epics, including later content, systems, presentation and acceptance outcomes when the source actually requires them. " +
            "The roadmap spans the whole product; selecting a small first experiment must not shrink it to first-increment features. " +
            "Keep later Epics at outcome level without prewriting their Stories, Tasks or estimates. Choose the first experiment or deliverable using the game's actual uncertainty. " +
            "Plan the first playable increment with the engineer who will initialize the project, restore its dependencies and launch a minimal slice. Scope a lockfile there only when required by the chosen package manager. The ownership map is planning metadata. Add scope-document work only for an approved deliverable or concrete production need; traceability alone does not justify a documentation milestone. " +
            "For document outcomes use a reviewable platform document revision as evidence. Reserve branch and commit evidence for actual repository work. " +
            "Each Epic needs a title, outcome, testable acceptanceCriteria, sourceIndexes selecting its directly owned commitments and prerequisiteIndexes " +
            "referring to Epics in your proposed array. For each owned commitment include acceptance criteria showing how the owner will deliver or enforce it. " +
            "Assign shared constraints only to Epics with a concrete responsibility. Preserve the approved scope. " +
            "A constraint, exclusion or non-goal restricts work; it is not work. Attach it to the outcome Epics it restricts with a criterion that can be checked on their deliverable. " +
            "Every Epic must own at least one commitment marked Outcome. Do not create an Epic to hold, baseline, trace or govern constraints. " +
            "An Epic is a prerequisite only when the later Epic cannot be built or tested without its result; agreement on scope, authority or exclusions is never a prerequisite. " +
            $"Valid commitment indexes are 0..{obligations.Length - 1}. Return Epics and the three findings arrays.",
            value => BindRoadmap(value, obligations, constraintSources), MaximumPlanningPageCharacters, token, includeRejectedDraft: true);
        var roadmap = BindRoadmap(draft, obligations, constraintSources);
        var coverage = roadmap.Coverage.Select((owner, index) => owner with {
            Statement = obligations[index].Text,
            Citations = inventory.Commitments[index].SourceIndexes.Distinct().Select(i => sources[i].SourceKey).ToArray()
        }).ToArray();
        return (await SaveProgressiveStateAsync(key, roadmap with { Coverage = coverage }, context, token)).Payload;
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
