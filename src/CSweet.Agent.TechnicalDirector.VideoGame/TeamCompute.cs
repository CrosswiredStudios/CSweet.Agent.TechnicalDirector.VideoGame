using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.Compute;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record TeamComputeOptions(ComputeDefaults Compute, IReadOnlyList<EligibleToolchainAdapter> Certified,
        string SelectedPath, string Rationale, Guid? EnvironmentId);
    internal static async Task ReconcileTeamComputeAsync(RepositorySetup setup, AgentRuntimeContext context, CancellationToken ct)
    {
        if (setup.Status != "Ready") return;
        try
        {
            var options = await context.Platform.Compute.GetProjectDefaultsAsync(setup.WorkstreamId, ct);
            IReadOnlyList<EligibleToolchainAdapter> certified = [];
            try { certified = await context.Platform.ReadEligibleToolchainsAsync(new(RequiredOperations: ["build"]), ct); }
            catch (PlatformCapabilityException error) when (error.Code is PlatformCapabilityErrorCode.Denied or PlatformCapabilityErrorCode.NotFound) { }
            Guid? environmentId = null;
            if (options is { State: "Ready", TemplateId: { } template } && options.WorkstreamId == setup.WorkstreamId &&
                options.Features.Contains("docker-apps-v1") && options.CachedImages.Contains("csweet/python:3.12"))
            {
                var desired = $"project-test:{setup.WorkstreamId:N}";
                var environment = await context.Platform.Compute.ProvisionAsync(new(setup.WorkstreamId, desired, desired,
                    new("linux", "x64", template, new(2, 2048, 20480), options.MaximumLifetimeSeconds)), ct);
                environmentId = environment.Id;
            }
            var key = $"technical-director:compute:{setup.WorkstreamId:N}";
            var selected = new TeamComputeOptions(options, certified, "compute-test",
                "Sprint implementation proceeds without certified packaging. Choose a source-compatible Docker or static recipe from reported guest features and cached images; exact-source build, HTTP and independent QA evidence are required. Certified recipes remain available for separately agreed packaging.",
                environmentId);
            var previous = await context.Platform.ReadOperatingStateAsync<TeamComputeOptions>(key, ct);
            if (previous is not null && JsonSerializer.Serialize(previous.Payload) == JsonSerializer.Serialize(selected)) return;
            await new RevisionSafeProjectState(context.Platform).MergeAsync<TeamComputeOptions>(key,
                "video-game.team-compute.v1", 1, _ => selected, new Dictionary<string,string>(), $"{key}:{Guid.NewGuid():N}", ct);
        }
        catch (PlatformCapabilityException error)
        {
            await context.ReportProgressAsync(new { stage = "team-compute-wait", projectId = setup.WorkstreamId,
                reason = error.FailureCode ?? error.Code.ToString() }, ct);
        }
    }

    private static async Task ReportTestUrlsAsync(Guid project, AgentRuntimeContext context, CancellationToken ct)
    {
        try
        {
            var plans = await context.Platform.Work.ReadDeliveryPlansAsync(new(project), ct);
            foreach (var plan in plans.Where(x => x.Status is not ("Cancelled" or "Draft")).OrderByDescending(x => x.CreatedAt).Take(1))
            foreach (var execution in plan.Executions.Where(x => x.Scope == WorkExecutionScopes.Release))
            {
                var stage = execution.Stages.LastOrDefault(x => x.StageKey == "build-readiness" && x.LastOutcomeCode == "approved");
                var result = stage?.LatestOutcome?.Output.Deserialize<WorkDeliveryReviewResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (result?.ComputeTests is not { Count: > 0 } || execution.Candidate?.Digest != result.CandidateDigest ||
                    result.ComputeTests.Any(x => x.UrlExpiresAt <= DateTimeOffset.UtcNow)) continue;
                var snapshot = await context.Platform.ReadOrganizationSnapshotAsync(ct);
                var roles = snapshot.Roles.Where(x => x.Name.Equals("CEO", StringComparison.OrdinalIgnoreCase) ||
                    x.Name.Equals("Chief Executive Officer", StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToHashSet();
                var executives = snapshot.People.Where(x => x.IsActive && x.EmployeeType == "Human" &&
                    x.RoleId is { } role && roles.Contains(role)).Select(x => x.Id).ToArray();
                var rootHumans = snapshot.People.Where(x => x.IsActive && x.EmployeeType == "Human" && x.ReportsToId is null).ToArray();
                var recipients = (executives.Length > 0 ? executives : rootHumans.Length == 1 ? new[] { rootHumans[0].Id } : Array.Empty<Guid>())
                    .Append(plan.ManagerOrganizationUserId).Distinct();

                foreach (var recipient in recipients)
                    await context.Platform.Communication.SendDirectMessageAsync(recipient,
                        $"A test application is ready for review. {result.Summary}\nThis link verifies build/HTTP readiness; full QA and acceptance are tracked separately on the project.",
                        $"test-url:{execution.Id:N}:{result.CandidateDigest[..16]}:{recipient:N}",
                        new AgentWorkContext(Guid.Parse(context.BusinessId), project, null, null, null, null, null, execution.Id, null, null), ct);
            }
        }
        catch (PlatformCapabilityException error)
        {
            // Missing reporting authority cannot erase valid compute evidence. Attention/change wakes retry delivery.
            await context.ReportProgressAsync(new { stage = "test-url-report-wait", projectId = project,
                reason = error.FailureCode ?? error.Code.ToString() }, ct);
        }
    }
}
