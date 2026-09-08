using CSweet.Agent.SDK;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class TechnicalReviewTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DecisionRequiresConsistentFindingsAndExactCommit(bool approved)
    {
        var sha = new string('a', 40);
        var valid = new GameTechnicalDecision(sha, approved, "Review rationale", approved ? [] : ["Fix boundary handling"]);
        SpecialistAgent.ValidateTechnicalDecision(valid, sha);
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateTechnicalDecision(valid, new string('b', 40)));
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateTechnicalDecision(valid with { Summary = "" }, sha));
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.ValidateTechnicalDecision(valid with { Findings = approved ? ["Unresolved defect"] : [] }, sha));
    }
    [Fact]
    public void MergeRequiresOwnRecordedApprovalOfSamePublicationAndCommit()
    {
        var id = Guid.NewGuid(); var sha = new string('a', 40);
        var candidate = new GitMergeReview(id, Guid.NewGuid(), Guid.NewGuid(), "Game", sha, null, "patch", [], [], "AwaitingLeadAuthorization");
        var approval = new GameTechnicalReviewEvidence("technical-review", id, sha, true, "Reviewed", []);
        Assert.True(SpecialistAgent.MatchesTechnicalApproval(approval, candidate));
        Assert.False(SpecialistAgent.MatchesTechnicalApproval(null, candidate));
        Assert.False(SpecialistAgent.MatchesTechnicalApproval(approval with { Approved = false }, candidate));
        Assert.False(SpecialistAgent.MatchesTechnicalApproval(approval with { StageKey = "specialist-execution" }, candidate));
        Assert.False(SpecialistAgent.MatchesTechnicalApproval(approval with { PublicationId = Guid.NewGuid() }, candidate));
        Assert.False(SpecialistAgent.MatchesTechnicalApproval(approval with { CandidateCommitSha = new string('b', 40) }, candidate));
        Assert.False(SpecialistAgent.MatchesTechnicalApproval(approval with { Findings = ["Unresolved defect"] }, candidate));
    }
}
