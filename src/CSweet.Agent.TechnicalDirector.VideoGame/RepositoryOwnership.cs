using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    private const string RepositoryStateKey = "technical-director:repositories";
    internal sealed record RepositorySetup(Guid WorkstreamId, Guid TeamId, string Status = "Pending",
        Guid? RepositoryId = null, Guid? ApprovalId = null, string? Remediation = null, string? DefaultBranch = null);
    internal sealed record RepositoryPortfolio(IReadOnlyDictionary<Guid, RepositorySetup> Projects);

    internal static ProvisionSourceControlRepositoryRequest RepositoryRequest(Guid workstreamId) => new(
        workstreamId, $"game-{workstreamId:N}", "Game source repository managed by the Technical Director.",
        Guid.Empty, $"game-repository:{workstreamId:N}");

    internal static async Task<RepositorySetup> ReconcileRepositoryAsync(
        RepositorySetup setup, AgentRuntimeContext context, CancellationToken token)
    {
        try
        {
            // Replay the same immutable request to recover current status, including after restart.
            var result = await context.Platform.SourceControl.ProvisionRepositoryAsync(RepositoryRequest(setup.WorkstreamId), token);
            setup = setup with { Status = result.Status, RepositoryId = result.RepositoryId,
                ApprovalId = result.ApprovalId, Remediation = result.Remediation, DefaultBranch = null };
            if (result.Status == "Completed" && result.RepositoryId is Guid repositoryId)
            {
                var options = await context.Platform.SourceControl.ListTeamRepositoryOptionsAsync(new(setup.TeamId), token);
                var repository = options.SingleOrDefault(x => x.RepositoryId == repositoryId);
                setup = repository is null
                    ? setup with { Status = "Blocked", Remediation = "Provisioned repository is not accessible to the planning team." }
                    : setup with { Status = "Ready", DefaultBranch = repository.DefaultBranch };
            }
        }
        catch (PlatformCapabilityException exception)
        {
            setup = setup with { Status = "Blocked", Remediation = $"Repository setup requires attention: {exception.Code}." };
        }
        return setup;
    }

    private static Task<AgentOperatingState<RepositoryPortfolio>> SaveRepositoryAsync(
        RepositorySetup setup, AgentRuntimeContext context, CancellationToken token, bool registerOnly = false) =>
        new RevisionSafeProjectState(context.Platform).MergeAsync<RepositoryPortfolio>(RepositoryStateKey,
            "video-game.repository-ownership.v1", 1, current =>
            {
                var projects = current?.Projects.ToDictionary(x => x.Key, x => x.Value) ?? [];
                if (!registerOnly || !projects.ContainsKey(setup.WorkstreamId)) projects[setup.WorkstreamId] = setup;
                return new(projects);
            }, new Dictionary<string, string>(), $"repository-state:{Guid.NewGuid():N}", token);

    private static async Task<RepositorySetup> EnsureRepositoryAsync(Guid workstreamId, Guid teamId,
        AgentRuntimeContext context, CancellationToken token)
    {
        var state = await SaveRepositoryAsync(new(workstreamId, teamId), context, token, registerOnly: true);
        var setup = state.Payload.Projects[workstreamId];
        if (setup.TeamId != teamId)
            return setup with { Status = "Blocked", Remediation = "The product team changed; reconcile repository ownership before using it." };
        setup = await ReconcileRepositoryAsync(setup, context, token);
        await SaveRepositoryAsync(setup, context, token);
        return setup;
    }

    public override async Task HandleAttentionReviewAsync(AgentAttentionReviewContext review,
        AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        var state = await context.Platform.ReadOperatingStateAsync<RepositoryPortfolio>(RepositoryStateKey, cancellationToken);
        if (state is null) return;
        foreach (var setup in state.Payload.Projects.Values.Where(x => x.Status != "Ready"))
            await SaveRepositoryAsync(await ReconcileRepositoryAsync(setup, context, cancellationToken), context, cancellationToken);
    }
}
