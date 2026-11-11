using System.Text.Json;
using System.Text.Json.Nodes;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

/// <summary>
/// Super Awesome Games (2026-11-10): the browser rule lived only as constraint prose, so the engineer, the reviewer and
/// QA each decided from different text whether a ticket needed browser evidence. Planning now declares it per task.
/// </summary>
public sealed class BrowserVerificationPlanningTests
{
    private static GameProposedWorkItemV1 Proposal(string key, string? verification) =>
        new(key, VideoGameWorkItemTypeKeys.Task, "Task " + key, "Do it.", ["It works."], VideoGameRoleKeys.Engineer,
            [], ["gameplay-programming"], ["work.execution.run"], []) { ParentProposalKey = "story", BrowserVerification = verification };

    [Fact]
    public void Every_fresh_task_must_declare_a_known_choice()
    {
        var issues = SpecialistAgent.BrowserVerificationIssues(
            [Proposal("a", GameBrowserVerification.Canvas), Proposal("b", GameBrowserVerification.Page), Proposal("c", GameBrowserVerification.None),
             Proposal("d", null), Proposal("e", "browser")]);

        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, issue => issue.StartsWith("d:"));
        Assert.Contains(issues, issue => issue.StartsWith("e:"));
    }

    [Fact]
    public void The_task_schema_offers_exactly_the_three_choices()
    {
        var schema = JsonNode.Parse(SpecialistAgent.PlanningResponseSchema(typeof(SpecialistAgent.ProgressiveStoryIndex)).GetRawText())!;
        var choices = schema["properties"]!["items"]!["items"]!["properties"]!["browserVerification"]!["enum"]!.AsArray()
            .Select(x => x!.GetValue<string>()).Order().ToArray();

        Assert.Equal(GameBrowserVerification.All.Order().ToArray(), choices);
    }

    [Fact]
    public void An_undeclared_task_serializes_exactly_as_before_so_retained_digests_do_not_change()
    {
        var legacy = Proposal("a", null);
        var json = JsonSerializer.Serialize(legacy);

        Assert.DoesNotContain("BrowserVerification", json);
        Assert.Contains("\"BrowserVerification\":\"canvas\"", JsonSerializer.Serialize(legacy with { BrowserVerification = GameBrowserVerification.Canvas }));
        Assert.Null(JsonSerializer.Deserialize<GameProposedWorkItemV1>(json)!.BrowserVerification);
    }

    [Fact]
    public void The_policy_asks_for_the_field_instead_of_constraint_prose()
    {
        Assert.Contains("Declare browserVerification on every executable task", SpecialistAgent.PlanningAuthorityPolicy);
        Assert.DoesNotContain("check_browser_build", SpecialistAgent.PlanningAuthorityPolicy);
    }
}
