using CSweet.Agent.SDK.VideoGame.Contracts;
namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public class AdaptiveRoadmapTests
{
    [Fact]
    public void Prism_first_increment_reply_cannot_cover_a_complete_approved_product_ledger()
    {
        // Actual 2026-11-15 delivered outline, minimized to one criterion per Epic.
        // Stable proposal/source bindings are normalized back to the authored index contract.
        var draft = System.Text.Json.JsonSerializer.Deserialize<SpecialistAgent.RoadmapDraft>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "prism-break-first-increment-roadmap.json")),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        var increment = Enumerable.Range(0, 6).Select(i => new SpecialistAgent.ScopeSource($"commitment-{i}", "Accepted first-increment outcome")).ToArray();
        Assert.Equal(5, SpecialistAgent.BindRoadmap(draft, increment).Plan.DeliveryItems.Count);
        var completeProduct = increment.Append(new SpecialistAgent.ScopeSource("later-content", "Deliver ten designed levels and the approved power-ups.")).ToArray();
        Assert.Contains("Missing sourceIndexes include 6", Assert.Throws<InvalidOperationException>(() =>
            SpecialistAgent.BindRoadmap(draft, completeProduct)).Message);
        // This is ledger validation, not a claim that code can judge semantic completeness.
        // The full-source inventory prompt and Producer ownership review supply that judgment.
    }

    [Fact]
    public void Dependencies_follow_the_graph_regardless_of_presentation_order()
    {
        SpecialistAgent.ScopeSource[] sources = [new("s", "Accepted scope")];
        var dependent = new SpecialistAgent.RoadmapEpic("Scoring", "Score a hit", ["Score increases"], [0], [1]);
        var prerequisite = new SpecialistAgent.RoadmapEpic("Playable loop", "Exercise a hit", ["Brick breaks"], [0], []);
        var draft = new SpecialistAgent.RoadmapDraft([dependent, prerequisite], [], [], []);
        var items = SpecialistAgent.BindRoadmap(draft, sources).Plan.DeliveryItems;
        Assert.Equal(items[1].ProposalKey, Assert.Single(items[0].DependencyProposalKeys));
        var cycle = draft with { Epics = [dependent, prerequisite with { PrerequisiteIndexes = [0] }] };
        Assert.Contains("cycle", Assert.Throws<InvalidOperationException>(() => SpecialistAgent.BindRoadmap(cycle, sources)).Message);
    }

    [Fact]
    public void Roadmap_owns_all_sources_before_approaching_epic_acquires_stories()
    {
        SpecialistAgent.ScopeSource[] sources = [new("brief/0","Paddle rebounds the ball"),new("brief/1","Each broken brick scores once")];
        var epic = new SpecialistAgent.RoadmapEpic("Playable loop","Prove ball interaction",["Ball rebounds", "Brick breaks"],[0],[]);
        var scoring = new SpecialistAgent.RoadmapEpic("Scoring","Score each hit",["Score increases once"],[1],[0]);
        var road = SpecialistAgent.BindRoadmap(new([epic, scoring], [], [], []), sources);
        Assert.All(road.Plan.DeliveryItems, e => Assert.Equal(VideoGameWorkItemTypeKeys.Milestone, e.WorkItemTypeKey));
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.BindRoadmap(new([epic],[],[],[]), sources));
        var stories = SpecialistAgent.BindSlices(road.Plan.DeliveryItems[0], new([
            new("Rebound", "Bounce the ball", [], [0], []), new("Brick hit", "Remove the brick", [], [1], [0])]));
        Assert.Equal(2,stories.Count);
        Assert.Equal(stories[0].ParentProposalKey,stories[1].ParentProposalKey);
        Assert.Contains(stories[0].ProposalKey,stories[1].DependencyProposalKeys);
        Assert.Equal(["Brick breaks"],stories[1].AcceptanceCriteria);
    }
}
