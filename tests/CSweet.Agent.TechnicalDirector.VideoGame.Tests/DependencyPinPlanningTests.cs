using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class DependencyPinPlanningTests
{
    private static SpecialistAgent.PlanningOutput PhaserExample()
    {
        var plan = SpecialistAgent.PlanningContractExample();
        return plan with { DeliveryItems = plan.DeliveryItems.Select(x => x.AccountableRoleKey == VideoGameRoleKeys.Engineer &&
            x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Task ? x with { Description = x.Description + "\nImplementation uses Phaser." } : x).ToArray() };
    }
    [Theory]
    [InlineData("director")]
    [InlineData("dependency")]
    [InlineData("capability")]
    [InlineData("criteria")]
    public void Invalid_existing_prerequisite_is_rejected_before_handoff(string scenario)
    {
        var original = SpecialistAgent.BindDependencyPins(PhaserExample() with {
            TechnicalConstraints = ["Engine: Phaser"]
        });
        var pin = original.DeliveryItems.Single(x => x.ProposalKey == "dependency-pin-phaser");
        var invalid = scenario switch {
            "director" => pin with { AccountableRoleKey = VideoGameRoleKeys.TechnicalDirector },
            "dependency" => pin with { DependencyProposalKeys = ["implementation"] },
            "capability" => pin with { RequiredCapabilityKeys = [] },
            _ => pin with { AcceptanceCriteria = [] }
        };
        var error = Assert.Throws<InvalidOperationException>(() => SpecialistAgent.BindDependencyPins(original with {
            DeliveryItems = original.DeliveryItems.Select(x => x.ProposalKey == pin.ProposalKey ? invalid : x).ToArray()
        }));
        Assert.Contains("root game-engineer task", error.Message);
    }

    [Fact]
    public void Unresolved_phaser_pin_becomes_executable_integrated_prerequisite()
    {
        var original = PhaserExample();
        var output = SpecialistAgent.BindDependencyPins(original with { TechnicalConstraints = ["Phaser version will be pinned at kickoff."] });
        var pin = Assert.Single(output.DeliveryItems, x => x.ProposalKey == "dependency-pin-phaser");
        Assert.Equal(VideoGameRoleKeys.Engineer, pin.AccountableRoleKey);
        Assert.Contains("integrated", pin.Description);
        Assert.All(output.DeliveryItems.Where(x => x.AccountableRoleKey == VideoGameRoleKeys.Engineer && x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Task && x.ProposalKey != pin.ProposalKey &&
            (x.Title + " " + x.Description).Contains("Phaser", StringComparison.OrdinalIgnoreCase)), x => Assert.Contains(pin.ProposalKey, x.DependencyProposalKeys));
        Assert.Empty(SpecialistAgent.PlanValidationIssues(output.DeliveryItems));
    }

    [Fact]
    public void Existing_pin_owner_is_reused_without_a_duplicate_ticket_or_changed_criteria()
    {
        var original = PhaserExample();
        var engineer = original.DeliveryItems.First(x => x.AccountableRoleKey == VideoGameRoleKeys.Engineer && x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Task);
        var pin = engineer with { ProposalKey = "select-commit-phaser-2d-dependency-lockfile",
            Title = "Select and commit exact Phaser 2D dependency and lockfile", DependencyProposalKeys = [],
            AcceptanceCriteria = ["A committed entry selects one exact Phaser 2D version.", "A lockfile records the same exact version."] };
        var consumer = engineer with { ProposalKey = "minimal-scene", Title = "Load a minimal Phaser scene", DependencyProposalKeys = [pin.ProposalKey] };
        var independent = engineer with { ProposalKey = "asset-rights", Title = "Record asset rights", DependencyProposalKeys = [] };
        var input = original with { TechnicalConstraints = ["Engine: Phaser"],
            DeliveryItems = [.. original.DeliveryItems.Where(x => x.ProposalKey != engineer.ProposalKey), pin, consumer, independent] };
        var result = SpecialistAgent.BindDependencyPins(input);
        Assert.Equal(input.DeliveryItems, result.DeliveryItems);
        Assert.DoesNotContain(result.DeliveryItems, x => x.ProposalKey == "dependency-pin-phaser");
        Assert.Equal(pin.AcceptanceCriteria, result.DeliveryItems.Single(x => x.ProposalKey == pin.ProposalKey).AcceptanceCriteria);
        var proposal = new GameTechnicalDeliveryProposalV1(null!, result.DeliveryItems, [], [], [], "accepted-digest");
        Assert.Equal(pin.ProposalKey, Assert.Single(SpecialistAgent.PlanningProposalPayload(proposal)["engineeringPrerequisiteKeys"]!.AsArray())!.GetValue<string>());
        Assert.Equal(result.DeliveryItems, SpecialistAgent.BindDependencyPins(result).DeliveryItems);
    }

    [Fact]
    public void Ambiguous_duplicate_pin_work_requires_correction_instead_of_silent_merging()
    {
        var original = SpecialistAgent.BindDependencyPins(PhaserExample() with { TechnicalConstraints = ["Phaser"] });
        var pin = original.DeliveryItems.Single(x => x.ProposalKey == "dependency-pin-phaser");
        var duplicate = pin with { ProposalKey = "another-pin" };
        Assert.Contains("overlap", Assert.Throws<InvalidOperationException>(() => SpecialistAgent.BindDependencyPins(original with {
            DeliveryItems = [.. original.DeliveryItems, duplicate]
        })).Message);
        Assert.Equal(pin.AcceptanceCriteria, duplicate.AcceptanceCriteria);
    }

    [Fact]
    public void Synthesized_pin_does_not_serialize_unrelated_engineering_work()
    {
        var original = PhaserExample();
        var engineer = original.DeliveryItems.First(x => x.AccountableRoleKey == VideoGameRoleKeys.Engineer && x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Task);
        var result = SpecialistAgent.BindDependencyPins(original with { TechnicalConstraints = ["Phaser"], DeliveryItems =
            [.. original.DeliveryItems.Where(x => x.ProposalKey != engineer.ProposalKey),
                engineer with { Description = "Implement the Phaser scene." },
                engineer with { ProposalKey = "asset-rights", Title = "Record rights", Description = "Document supplied asset rights.", DependencyProposalKeys = [] }] });
        Assert.Contains("dependency-pin-phaser", result.DeliveryItems.Single(x => x.ProposalKey == engineer.ProposalKey).DependencyProposalKeys);
        Assert.Empty(result.DeliveryItems.Single(x => x.ProposalKey == "asset-rights").DependencyProposalKeys);
    }

    [Fact]
    public void Exact_pin_is_carried_into_engineering_brief_and_ranges_are_rejected()
    {
        var original = PhaserExample();
        var pinned = original with { DependencyPins = [new("phaser", "3.80.1")] };
        var output = SpecialistAgent.BindDependencyPins(pinned);
        Assert.Equal(output.DeliveryItems, SpecialistAgent.BindDependencyPins(output).DeliveryItems);
        Assert.Contains(output.DeliveryItems, x => x.Description.Contains("dependency-pins.v1") && x.Description.Contains("3.80.1"));
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.BindDependencyPins(original with { DependencyPins = [new("phaser", "^3.80.1")] }));
    }

    [Fact]
    public void Global_engine_constraint_does_not_inject_a_pin_into_an_unrelated_increment()
    {
        var original = SpecialistAgent.PlanningContractExample() with { TechnicalConstraints = ["Future engine: Phaser"] };
        Assert.Equal(original.DeliveryItems, SpecialistAgent.BindDependencyPins(original).DeliveryItems);
    }

    [Fact]
    public void Handoff_declares_the_integrated_prerequisite_without_answering_or_removing_authority_questions()
    {
        var original = PhaserExample() with {
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
