using System.Security.Cryptography;
using System.Text.Json;
using CrosswiredStudios.VideoGame.Contracts;
using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record OutlineIncrement(string EpicKey, string StoryKey, string Title, IReadOnlyList<string> DependencyEpicKeys);
    internal sealed record OutlineManifest(IReadOnlyList<OutlineIncrement> Increments, IReadOnlyList<string> FeasibilityFindings,
        IReadOnlyList<string> TechnicalConstraints, IReadOnlyList<string> OpenFeasibilityDecisions);
    internal sealed record OutlineItemPage(string ManifestDigest, IReadOnlyList<GameProposedWorkItemV1> Items);
    internal sealed record OutlineCoveragePage(string ManifestDigest, IReadOnlyList<ScopeCoverage> Coverage);
    private sealed record OutlineCompletion(string ManifestDigest, string OutlineDigest);

    internal static void ValidateOutlineManifest(OutlineManifest manifest)
    {
        if (manifest?.Increments is not { Count: > 0 } || manifest.FeasibilityFindings is null ||
            manifest.TechnicalConstraints is null || manifest.OpenFeasibilityDecisions is null)
            throw new InvalidOperationException("The scope manifest requires bounded increments and explicit findings/constraints/decisions arrays.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var increment in manifest.Increments)
        {
            if (increment is null || string.IsNullOrWhiteSpace(increment.EpicKey) || string.IsNullOrWhiteSpace(increment.StoryKey) ||
                string.IsNullOrWhiteSpace(increment.Title) || increment.DependencyEpicKeys is null ||
                increment.DependencyEpicKeys.Distinct().Count() != increment.DependencyEpicKeys.Count ||
                increment.DependencyEpicKeys.Any(key => !seen.Contains(key)) ||
                !seen.Add(increment.EpicKey) || !seen.Add(increment.StoryKey))
                throw new InvalidOperationException("Manifest identities must be unique and prerequisites must name earlier Epics.");
            // Story identities are distinct from the Epic dependency namespace.
            if (increment.DependencyEpicKeys.Any(key => !manifest.Increments.Any(x => x.EpicKey == key)))
                throw new InvalidOperationException("Manifest dependencies may name only prerequisite Epics.");
        }
    }

    internal static void ValidateOutlineItemPage(OutlineItemPage page, string digest, IReadOnlyList<OutlineIncrement> selected)
    {
        ValidateOutlinePageStructure(page, digest, selected);
        var issues = page.Items.SelectMany(PlanningItemMetadataIssues).ToArray();
        if (issues.Length > 0) throw new InvalidOperationException(string.Join("; ", issues));
    }

    private static void ValidateOutlinePageStructure(OutlineItemPage page, string digest, IReadOnlyList<OutlineIncrement> selected)
    {
        if (page?.ManifestDigest != digest || page.Items is null || page.Items.Any(x => x is null) ||
            page.Items.Count != selected.Count * 2 ||
            !page.Items.Select(x => x.ProposalKey).ToHashSet().SetEquals(selected.SelectMany(x => new[] { x.EpicKey, x.StoryKey })))
            throw new InvalidOperationException("An outline page must contain exactly the requested Epic/Story pairs and manifest digest.");
        foreach (var increment in selected)
        {
            var epic = page.Items.Single(x => x.ProposalKey == increment.EpicKey);
            var story = page.Items.Single(x => x.ProposalKey == increment.StoryKey);
            void Require(bool valid, GameProposedWorkItemV1 item, string field, string expected)
            {
                if (!valid) throw new InvalidOperationException($"Item '{item.ProposalKey}': {field} must be {expected}.");
            }
            Require(epic.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Milestone, epic, "workItemTypeKey", VideoGameWorkItemTypeKeys.Milestone);
            Require(epic.ParentProposalKey is null, epic, "parentProposalKey", "null for an Epic");
            Require(story.WorkItemTypeKey is VideoGameWorkItemTypeKeys.Feature or VideoGameWorkItemTypeKeys.Content, story, "workItemTypeKey", "video-game.feature.v1 or video-game.content.v1");
            Require(story.ParentProposalKey == epic.ProposalKey, story, "parentProposalKey", epic.ProposalKey);
            Require(epic.DependencyProposalKeys is not null && epic.DependencyProposalKeys.SequenceEqual(increment.DependencyEpicKeys), epic, "dependencyProposalKeys", "the exact frozen manifest prerequisites");
            Require(story.DependencyProposalKeys is { Count: 0 }, story, "dependencyProposalKeys", "[] for this outline phase");
            foreach (var item in new[] { epic, story })
            {
                Require(!string.IsNullOrWhiteSpace(item.Title), item, "title", "nonblank");
                Require(!string.IsNullOrWhiteSpace(item.Description), item, "description", "nonblank");
                Require(item.AcceptanceCriteria is { Count: > 0 } && !item.AcceptanceCriteria.Any(string.IsNullOrWhiteSpace), item, "acceptanceCriteria", "a nonempty array of nonblank criteria");
            }
        }
    }

    private async Task<ProgressiveScopeIndex> ReadOrGenerateScopeOutlineAsync(AgentRuntimeContext context,
        Func<IReadOnlyList<ChatMessage>, Type, CancellationToken, Task<ChatResponse>> generate,
        IReadOnlyList<ChatMessage> messages, string key, IReadOnlyList<ScopeSource> sources, bool mayGenerate, CancellationToken token)
    {
        // Existing accepted single-record outlines retain their identity and remain readable.
        var legacy = await context.Platform.ReadOperatingStateAsync<ProgressiveScopeIndex>(key, token);
        if (legacy is not null) { ValidateProgressiveOutline(legacy.Payload, sources); return legacy.Payload; }
        var manifestKey = key + ":manifest";
        var saved = await context.Platform.ReadOperatingStateAsync<OutlineManifest>(manifestKey, token);
        if (!mayGenerate && saved is null) throw new InvalidOperationException("The exact accepted scope outline is unavailable; publish it before requesting detail.");
        var manifest = saved?.Payload ?? await ReadPlanningPageAsync<OutlineManifest>(generate, messages, "complete scope manifest",
            "Return a compact complete identity/dependency manifest for ALL approved scope, including non-goals, content, polish, QA and release. " +
            "Do not write descriptions, acceptance criteria, coverage mappings, tasks or implementation briefs yet; they follow in checkpointed pages. " +
            "Each increment has epicKey, storyKey, title, dependencyEpicKeys. Each Epic owns exactly one bounded Story refinable into at most twelve tasks. " +
            "Structure example only, not project scope: {\"increments\":[{\"epicKey\":\"example-epic\",\"storyKey\":\"example-story\",\"title\":\"Playable increment\",\"dependencyEpicKeys\":[]}],\"feasibilityFindings\":[],\"technicalConstraints\":[],\"openFeasibilityDecisions\":[]}. " +
            "Order increments by actual prerequisites, starting with a small end-to-end playable prototype. Dependencies name earlier Epics only. " +
            "Use explicit feasibilityFindings, technicalConstraints and openFeasibilityDecisions arrays. Do not drop any accepted requirement. " +
            "Use the exact accepted source ledger supplied as project data.", ValidateOutlineManifest, MaximumPlanningPageCharacters, token);
        ValidateOutlineManifest(manifest);
        if (saved is null) await SaveProgressiveStateAsync(manifestKey, manifest, context, token);
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest)));
        var completed = await context.Platform.ReadOperatingStateAsync<OutlineCompletion>(key + ":outline-completed", token);
        if (!mayGenerate && completed is null) throw new InvalidOperationException("The scope outline is incomplete; resume its Outline phase before Story refinement.");
        var items = new List<GameProposedWorkItemV1>();
        var pages = manifest.Increments.Chunk(PlanningPageSize).ToArray();
        for (var number = 0; number < pages.Length; number++)
        {
            var pageKey = key + ":outline:" + number;
            var retained = await context.Platform.ReadOperatingStateAsync<OutlineItemPage>(pageKey, token);
            if (retained is null && (!mayGenerate || completed is not null)) throw new InvalidOperationException("A completed outline is missing a validated page.");
            var selected = pages[number];
            OutlineItemPage page;
            if (retained is not null)
                page = await ReadOrRepairOutlinePageAsync(context, generate, messages, pageKey, retained.Payload, digest, selected,
                    mayGenerate && completed is null, token);
            else
            {
                var draft = await ReadPlanningPageAsync<OutlineDraftPage>(generate, messages, "scope outline page " + number,
                    OutlinePageInstructions() + "\nRequested increments: " + JsonSerializer.Serialize(selected, PlanningJson),
                    value => ValidateOutlineItemPage(BindOutlinePage(value, digest, selected), digest, selected), MaximumPlanningPageCharacters, token);
                page = BindOutlinePage(draft, digest, selected);
            }
            ValidateOutlineItemPage(page, digest, selected);
            if (retained is null) await SaveProgressiveStateAsync(pageKey, page, context, token);
            items.AddRange(selected.SelectMany(x => new[] { page.Items.Single(i => i.ProposalKey == x.EpicKey), page.Items.Single(i => i.ProposalKey == x.StoryKey) }));
        }
        var coverage = new List<ScopeCoverage>();
        var sourcePages = sources.Chunk(12).ToArray();
        var storyKeys = manifest.Increments.Select(x => x.StoryKey).ToHashSet(StringComparer.Ordinal);
        for (var number = 0; number < sourcePages.Length; number++)
        {
            var pageKey = key + ":coverage:" + number;
            var retained = await context.Platform.ReadOperatingStateAsync<OutlineCoveragePage>(pageKey, token);
            if (retained is null && (!mayGenerate || completed is not null)) throw new InvalidOperationException("A completed outline is missing a coverage page.");
            var selected = sourcePages[number];
            void Validate(OutlineCoveragePage value)
            {
                if (value?.ManifestDigest != digest) throw new InvalidOperationException("The coverage page names a different scope manifest.");
                ValidateScopeCoverage(value.Coverage, selected, storyKeys);
            }
            var page = retained?.Payload;
            if (page is null)
            {
                var draft = await ReadPlanningPageAsync<OutlineCoverageDraft>(generate, messages, "scope coverage page " + number,
                "Current phase: source coverage only. Return coverage; code supplies manifestDigest, so omit it. Map EVERY requested sourceKey exactly once to one or more owning Story proposalKeys from " +
                "the frozen outline. Retain all requirements, acceptance criteria and non-goals; identify missing scope as an error, never omit it. " +
                "Do not recreate source text, change the outline or generate tasks. " + JsonSerializer.Serialize(new { sources = selected,
                    stories = items.Where(x => storyKeys.Contains(x.ProposalKey)).Select(x => new { x.ProposalKey, x.Title, x.Description, x.AcceptanceCriteria }) }, PlanningJson),
                value => Validate(new(digest, value.Coverage)), MaximumPlanningPageCharacters, token);
                page = new(digest, draft.Coverage);
            }
            Validate(page);
            if (retained is null) await SaveProgressiveStateAsync(pageKey, page, context, token);
            coverage.AddRange(page.Coverage);
        }
        var scope = new ProgressiveScopeIndex(new(items, manifest.FeasibilityFindings, manifest.TechnicalConstraints, manifest.OpenFeasibilityDecisions), coverage);
        ValidateProgressiveOutline(scope, sources);
        var outlineDigest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(scope)));
        if (completed is not null && completed.Payload != new OutlineCompletion(digest, outlineDigest))
            throw new InvalidOperationException("The completed scope outline does not match its immutable pages.");
        if (completed is null) await SaveProgressiveStateAsync(key + ":outline-completed", new OutlineCompletion(digest, outlineDigest), context, token);
        return scope;
    }
}
