using CSweet.Agent.SDK.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class DependencyPinPlanningTests
{
    [Fact]
    public void Retained_Prism_Break_index_keeps_the_prior_scope_dependency_and_its_consumers()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "prism-break-pin-index.json");
        var index = System.Text.Json.JsonSerializer.Deserialize<SpecialistAgent.ProgressiveStoryIndex>(File.ReadAllText(path))!;
        var plan = SpecialistAgent.PlanningContractExample();
        var template = plan.DeliveryItems.First(x => x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Feature);
        var pin = index.Items[0];
        var story = template with { ProposalKey = pin.ParentProposalKey! };
        var priorScope = pin with { ProposalKey = pin.DependencyProposalKeys.Single(), ParentProposalKey = template.ProposalKey,
            Title = "Publish the scope outline", AccountableRoleKey = VideoGameRoleKeys.TechnicalDirector, DependencyProposalKeys = [] };
        var outline = plan with { DeliveryItems = [.. plan.DeliveryItems.Where(x => x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Milestone), template, story] };
        var bound = SpecialistAgent.BindStoryDependencyPins(index, story, outline, [priorScope]);
        Assert.Equal(index.Items.Count, bound.Items.Count);
        Assert.Equal(pin, bound.Items[0]);
        Assert.Equal(index.Items.Skip(1), bound.Items.Skip(1));
        var items = outline.DeliveryItems.Concat([priorScope]).Concat(bound.Items).ToArray();
        Assert.Empty(SpecialistAgent.PlanValidationIssues(items, partialIncrement: true));
        var cyclic = items.Select(x => x.ProposalKey == pin.ProposalKey
            ? x with { DependencyProposalKeys = [bound.Items[1].ProposalKey] } : x).ToArray();
        Assert.Contains(SpecialistAgent.PlanValidationIssues(cyclic, partialIncrement: true), x => x.Contains("cycle"));
        var proposal = new GameTechnicalDeliveryProposalV1(null!, items, [], [], [], "approved");
        Assert.Empty(SpecialistAgent.PlanningProposalPayload(proposal)["engineeringPrerequisiteKeys"]!.AsArray());
    }

    [Fact]
    public void Exact_pin_is_carried_into_engineering_brief_and_ranges_are_rejected()
    {
        var original = SpecialistAgent.PlanningContractExample();
        var pinned = original with { DependencyPins = [new("phaser", "3.80.1")] };
        var output = SpecialistAgent.BindDependencyPins(pinned);
        Assert.Equal(output.DeliveryItems, SpecialistAgent.BindDependencyPins(output).DeliveryItems);
        Assert.Contains(output.DeliveryItems, x => x.Description.Contains("dependency-pins.v1") && x.Description.Contains("3.80.1"));
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.BindDependencyPins(original with { DependencyPins = [new("phaser", "^3.80.1")] }));
    }

    [Theory]
    [InlineData("Phaser")]
    [InlineData("Example Toolkit")]
    public void An_engine_in_the_brief_does_not_change_the_authored_tasks_or_dependencies(string name)
    {
        var plan = SpecialistAgent.PlanningContractExample();
        var items = plan.DeliveryItems.Select(x => x with { Description = x.Description + " Use " + name }).ToArray();
        var bound = SpecialistAgent.BindDependencyPins(plan with { DeliveryItems = items, TechnicalConstraints = [name] });
        Assert.Equal(items, bound.DeliveryItems);
    }
}
