using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class ProductionPlanningTests
{
    [Fact]
    public void Lean_plan_can_cover_engineering_and_qa_without_studio_specialists()
    {
        Assert.True(SpecialistAgent.IsValidPlan(Hierarchy()));
    }
    [Theory]
    [InlineData(VideoGameRoleKeys.Engineer)]
    [InlineData(VideoGameRoleKeys.QualityAssurance)]
    public void DomainSkillsArePreferredForCoreSoftwareRoles(string role)
    {
        var item = Item("work", role) with
        {
            RequiredSpecializationKeys = [VideoGameSpecializationKeys.Development],
            PreferredSpecializationKeys = [VideoGameSpecializationKeys.Gameplay]
        };

        var normalized = SpecialistAgent.NormalizeCoreRoleSkills(item);

        Assert.Empty(normalized.RequiredSpecializationKeys);
        Assert.Equal([VideoGameSpecializationKeys.Development, VideoGameSpecializationKeys.Gameplay],
            normalized.PreferredSpecializationKeys);
    }

    [Fact]
    public void Rejects_cycles_unknown_roles_and_untestable_tasks()
    {
        var plan = Hierarchy();
        Assert.False(SpecialistAgent.IsValidPlan([.. plan.Take(2), plan[2] with { DependencyProposalKeys = [plan[2].ProposalKey] }, plan[3]]));
        Assert.False(SpecialistAgent.IsValidPlan([.. plan.Take(2), plan[2] with { AccountableRoleKey = "invented-role" }, plan[3]]));
        Assert.False(SpecialistAgent.IsValidPlan([.. plan.Take(2), plan[2] with { AcceptanceCriteria = [] }, plan[3]]));
        Assert.False(SpecialistAgent.IsValidPlan([.. plan.Take(2), plan[2] with { DependencyProposalKeys = ["missing"] }, plan[3]]));
    }
    [Fact]
    public void Requires_epic_story_and_separate_testable_engineering_and_qa_tasks()
    {
        var plan = Hierarchy();
        Assert.False(SpecialistAgent.IsValidPlan(plan.Skip(1).ToArray()));
        Assert.False(SpecialistAgent.IsValidPlan([plan[0], .. plan.Skip(2)]));
        Assert.False(SpecialistAgent.IsValidPlan(plan.Take(3).ToArray()));
        Assert.False(SpecialistAgent.IsValidPlan([.. plan.Take(2), plan[2] with { ParentProposalKey = plan[0].ProposalKey }, plan[3]]));
        Assert.False(SpecialistAgent.IsValidPlan([plan[0], plan[1] with { ParentProposalKey = null }, plan[2], plan[3]]));
    }
    [Theory]
    [InlineData("valid", 1, true)]
    [InlineData("malformed", 2, true)]
    [InlineData("wrong-shape", 2, true)]
    [InlineData("invalid-plan", 2, true)]
    [InlineData("compact-pass", 3, true)]
    [InlineData("persistent", 3, false)]
    public async Task Corrects_bad_output_with_bounded_compact_fallback_and_preserves_validation(string scenario, int expectedCalls, bool succeeds)
    {
        var calls = 0;
        var valid = System.Text.Json.JsonSerializer.Serialize(new SpecialistAgent.PlanningOutput(
            Hierarchy(), [], [], []));
        var result = await SpecialistAgent.GeneratePlanningAsync((messages, token) =>
        {
            calls++;
            Assert.Equal("Accepted scope", messages[0].Text);
            if (calls == 2)
            {
                Assert.Equal(Microsoft.Extensions.AI.ChatRole.Assistant, messages[1].Role);
                Assert.Contains("Correct your proposal:", messages[2].Text);
            }
            if (calls == 3)
            {
                Assert.Equal(2, messages.Count);
                Assert.Contains("at most 20 concise deliveryItems", messages[1].Text);
            }
            return Task.FromResult(scenario == "persistent" || scenario == "compact-pass" && calls < 3 ? "{" :
                calls >= 2 || scenario == "valid" ? valid :
                scenario == "wrong-shape" ? "{\"deliveryItems\":[],\"feasibilityFindings\":[{}]}" :
                scenario == "invalid-plan" ? "{\"deliveryItems\":[]}" : "{");
        }, [new(Microsoft.Extensions.AI.ChatRole.User, "Accepted scope")], default);
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(succeeds, result.Output is not null);
        if (!succeeds) Assert.Contains("after three attempts", result.Error);
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
    private static GameProposedWorkItemV1[] Hierarchy()
    {
        var epic = Item("epic", VideoGameRoleKeys.TechnicalDirector) with
        { WorkItemTypeKey = VideoGameWorkItemTypeKeys.Milestone };
        var story = Item("story", VideoGameRoleKeys.Engineer) with
        { WorkItemTypeKey = VideoGameWorkItemTypeKeys.Feature, ParentProposalKey = epic.ProposalKey };
        var engineer = Item("implementation", VideoGameRoleKeys.Engineer) with
        { ParentProposalKey = story.ProposalKey };
        var qa = Item("qa", VideoGameRoleKeys.QualityAssurance) with
        { ParentProposalKey = story.ProposalKey, DependencyProposalKeys = [engineer.ProposalKey] };
        return [epic, story, engineer, qa];
    }
    private static GameProposedWorkItemV1 Item(string key, string role) => new(key, VideoGameWorkItemTypeKeys.Task,
        "Implement playable movement", "Deliver the accepted movement behavior", ["Input changes player position"], role,
        [role == VideoGameRoleKeys.QualityAssurance ? VideoGameSpecializationKeys.TestPlanning : VideoGameSpecializationKeys.GameplayProgramming], [], ["work.execution.run.v1"], []);
}
