using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class ResourceRequestTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Project_resource_handoff_requests_capacity_without_requiring_existing_certification(bool matchingScope)
    {
        var project = Guid.NewGuid();
        var need = new ResourceRequest(project, ResourceCapabilities.BuildToolchain, "phaser.web-2d.v1", "Build the approved game", "stable-resource");
        var calls = 0;
        var runtime = new AgentTestRuntime().RegisterCapability<ResourceRequest, ResourceRequestState>(ResourceCapabilities.Request, (input, _) => {
            calls++; Assert.Equal(need, input);
            return Task.FromResult(JsonSerializer.Deserialize<ResourceRequestState>("{}")! with { Id = Guid.NewGuid(),
                WorkstreamId = project, Status = "PendingApproval" });
        });
        var artifact = new AgentCoordinationArtifactSubmission("platform.resource.requirement.v1", "1.0", "source", 1, true, JsonSerializer.SerializeToElement(need));
        var request = new AgentCoordinationTurnRequest(Guid.NewGuid(), 1, 1, "Build capacity", "Prepare capacity", [],
            new(Guid.NewGuid(), Guid.NewGuid(), "Technical Director", "Technical Director"), new(Guid.NewGuid(), Guid.NewGuid(), "Director", "Director"), false,
            [new(Guid.NewGuid(), 0, Guid.NewGuid(), "Continue", "Prepare the approved project requirement", DateTimeOffset.UtcNow,
                new(artifact.Type, artifact.SchemaVersion, artifact.Key, 1, true, artifact.Payload, "digest"))])
            { WorkContext = new(Guid.NewGuid(), matchingScope ? project : Guid.NewGuid(), null, null, null, null, null, Guid.NewGuid(), null, null) };
        var result = await new SpecialistAgent().HandleCoordinationTurnAsync(request, runtime.CreateContext(), default);
        Assert.Equal(matchingScope ? "Completed" : "Blocked", result.Disposition);
        Assert.Equal(matchingScope ? 1 : 0, calls);
        if (matchingScope) Assert.Equal("platform.resource.requested.v1", result.Artifact!.Type);
    }
}
