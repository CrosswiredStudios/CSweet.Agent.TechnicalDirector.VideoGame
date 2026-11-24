using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.Agent.SDK.Compute;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class TeamComputeTests
{
    [Theory, Trait("Category", "AgentHarness")]
    [InlineData("Ready")]
    [InlineData("Pending")]
    public async Task Repository_readiness_discovers_options_without_reserving_a_test_environment(string status)
    {
        var project = Guid.NewGuid();
        var requests = new List<ProvisionComputeRequest>();
        AgentOperatingStateResponse? retained = null;
        var now = DateTimeOffset.UtcNow;
        var setup = new SpecialistAgent.RepositorySetup(project, Guid.NewGuid(), "Ready", Guid.NewGuid(), DefaultBranch: "main");
        var portfolio = new SpecialistAgent.RepositoryPortfolio(new Dictionary<Guid, SpecialistAgent.RepositorySetup> { [project] = setup });
        var repositories = new AgentOperatingStateResponse(Guid.NewGuid(), "technical-director:repositories",
            "video-game.repository-ownership.v1", 1, "Active", new Dictionary<string,string>(), [], "ready", [],
            Guid.NewGuid(), JsonSerializer.SerializeToElement(portfolio), 1, now, now);
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement,ComputeDefaults>(ComputeCapabilities.Read,(request,_) => {
                Assert.True(request.GetProperty("defaults").GetBoolean());
                Assert.Equal(project,request.GetProperty("workstreamId").GetGuid());
                return Task.FromResult(new ComputeDefaults(status,project,"linux-test",null) {
                    Features = ["guest-execution","python","docker-apps-v1"],
                    CachedImages = ["csweet/python:3.12","csweet/node:22"], LinkReachability = "local-machine" });
            })
            .RegisterCapability<ReadToolchainCatalogV2Request,IReadOnlyList<EligibleToolchainAdapter>>(PlatformCapabilities.ToolchainCatalogRead,
                (_,_) => Task.FromResult<IReadOnlyList<EligibleToolchainAdapter>>([]))
            .RegisterCapability<ProvisionComputeRequest,ComputeEnvironment>(ComputeCapabilities.Provision,(request,_) => {
                requests.Add(request);
                return Task.FromResult(new ComputeEnvironment(project,1,1,"running","ready","ephemeral",DateTimeOffset.UtcNow,DateTimeOffset.MaxValue,null));
            })
            .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (request,_) => Task.FromResult(new AgentOperatingStateReadResponse(request.StateKey == repositories.StateKey ? repositories : retained)))
            .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,(request,_) => {
                var now=DateTimeOffset.UtcNow;
                retained=new(Guid.NewGuid(),request.StateKey,request.SchemaId,request.SchemaVersion,request.Status,
                    request.SourceRevisions,request.ConditionCodes,request.DecisionFingerprint,request.OpenCommitmentCorrelations,
                    request.AttentionReviewId,request.Payload,1,now,now);
                return Task.FromResult(retained);
            });
        runtime.RegisterCapability<ClaimPersonalTodoItemRequest, PersonalTodoClaim>(PersonalTodoCapabilities.Claim,
            (_, _) => Task.FromResult(new PersonalTodoClaim((PersonalTodoItem?)null)));
        runtime.RegisterCapability<JsonElement, IReadOnlyList<WorkBoardSummary>>(WorkBoardCapabilities.Read,
            (_, _) => Task.FromResult<IReadOnlyList<WorkBoardSummary>>([]));
        runtime.RegisterCapability<ReadWorkDeliveryPlansRequest, IReadOnlyList<WorkDeliveryPlanResponse>>(WorkDeliveryCapabilities.Read,
            (_, _) => Task.FromResult<IReadOnlyList<WorkDeliveryPlanResponse>>([]));
        var delivery = Guid.NewGuid();
        var review = new AgentAttentionReviewDueEvent(Guid.NewGuid(), now, now.AddMinutes(5), "Recovered");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var reply = await new AgentTestHost(new SpecialistAgent(), runtime).DeliverEventAsync(AgentAttentionEvents.ReviewDue, review, delivery);
            Assert.True(reply.Succeeded, reply.Error);
        }
        Assert.Empty(requests);
        var options=retained!.Payload.Deserialize<SpecialistAgent.TeamComputeOptions>()!;
        Assert.Empty(options.Certified); Assert.Equal("compute-test",options.SelectedPath);
        Assert.Null(options.EnvironmentId);
        Assert.Equal("local-machine",options.Compute.LinkReachability);
        Assert.Equal(1, retained.Revision);
    }
}
