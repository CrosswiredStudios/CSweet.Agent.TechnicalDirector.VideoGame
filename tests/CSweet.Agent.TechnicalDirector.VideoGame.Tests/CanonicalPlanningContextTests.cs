using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class CanonicalPlanningContextTests
{
    [Fact]
    public void Large_published_outline_keeps_every_identity_and_dependency_without_repeated_scope_bodies()
    {
        var common = "Accepted global constraint " + new string('x', 5000);
        var ids = Enumerable.Range(0, 126).Select(_ => Guid.NewGuid()).ToArray();
        var items = ids.Select((id, index) => new WorkItem(id, Guid.NewGuid(), index == 0 ? null : ids[0], null,
            "Story", $"[scope-{index}] Scope {index}", "Full retained description", index == 0 ? "Completed" : "Ready", "High", null, 0, 1, null)
        {
            TypeKey = "video-game.feature.v1", ProposalProvenance = new(Guid.NewGuid(), "digest", "scope-" + index),
            Planning = new(["Accepted requirement"], ["Exact retained criterion"], [common]) {
                DependencyItemIds = index < 2 ? [] : [ids[1]] }
        }).ToArray();
        var compact = SpecialistAgent.CanonicalPlanningContext(items, true);
        var full = SpecialistAgent.CanonicalPlanningContext(items, false);
        Assert.True(full.Length > 650000);
        Assert.True(compact.Length < 64000);
        Assert.DoesNotContain(common, compact);
        using var document = JsonDocument.Parse(compact);
        var ledger = document.RootElement.EnumerateArray().ToDictionary(x => x.GetProperty("proposalKey").GetString()!);
        Assert.Equal(126, ledger.Count);
        Assert.Equal("Completed", ledger["scope-0"].GetProperty("Status").GetString());
        Assert.Equal("scope-0", ledger["scope-125"].GetProperty("parentProposalKey").GetString());
        Assert.Equal("scope-1", ledger["scope-125"].GetProperty("dependencyProposalKeys")[0].GetString());
        Assert.All(items, item => Assert.Equal("Exact retained criterion", item.Planning!.AcceptanceCriteria[0]));
        Assert.Contains(common, full); // Nonprogressive repair/planning retains the complete context.
    }
}
