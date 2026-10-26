using System.Text.Json;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record CoverageIndex(int Source, IReadOnlyList<int> Stories);
    internal sealed record CoverageIndexDraft(IReadOnlyList<CoverageIndex> Coverage);

    internal static OutlineCoveragePage BindCoverageIndexes(CoverageIndexDraft draft, string digest,
        IReadOnlyList<ScopeSource> sources, IReadOnlyList<GameProposedWorkItemV1> stories)
    {
        if (draft?.Coverage is null || draft.Coverage.Count != sources.Count ||
            draft.Coverage.Any(x => x is null || x.Source < 0 || x.Source >= sources.Count ||
                x.Stories is not { Count: > 0 } || x.Stories.Any(index => index < 0 || index >= stories.Count) ||
                x.Stories.Distinct().Count() != x.Stories.Count) ||
            draft.Coverage.Select(x => x.Source).Distinct().Count() != sources.Count)
            throw new InvalidOperationException("Map every requested source index once to nonempty, unique story indexes from this page's ledger.");
        var page = new OutlineCoveragePage(digest, draft.Coverage.Select(x => new ScopeCoverage(
            sources[x.Source].SourceKey, x.Stories.Select(index => stories[index].ProposalKey).ToArray())).ToArray());
        ValidateScopeCoverage(page.Coverage, sources, stories.Select(x => x.ProposalKey).ToHashSet(StringComparer.Ordinal));
        return page;
    }

    internal static string CoverageIndexInstructions(int number, IReadOnlyList<ScopeSource> sources,
        IReadOnlyList<GameProposedWorkItemV1> stories) =>
        $"Current phase: source coverage page {number} only. Return exactly {sources.Count} coverage entries. " +
        "Each entry is {source: integer, stories: integer array}, using only indexes in the supplied ledgers. " +
        "Map each source index exactly once. Return no source text, canonical keys, manifest digest or tasks. " +
        "Use the smallest sufficient set of direct owning Stories that preserves every requirement in that source. " +
        "Do not add transitive dependencies or every downstream verification Story. Context headings and document metadata " +
        "map to the closest relevant owner; they do not require listing the whole project. A source with multiple distinct " +
        "requirements may name every necessary owner. Retain all accepted requirements, criteria and non-goals. " +
        "Sources on other pages remain required and are mapped separately. The frozen outline cannot be changed. " +
        "Identify missing scope as an error rather than omit it. " + JsonSerializer.Serialize(new {
            sources = sources.Select((source, index) => new { index, source.Text }),
            stories = stories.Select((story, index) => new { index, story.Title, story.Description, story.AcceptanceCriteria })
        }, PlanningJson);
}
