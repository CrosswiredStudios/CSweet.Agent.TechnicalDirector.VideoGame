using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class DependencyPinPlanningTests
{
    [Fact]
    public void Unresolved_phaser_pin_becomes_executable_integrated_prerequisite()
    {
        var original = SpecialistAgent.PlanningContractExample();
        var output = SpecialistAgent.BindDependencyPins(original with { TechnicalConstraints = ["Phaser version will be pinned at kickoff."] });
        var pin = Assert.Single(output.DeliveryItems, x => x.ProposalKey == "dependency-pin-phaser");
        Assert.Equal(VideoGameRoleKeys.Engineer, pin.AccountableRoleKey);
        Assert.Contains("integrated", pin.Description);
        Assert.All(output.DeliveryItems.Where(x => x.AccountableRoleKey == VideoGameRoleKeys.Engineer && x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Task && x.ProposalKey != pin.ProposalKey),
            x => Assert.Contains(pin.ProposalKey, x.DependencyProposalKeys));
        Assert.Empty(SpecialistAgent.PlanValidationIssues(output.DeliveryItems));
    }

    [Fact]
    public void Exact_pin_is_carried_into_engineering_brief_and_ranges_are_rejected()
    {
        var original = SpecialistAgent.PlanningContractExample();
        var pinned = original with { DependencyPins = [new("phaser", "3.80.1")] };
        var output = SpecialistAgent.BindDependencyPins(pinned);
        Assert.Contains(output.DeliveryItems, x => x.Description.Contains("dependency-pins.v1") && x.Description.Contains("3.80.1"));
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.BindDependencyPins(original with { DependencyPins = [new("phaser", "^3.80.1")] }));
    }

    [Fact]
    public void Handoff_declares_the_integrated_prerequisite_without_answering_or_removing_authority_questions()
    {
        var original = SpecialistAgent.PlanningContractExample() with {
            TechnicalConstraints = ["Engine: Phaser (2D)"],
            OpenFeasibilityDecisions = ["Specific Phaser version selection for optimal particle system performance"]
        };
        var bound = SpecialistAgent.BindDependencyPins(original);
        var proposal = new GameTechnicalDeliveryProposalV1(null!, bound.DeliveryItems, bound.FeasibilityFindings!, bound.TechnicalConstraints!, bound.OpenFeasibilityDecisions!, "unchanged-proposal");
        var payload = SpecialistAgent.PlanningProposalPayload(proposal);
        Assert.Equal("dependency-pin-phaser", Assert.Single(payload["engineeringPrerequisiteKeys"]!.AsArray())!.GetValue<string>());
        Assert.Equal(original.OpenFeasibilityDecisions.Single(), Assert.Single(payload["OpenFeasibilityDecisions"]!.AsArray())!.GetValue<string>());
        Assert.Equal("unchanged-proposal", payload["ProposalDigest"]!.GetValue<string>());
    }
}
