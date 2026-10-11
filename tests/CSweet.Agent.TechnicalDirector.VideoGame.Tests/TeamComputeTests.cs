using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.Compute;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class TeamComputeTests
{
    [Theory]
    [InlineData("Ready", true)]
    [InlineData("Pending", false)]
    public async Task Discovers_options_without_a_certified_toolchain_and_replays_one_project_environment(string status, bool provision)
    {
        var project = Guid.NewGuid();
        var requests = new List<ProvisionComputeRequest>();
        AgentOperatingStateResponse? retained = null;
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
                (_,_) => Task.FromResult(new AgentOperatingStateReadResponse(retained)))
            .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,(request,_) => {
                var now=DateTimeOffset.UtcNow;
                retained=new(Guid.NewGuid(),request.StateKey,request.SchemaId,request.SchemaVersion,request.Status,
                    request.SourceRevisions,request.ConditionCodes,request.DecisionFingerprint,request.OpenCommitmentCorrelations,
                    request.AttentionReviewId,request.Payload,1,now,now);
                return Task.FromResult(retained);
            });
        var setup=new SpecialistAgent.RepositorySetup(project,Guid.NewGuid(),"Ready",Guid.NewGuid(),DefaultBranch:"main");
        await SpecialistAgent.ReconcileTeamComputeAsync(setup,runtime.CreateContext(),default);
        await SpecialistAgent.ReconcileTeamComputeAsync(setup,runtime.CreateContext(),default);
        Assert.Equal(provision ? 2 : 0,requests.Count);
        if(provision) Assert.Equal(requests[0],requests[1]);
        var options=retained!.Payload.Deserialize<SpecialistAgent.TeamComputeOptions>()!;
        Assert.Empty(options.Certified); Assert.Equal("compute-test",options.SelectedPath);
        Assert.Equal(provision,options.EnvironmentId.HasValue);
        Assert.Equal("local-machine",options.Compute.LinkReachability);
        Assert.All(requests,x=>Assert.Null(x.Specification.Network));
    }
}
