using System.Text.Json;
using CSweet.Agent.SDK.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class LeanReviewPlanningTests
{
    [Fact]
    public async Task Routine_code_increment_is_accepted_without_a_duplicate_qa_leaf_or_correction_round_trip()
    {
        var example = SpecialistAgent.PlanningContractExample();
        var plan = example with { DeliveryItems = example.DeliveryItems.Where(x => x.AccountableRoleKey != VideoGameRoleKeys.QualityAssurance).ToArray() };
        Assert.Empty(SpecialistAgent.PlanValidationIssues(plan.DeliveryItems));
        var calls = 0;
        var result = await SpecialistAgent.GeneratePlanningAsync((messages, _) =>
        {
            calls++;
            Assert.Contains("Routine technical review and QA are stages", messages.Last().Text);
            return Task.FromResult(JsonSerializer.Serialize(plan));
        }, [], default);
        Assert.NotNull(result.Output);
        Assert.Equal(1, calls);
        Assert.Equal(plan.DeliveryItems.Count, result.Output.DeliveryItems.Count);
        Assert.Contains("immutable candidate commit", SpecialistAgent.PlanningAuthorityPolicy);
    }

    [Fact]
    public void A_distinct_participant_validation_deliverable_is_retained()
    {
        var plan = SpecialistAgent.PlanningContractExample();
        Assert.Empty(SpecialistAgent.PlanValidationIssues(plan.DeliveryItems));
        Assert.Contains("participant", plan.DeliveryItems.Single(x => x.AccountableRoleKey == VideoGameRoleKeys.QualityAssurance).Description);
    }
}
