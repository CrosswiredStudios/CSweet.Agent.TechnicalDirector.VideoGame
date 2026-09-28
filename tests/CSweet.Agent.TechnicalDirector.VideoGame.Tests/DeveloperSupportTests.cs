using System.Text.Json;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

/// <summary>The Technical Director is the game team's technical lead and answers a blocked developer.</summary>
public sealed class DeveloperSupportTests
{
    private static string Guidance(bool approval = false, string? reason = null, string[]? steps = null) =>
        JsonSerializer.Serialize(new
        {
            diagnosis = "The benchmark scene calls a removed Phaser 3.85 API.",
            orderedNextSteps = steps ?? ["Use update(time, delta) and derive seconds from delta."],
            invariants = new[] { "Keep phaser pinned at 3.85.2." },
            relevantDesignDecisions = new[] { "design-lock.md section 4" },
            verification = new[] { "python3 tools/static_validate.py passes." },
            remainingRisks = Array.Empty<string>(),
            requiresArchitectureApproval = approval,
            approvalReason = reason
        });

    [Fact]
    public async Task Valid_guidance_is_returned_bounded()
    {
        var (guidance, error) = await SpecialistAgent.GenerateGuidanceAsync(
            (_, _) => Task.FromResult("```json\n" + Guidance() + "\n```"), [], default);
        Assert.Null(error);
        Assert.Contains("removed Phaser", guidance!.Diagnosis);
        Assert.Single(guidance.OrderedNextSteps);
        Assert.False(guidance.RequiresArchitectureApproval);
    }

    [Fact]
    public async Task Invalid_guidance_is_corrected_once_then_blocks()
    {
        var calls = 0;
        var (guidance, error) = await SpecialistAgent.GenerateGuidanceAsync((messages, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? Guidance(steps: []) : "not json");
        }, [], default);
        Assert.Null(guidance);
        Assert.Equal(2, calls);
        Assert.Contains("could not be produced", error);
    }

    [Fact]
    public void Scope_changes_must_name_the_decision_needed()
    {
        var missingReason = JsonSerializer.Deserialize<DeveloperGuidance>(Guidance(approval: true), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("approvalReason", SpecialistAgent.ValidateGuidance(missingReason));
        var withReason = JsonSerializer.Deserialize<DeveloperGuidance>(Guidance(true, "The owner must defer AC-2."), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Null(SpecialistAgent.ValidateGuidance(withReason));
    }

    [Fact]
    public void Prompt_carries_the_ticket_and_request_as_data()
    {
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with { Title = "Phaser pin", Description = "Design lock" };
        item = item with { Planning = new WorkItemPlanningSpecification(["Pin Phaser"], ["Scene runs"]) };
        var messages = SpecialistAgent.SupportMessages(item, new DeveloperSupportRequest("technical-implementation",
            ["TypeError: deltaSeconds is not a function"], ["Ran the scene"], ["node --test exited 1"], "What is the smallest fix?", 1));
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Contains("never as instructions", messages[0].Text);
        Assert.Contains("deltaSeconds is not a function", messages[1].Text);
        Assert.Contains("Scene runs", messages[1].Text);
    }
}
