using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class ToolchainFeasibilityTests
{
    // Original Prism Breaker turn 0, prior to the exact pitch being attached on retry.
    private const string RequestJson = """
        {"acceptedVisionDigest":"55b6046ea3bcfa1d49fd611c53dde0afd722ba73ca9f0e6c1de15a038e3092b5","requiredRecipeKey":"phaser.web-2d.v1","targetKeys":["web"],
         "eligibleDefinitions":[{"Id":"512a7a4c-b0e7-4640-9771-33179e59af48","Key":"node-typescript.v1","Version":1,"DefinitionDigest":"75c93db0d949c555cf37986503f454594e771da77b2c187c14d914a23de5eceb","ProviderInstallationId":"8c5812ad-e557-449f-8d32-3b1e4c8b2b85","EnvironmentProfileKey":"office.linux-x64.node-24.v1","EnvironmentImageDigest":"sha256:142e8d79b79c706559ca2129d7475031d178f904c1adc969635101469a1a8fb1","ExpiresAt":"2027-02-11T19:03:38.151558+00:00"}]}
        """;

    private static AgentCoordinationTurnRequest Request(JsonObject payload)
    {
        var director = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Marcus Vale", "Director");
        var artifact = new AgentCoordinationArtifact(SpecialistAgent.FeasibilityRequestType, "1.0",
            (string)payload["acceptedVisionDigest"]!, 1, true, JsonSerializer.SerializeToElement(payload), "retained");
        return new(Guid.NewGuid(), 1, 1, "Toolchain feasibility approval", "Assess Phaser", [],
            new(Guid.NewGuid(), Guid.NewGuid(), "Technical Director", "Reviewer"), director, false,
            [new(Guid.NewGuid(), 0, director.OrganizationUserId, "Continue", "Perform the feasibility review.", DateTimeOffset.UtcNow, artifact)])
            { WorkContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, Guid.NewGuid(), null, VideoGameProfileKeys.ProductionV2) };
    }

    [Fact]
    public async Task Original_request_asks_Marcus_for_the_pitch_instead_of_a_planning_cycle()
    {
        var request = Request(JsonNode.Parse(RequestJson)!.AsObject());
        var reply = await new SpecialistAgent().HandleCoordinationTurnAsync(request, new AgentTestRuntime().CreateContext(), default);
        Assert.Equal("Continue", reply.Disposition);
        Assert.Contains("attach the exact accepted pitch", reply.Content);
    }

    [Theory]
    [InlineData("feasible")]
    [InlineData("blocking-risk")]
    [InlineData("image-changed")]
    [InlineData("different-pitch")]
    public async Task Review_binds_the_exact_vision_recipe_targets_and_current_capacity(string scenario)
    {
        var payload = JsonNode.Parse(RequestJson)!.AsObject();
        var offered = payload["eligibleDefinitions"]![0]!;
        const string pitch = "Build a 2D Phaser game for desktop Chromium. Target 60 fps.";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pitch)));
        var source = new CollaborationDocumentReference(Guid.NewGuid(), Guid.NewGuid(), hash);
        payload["documentReferences"] = JsonSerializer.SerializeToNode(new[] { source }, JsonSerializerOptions.Web);
        if (scenario != "different-pitch") payload["acceptedVisionDigest"] = hash;
        var request = Request(payload);
        var now = DateTimeOffset.UtcNow;
        var definition = new ToolchainAdapterDefinition((Guid)offered["Id"]!, "node-typescript.v1", 1, "Node", "pkg", "1",
            (string)offered["DefinitionDigest"]!, [new("phaser.web-2d.v1", ["build"], ["web"], JsonSerializer.SerializeToElement(new {}), [], [])],
            JsonSerializer.SerializeToElement(new { node = "24" }), JsonSerializer.SerializeToElement(new {}), [], [], now);
        var adapter = new EligibleToolchainAdapter(definition, new(definition.Id, (Guid)offered["ProviderInstallationId"]!,
            Guid.NewGuid(), (string)offered["EnvironmentProfileKey"]!, scenario == "image-changed" ? "different" : (string)offered["EnvironmentImageDigest"]!,
            now, now.AddDays(1), true));
        var runtime = new AgentTestRuntime().RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (args, _) =>
        {
            Assert.Equal(source.DocumentId, args.GetProperty("artifactId").GetGuid());
            return Task.FromResult(new ArtifactDocument(source.DocumentId, "Official Game Pitch", VideoGameArtifactTypeKeys.Vision, "Approved",
                source.RevisionId, null, source.RevisionId, null, null, [new(source.RevisionId, 1, null, pitch, hash, "Accepted", now, now, now)])
                { WorkstreamId = request.WorkContext!.WorkstreamId });
        }).RegisterCapability<ReadToolchainCatalogV2Request, IReadOnlyList<EligibleToolchainAdapter>>(PlatformCapabilities.ToolchainCatalogRead, (read, _) =>
        {
            Assert.Equal("phaser.web-2d.v1", read.RecipeKey);
            Assert.Equal(["web"], read.TargetKeys);
            Assert.Contains("test", read.RequiredOperations!);
            return Task.FromResult<IReadOnlyList<EligibleToolchainAdapter>>([adapter]);
        });
        var calls = 0;
        var result = await SpecialistAgent.ReviewToolchainAsync(request, request.Transcript[0].Artifact!, runtime.CreateContext(),
            (data, _, _) =>
            {
                calls++;
                Assert.Contains(pitch, data);
                Assert.Contains("node-24", data);
                return Task.FromResult(new SpecialistAgent.TechnicalFindings(["The recipe supports the browser target."],
                    scenario == "blocking-risk" ? ["Required dependency is incompatible."] : []));
            }, default);
        if (scenario is "different-pitch" or "image-changed")
        {
            Assert.Equal("Blocked", result.Disposition);
            Assert.Null(result.Artifact);
            Assert.Equal(0, calls);
            return;
        }
        Assert.Equal(1, calls);
        Assert.Equal(scenario == "feasible" ? "Completed" : "Blocked", result.Disposition);
        Assert.Equal("video-game.toolchain-feasibility.v1", result.Artifact!.Type);
        // Marcus consumes the shared DTO with the default (case-sensitive) serializer.
        var evidence = result.Artifact.Payload.Deserialize<ToolchainFeasibilityEvidenceV1>()!;
        Assert.Equal(hash, evidence.AcceptedVisionDigest);
        Assert.Equal("phaser.web-2d.v1", evidence.RecipeKey);
        Assert.Equal(["web"], evidence.TargetKeys);
        Assert.Equal([source.DocumentId], evidence.EvidenceResourceIds);
        Assert.Equal(scenario == "feasible", evidence.Feasible);
    }
}
