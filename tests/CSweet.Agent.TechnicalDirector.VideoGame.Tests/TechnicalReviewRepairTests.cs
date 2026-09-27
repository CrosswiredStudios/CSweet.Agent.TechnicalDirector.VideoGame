using System.Text.Json;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class TechnicalReviewRepairTests
{
    private static readonly string Sha = new('a', 40);
    private static string Decision(bool approved = false) => JsonSerializer.Serialize(new
    { candidateCommitSha = Sha, approved, summary = "Review result", findings = approved ? Array.Empty<string>() : new[] { "Fix the import." } });

    [Fact]
    public async Task Extra_brace_gets_one_format_retry_preserving_the_rejection()
    {
        var calls = 0;
        var result = await SpecialistAgent.GenerateTechnicalDecisionAsync((messages, _) =>
        {
            calls++;
            if(calls == 2) { Assert.Equal(3, messages.Count); Assert.Contains("Correct only", messages.Last().Text); }
            return Task.FromResult(Decision() + (calls == 1 ? "}" : ""));
        }, [new(ChatRole.User, "Original exact candidate")], Sha, default);
        Assert.False(result.Approved); Assert.Single(result.Findings); Assert.Equal(2,calls);
    }

    [Fact]
    public async Task Repair_cannot_turn_a_rejection_into_approval()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SpecialistAgent.GenerateTechnicalDecisionAsync((_, _) =>
            Task.FromResult(++calls == 1 ? Decision() + "}" : Decision(true)), [], Sha, default));
        Assert.Equal(2,calls);
    }

    [Fact]
    public async Task Repeated_malformed_json_blocks_after_two_calls()
    {
        var calls = 0;
        await Assert.ThrowsAsync<TechnicalReviewFormatException>(() => SpecialistAgent.GenerateTechnicalDecisionAsync((_, _) =>
        { calls++; return Task.FromResult("{bad JSON}"); }, [], Sha, default));
        Assert.Equal(2,calls);
    }

    [Fact]
    public async Task Valid_wrong_commit_is_not_repaired()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SpecialistAgent.GenerateTechnicalDecisionAsync((_, _) =>
        { calls++; return Task.FromResult(Decision()); }, [], new string('b',40), default));
        Assert.Equal(1,calls);
    }
}
