using CSweet.Agent.SDK.VideoGame.Contracts;
namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public class AdaptiveRoadmapTests
{
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
