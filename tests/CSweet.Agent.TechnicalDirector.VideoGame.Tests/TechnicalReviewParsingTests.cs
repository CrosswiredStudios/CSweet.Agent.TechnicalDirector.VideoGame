using System.Text.Json;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class TechnicalReviewParsingTests
{
    private static readonly string Sha = new('a', 40);
    private static string Decision(bool approved = false) => JsonSerializer.Serialize(new
    {
        candidateCommitSha = Sha, approved, summary = "Review rationale",
        findings = approved ? Array.Empty<string>() : new[] { "Fix the JSON import; retain literal \\n in the example." }
    });

    [Theory]
    [InlineData("")]
    [InlineData("\n\r\t")]
    [InlineData("\\n")]
    [InlineData("\\r\\n \\t\n")]
    public void Escaped_suffix_preserves_rejection_and_findings(string suffix)
    {
        var decision = SpecialistAgent.ParseTechnicalDecision("\n" + Decision() + suffix, Sha);
        Assert.False(decision.Approved);
        Assert.Equal("Fix the JSON import; retain literal \\n in the example.", Assert.Single(decision.Findings));
        Assert.Equal(Sha, decision.CandidateCommitSha);
    }

    [Theory]
    [InlineData(" trailing prose")]
    [InlineData("{}")]
    [InlineData("\\n APPROVED")]
    [InlineData("\\x")]
    public void Invalid_suffix_is_a_specific_blocker_not_a_decision(string suffix)
    {
        var error = Assert.ThrowsAny<InvalidOperationException>(() => SpecialistAgent.ParseTechnicalDecision(Decision() + suffix, Sha));
        Assert.Contains("invalid decision JSON", error.Message);
    }

    [Fact]
    public void Normalization_does_not_weaken_commit_or_approval_checks()
    {
        Assert.ThrowsAny<InvalidOperationException>(() => SpecialistAgent.ParseTechnicalDecision(Decision() + "\\n", new string('b', 40)));
        Assert.ThrowsAny<InvalidOperationException>(() => SpecialistAgent.ParseTechnicalDecision(Decision().Replace("\"approved\":false", "\"approved\":true") + "\\n", Sha));
        Assert.True(SpecialistAgent.ParseTechnicalDecision(Decision(true) + "\\n", Sha).Approved);
        Assert.ThrowsAny<InvalidOperationException>(() => SpecialistAgent.ParseTechnicalDecision("```json\n" + Decision() + "\n```", Sha));
        Assert.ThrowsAny<InvalidOperationException>(() => SpecialistAgent.ParseTechnicalDecision("{bad json}\\n", Sha));
    }
}
