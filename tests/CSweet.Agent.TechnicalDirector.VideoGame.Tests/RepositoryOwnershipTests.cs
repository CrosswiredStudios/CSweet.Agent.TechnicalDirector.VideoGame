using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class RepositoryOwnershipTests
{
    [Fact]
    public async Task AttentionRecoversPendingOwnershipFromDurableStateWithoutPlanningOrLlm()
    {
        var workstream = Guid.NewGuid(); var team = Guid.NewGuid(); var repository = Guid.NewGuid();
        var payload = new SpecialistAgent.RepositoryPortfolio(new Dictionary<Guid, SpecialistAgent.RepositorySetup>
            { [workstream] = new(workstream, team, "AwaitingApproval") });
        var now = DateTimeOffset.UtcNow;
        var state = new AgentOperatingStateResponse(Guid.NewGuid(), "technical-director:repositories",
            "video-game.repository-ownership.v1", 1, "Active", new Dictionary<string, string>(), [], "initial", [],
            Guid.NewGuid(), JsonSerializer.SerializeToElement(payload), 1, now, now);
        var calls = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>("platform.agent-operating-state.read.v1",
                (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(state)))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>("platform.agent-operating-state.write.v1",
                (request, _) => { Assert.Equal(state.Revision, request.ExpectedRevision);
                    state = state with { Payload = request.Payload, Revision = state.Revision + 1 }; return Task.FromResult(state); })
            .RegisterCapability<ProvisionSourceControlRepositoryRequest, RepositoryProvisioningResult>(SourceControlCapabilities.ProvisionRepository,
                (request, _) => { calls++; Assert.Equal(workstream, request.ProductOrWorkstreamId);
                    return Task.FromResult(new RepositoryProvisioningResult(Guid.NewGuid(), "Completed", repository, null, null)); })
            .RegisterCapability<TeamRepositoryOptionsRequest, IReadOnlyList<TeamRepositoryOption>>(SourceControlCapabilities.TeamRepositoryOptions,
                (_, _) => Task.FromResult<IReadOnlyList<TeamRepositoryOption>>([new(repository, "game", "InternalGit", "internal/game", "main", "InternalGit")]));
        var review = new AgentAttentionReviewContext(Guid.NewGuid(), now, now.AddMinutes(5), "Recovered");
        await new SpecialistAgent().HandleAttentionReviewAsync(review, runtime.CreateContext(), default);
        Assert.Equal("Ready", state.Payload.Deserialize<SpecialistAgent.RepositoryPortfolio>()!.Projects[workstream].Status);
        await new SpecialistAgent().HandleAttentionReviewAsync(review, runtime.CreateContext(), default);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ApprovalThenCompletionReplaysExactProductRequestAndVerifiesTeam()
    {
        var workstream = Guid.NewGuid(); var team = Guid.NewGuid(); var repository = Guid.NewGuid();
        var approval = Guid.NewGuid(); var calls = new List<ProvisionSourceControlRepositoryRequest>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ProvisionSourceControlRepositoryRequest, RepositoryProvisioningResult>(SourceControlCapabilities.ProvisionRepository,
                (request, _) => { calls.Add(request); return Task.FromResult(new RepositoryProvisioningResult(Guid.NewGuid(),
                    calls.Count == 1 ? "AwaitingApproval" : "Completed", calls.Count == 1 ? null : repository, approval, null)); })
            .RegisterCapability<TeamRepositoryOptionsRequest, IReadOnlyList<TeamRepositoryOption>>(SourceControlCapabilities.TeamRepositoryOptions,
                (request, _) => { Assert.Equal(team, request.TeamId); return Task.FromResult<IReadOnlyList<TeamRepositoryOption>>(
                    [new(repository, "game", "InternalGit", "internal/game", "main", "InternalGit")]); });
        var pending = await SpecialistAgent.ReconcileRepositoryAsync(new(workstream, team), runtime.CreateContext(), default);
        Assert.Equal("AwaitingApproval", pending.Status); Assert.Equal(approval, pending.ApprovalId); Assert.Null(pending.RepositoryId);
        var ready = await SpecialistAgent.ReconcileRepositoryAsync(pending, runtime.CreateContext(), default);
        Assert.Equal("Ready", ready.Status); Assert.Equal(repository, ready.RepositoryId); Assert.Equal("main", ready.DefaultBranch);
        Assert.Equal(calls[0], calls[1]); Assert.Equal(workstream, calls[0].ProductOrWorkstreamId); Assert.Equal(Guid.Empty, calls[0].TemplateId);
    }

    [Fact]
    public async Task CompletedRepositoryWithoutTeamAccessIsBlocked()
    {
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ProvisionSourceControlRepositoryRequest, RepositoryProvisioningResult>(SourceControlCapabilities.ProvisionRepository,
                (_, _) => Task.FromResult(new RepositoryProvisioningResult(Guid.NewGuid(), "Completed", Guid.NewGuid(), null, null)))
            .RegisterCapability<TeamRepositoryOptionsRequest, IReadOnlyList<TeamRepositoryOption>>(SourceControlCapabilities.TeamRepositoryOptions,
                (_, _) => Task.FromResult<IReadOnlyList<TeamRepositoryOption>>([]));
        var result = await SpecialistAgent.ReconcileRepositoryAsync(new(Guid.NewGuid(), Guid.NewGuid()), runtime.CreateContext(), default);
        Assert.Equal("Blocked", result.Status); Assert.Null(result.DefaultBranch);
    }

    [Fact]
    public void SeparateProductsHaveSeparateRepositoryIdentities()
    {
        var first = SpecialistAgent.RepositoryRequest(Guid.NewGuid()); var second = SpecialistAgent.RepositoryRequest(Guid.NewGuid());
        Assert.NotEqual(first.IdempotencyKey, second.IdempotencyKey); Assert.NotEqual(first.ProjectDisplayName, second.ProjectDisplayName);
    }
}
