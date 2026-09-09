using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class ProductionPlanningTests
{
    [Fact]
    public void Lean_plan_can_cover_engineering_and_qa_without_studio_specialists()
    {
        var engineer = Item("implementation", VideoGameRoleKeys.Engineer);
        var qa = Item("qa", VideoGameRoleKeys.QualityAssurance) with { DependencyProposalKeys = [engineer.ProposalKey] };
        Assert.True(SpecialistAgent.IsValidPlan([engineer, qa]));
    }
    [Fact]
    public void Rejects_cycles_unknown_roles_and_untestable_tasks()
    {
        var item = Item("implementation", VideoGameRoleKeys.Engineer);
        Assert.False(SpecialistAgent.IsValidPlan([item with { DependencyProposalKeys = [item.ProposalKey] }]));
        Assert.False(SpecialistAgent.IsValidPlan([item with { AccountableRoleKey = "invented-role" }]));
        Assert.False(SpecialistAgent.IsValidPlan([item with { AcceptanceCriteria = [] }]));
        Assert.False(SpecialistAgent.IsValidPlan([item with { DependencyProposalKeys = ["missing"] }]));
    }
    [Theory]
    [InlineData("valid", 1, true)]
    [InlineData("malformed", 2, true)]
    [InlineData("wrong-shape", 2, true)]
    [InlineData("invalid-plan", 2, true)]
    [InlineData("persistent", 2, false)]
    public async Task Corrects_bad_output_once_and_preserves_validation(string scenario, int expectedCalls, bool succeeds)
    {
        var calls = 0;
        var valid = System.Text.Json.JsonSerializer.Serialize(new SpecialistAgent.PlanningOutput(
            [Item("implementation", VideoGameRoleKeys.Engineer)], [], [], []));
        var result = await SpecialistAgent.GeneratePlanningAsync((messages, token) =>
        {
            calls++;
            Assert.Equal("Accepted scope", messages[0].Text);
            if (calls == 2)
            {
                Assert.Equal(Microsoft.Extensions.AI.ChatRole.Assistant, messages[1].Role);
                Assert.Contains("Correct your proposal:", messages[2].Text);
            }
            return Task.FromResult(scenario == "persistent" ? "{" : calls == 2 || scenario == "valid" ? valid :
                scenario == "wrong-shape" ? "{\"deliveryItems\":[],\"feasibilityFindings\":[{}]}" :
                scenario == "invalid-plan" ? "{\"deliveryItems\":[]}" : "{");
        }, [new(Microsoft.Extensions.AI.ChatRole.User, "Accepted scope")], default);
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(succeeds, result.Output is not null);
        if (!succeeds) Assert.Contains("after two attempts", result.Error);
    }

    [Fact]
    public async Task Cancellation_does_not_start_a_correction_attempt()
    {
        var calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => SpecialistAgent.GeneratePlanningAsync((messages, token) =>
        {
            calls++;
            throw new OperationCanceledException();
        }, [], default));
        Assert.Equal(1, calls);
    }
    private static GameProposedWorkItemV1 Item(string key, string role) => new(key, VideoGameWorkItemTypeKeys.Task,
        "Implement playable movement", "Deliver the accepted movement behavior", ["Input changes player position"], role,
        [role == VideoGameRoleKeys.QualityAssurance ? VideoGameSpecializationKeys.TestPlanning : VideoGameSpecializationKeys.GameplayProgramming], [], ["work.execution.run.v1"], []);
}
