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
    private static GameProposedWorkItemV1 Item(string key, string role) => new(key, VideoGameWorkItemTypeKeys.Task,
        "Implement playable movement", "Deliver the accepted movement behavior", ["Input changes player position"], role,
        [role == VideoGameRoleKeys.QualityAssurance ? VideoGameSpecializationKeys.TestPlanning : VideoGameSpecializationKeys.GameplayProgramming], [], ["work.execution.run.v1"], []);
}
