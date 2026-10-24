using System.Text.Json;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class CompactCoverageTests
{
    private static readonly SpecialistAgent.ScopeSource[] Sources = [new("document-0:paragraph-4", "Paddle controls"), new("document-1:paragraph-8", "Colour-safe palette")];
    private static readonly CrosswiredStudios.VideoGame.Contracts.GameProposedWorkItemV1[] Stories =
        [SpecialistAgent.PlanningContractExample().DeliveryItems[1], SpecialistAgent.PlanningContractExample().DeliveryItems[1] with { ProposalKey = "palette-story", Title = "Palette" }];

    [Fact]
    public void Numeric_response_resolves_to_exact_canonical_source_and_story_identities()
    {
        var page = SpecialistAgent.BindCoverageIndexes(new([new(1, [1]), new(0, [0, 1])]), "frozen-digest", Sources, Stories);
        Assert.Equal("frozen-digest", page.ManifestDigest);
        Assert.Equal("document-1:paragraph-8", page.Coverage[0].SourceKey);
        Assert.Equal(new[] { "palette-story" }, page.Coverage[0].ProposalKeys);
        Assert.Equal(new[] { Stories[0].ProposalKey, "palette-story" }, page.Coverage[1].ProposalKeys);
        SpecialistAgent.ValidateScopeCoverage(page.Coverage, Sources, Stories.Select(x => x.ProposalKey).ToHashSet());
    }

    [Theory]
    [InlineData("{\"coverage\":[{\"source\":0,\"stories\":[0]}]}")]
    [InlineData("{\"coverage\":[{\"source\":0,\"stories\":[0]},{\"source\":0,\"stories\":[1]}]}")]
    [InlineData("{\"coverage\":[{\"source\":-1,\"stories\":[0]},{\"source\":1,\"stories\":[1]}]}")]
    [InlineData("{\"coverage\":[{\"source\":2,\"stories\":[0]},{\"source\":1,\"stories\":[1]}]}")]
    [InlineData("{\"coverage\":[{\"source\":0,\"stories\":[2]},{\"source\":1,\"stories\":[1]}]}")]
    [InlineData("{\"coverage\":[{\"source\":0,\"stories\":[]},{\"source\":1,\"stories\":[1]}]}")]
    [InlineData("{\"coverage\":[{\"source\":0,\"stories\":[0,0]},{\"source\":1,\"stories\":[1]}]}")]
    public void Missing_duplicate_or_out_of_ledger_indexes_cannot_pass_coverage(string json)
    {
        var value = JsonSerializer.Deserialize<SpecialistAgent.CoverageIndexDraft>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.BindCoverageIndexes(value, "digest", Sources, Stories));
    }

    [Fact]
    public void Compact_wire_format_preserves_dense_mappings_without_repeating_long_keys()
    {
        var stories = Enumerable.Range(0, 63).Select(i => Stories[0] with { ProposalKey = "implement-accepted-game-feature-with-long-canonical-identity-" + i }).ToArray();
        var sources = Enumerable.Range(0, 12).Select(i => new SpecialistAgent.ScopeSource("document-0:paragraph-" + i, "Requirement " + i)).ToArray();
        var draft = new SpecialistAgent.CoverageIndexDraft(sources.Select((_, i) => new SpecialistAgent.CoverageIndex(i, Enumerable.Range(0, 63).ToArray())).ToArray());
        var canonical = SpecialistAgent.BindCoverageIndexes(draft, "digest", sources, stories);
        Assert.Equal(756, canonical.Coverage.Sum(x => x.ProposalKeys.Count));
        Assert.True(JsonSerializer.Serialize(draft).Length * 10 < JsonSerializer.Serialize(canonical).Length);
        var instructions = SpecialistAgent.CoverageIndexInstructions(2, sources, stories);
        Assert.DoesNotContain(stories[0].ProposalKey, instructions);
        Assert.All(sources, source => Assert.Contains(source.Text, instructions));
        Assert.All(stories[0].AcceptanceCriteria, criterion => Assert.Contains(criterion, instructions));
    }
}
