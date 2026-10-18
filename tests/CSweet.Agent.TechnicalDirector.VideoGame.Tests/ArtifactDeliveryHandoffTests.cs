using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class ArtifactDeliveryHandoffTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("current")]
    [InlineData("legacy")]
    [InlineData("bad-latest")]
    [InlineData("wrong-content")]
    public async Task ActualAuthorCallbackSurvivesPersistenceAndReachesIndependentQa(string scenario)
    {
        var author = new SpecialistAgent();
        var authorInstallation = Guid.NewGuid(); var qaInstallation = Guid.NewGuid();
        var artifactId = Guid.NewGuid(); var revisionId = Guid.NewGuid();
        var content = "## Architecture\nPhaser runtime API evidence bound to this exact revision.";
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var members = new[] { new ArtifactPackageMemberDigest(Guid.NewGuid(), Guid.NewGuid(), "brief", new string('a', 64)) };
        var packageId = Guid.NewGuid();
        var planning = new WorkItemPlanningSpecification(["Validate engine API"], ["Identify supported runtime API"], [])
        {
            DelegationRecommendations = [new("specialist-execution", author.DeclaredRoleKey, ["work.execution.run"], null, true, "Author technical validation")],
            ArtifactPackageDigest = new(packageId, 1, ArtifactPackageDigestCalculator.Calculate(packageId, 1, members), DateTimeOffset.UtcNow, members)
        };
        var input = new WorkExecutionInputV1(Guid.NewGuid(), Guid.NewGuid(), 1, planning)
        {
            AssignmentRequirements = new(author.DeclaredRoleKey, [], [], ["work.execution.run"]),
            AssignmentSelection = new(authorInstallation, 1, new string('a', 64), [], new string('a', 64), DateTimeOffset.UtcNow)
        };
        var assignment = JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with
        {
            Scope = "Task", PolicyRevisionId = Guid.NewGuid(),
            SprintExecutionId = Guid.NewGuid(), ExecutionId = Guid.NewGuid(), StageExecutionId = Guid.NewGuid(), AttemptId = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(), BoardId = Guid.NewGuid(), SprintId = Guid.NewGuid(), ItemId = Guid.NewGuid(),
            AssignmentRevision = 1, Attempt = 1, Deadline = DateTimeOffset.UtcNow.AddMinutes(5), StageKey = "specialist-execution",
            Item = JsonSerializer.SerializeToElement(new { }), Input = JsonSerializer.SerializeToElement(input, Json), PriorOutcomes = [], Evidence = []
        };
        // Recover the actual author's completed durable state, exercising its production outcome writer.
        var payload = new SpecialistOperatingState
        {
            WorkstreamId = input.WorkstreamId!.Value, WorkItemId = assignment.TaskItemId, RoleKey = author.DeclaredRoleKey,
            StageExecutionId = assignment.StageExecutionId, AttemptId = assignment.AttemptId, AssignmentRevision = 1, Status = "Completed",
            Delivery = new("Exact technical validation", artifactId, revisionId, sha, [], [])
        };
        var now = DateTimeOffset.UtcNow;
        var state = new AgentOperatingStateResponse(Guid.NewGuid(), ProjectStateKeys.WorkItem(author.DeclaredRoleKey, input.WorkstreamId.Value, assignment.TaskItemId),
            "com.csweet.video-game.specialist-work-state.v1", 1, "Active", new Dictionary<string, string>(), [], "completed", [], Guid.NewGuid(),
            JsonSerializer.SerializeToElement(payload), 1, now, now);
        var authorRuntime = new AgentTestRuntime().RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(
            PlatformCapabilities.AgentOperatingStateRead, (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(state)));
        var authored = await author.ExecuteCapabilityAsync(new(Guid.NewGuid(), WorkManagementCapabilityNames.ExecutionRun,
            JsonSerializer.SerializeToElement(assignment, Json), "author"), authorRuntime.CreateContext(assignment.OrganizationId.ToString(), authorInstallation.ToString()), default);
        Assert.True(authored.Succeeded, authored.Error);
        var outcome = authored.Value!.Value.Deserialize<WorkExecutionOutcomeV1>(Json)!;
        Assert.Equal(WorkExecutionDispositions.Completed, outcome.Disposition);
        Assert.Equal("completed", outcome.OutcomeCode);
        Assert.Equal(artifactId, outcome.Output.GetProperty("artifactId").GetGuid());
        Assert.Equal(revisionId, outcome.Output.GetProperty("revisionId").GetGuid());
        Assert.Equal(sha, outcome.Output.GetProperty("sha256").GetString());
        if (scenario == "legacy")
            outcome = outcome with { Output = JsonSerializer.SerializeToElement(new { ArtifactId = artifactId, RevisionId = revisionId, Sha256 = sha, RemainingRisks = Array.Empty<string>() }) };
        // The platform persists the envelope, then embeds its unchanged JsonElement in QA's assignment.
        var persisted = JsonSerializer.Deserialize<WorkExecutionOutcomeV1>(JsonSerializer.Serialize(outcome, Json), Json)!;
        var prior = new List<WorkExecutionOutcomeV1> { persisted };
        if (scenario == "bad-latest") prior.Add(persisted with { AttemptId = Guid.NewGuid(), Output = JsonSerializer.SerializeToElement(new { artifactId, revisionId }) });
        var qa = assignment with
        {
            StageKey = "quality", StageExecutionId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), PriorOutcomes = prior,
            Item = JsonSerializer.SerializeToElement(new { deliverySpecificationJson = JsonSerializer.Serialize(new { deliveryKind = "Artifact" }) })
        };
        qa = JsonSerializer.Deserialize<WorkExecutionAssignment>(JsonSerializer.Serialize(qa, Json), Json)!;
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with
        {
            Id = assignment.TaskItemId, PlanningRevision = 1, Planning = planning,
            StageAssignments = [new("quality", "AgentInstallation", Guid.NewGuid(), qaInstallation)]
        };
        var reads = 0; var reviews = 0;
        var qaRuntime = new AgentTestRuntime()
            .RegisterCapability<WorkItemReference, WorkItem>(WorkItemCapabilities.Read, (_, _) => Task.FromResult(item))
            .RegisterCapability<JsonElement, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (request, _) =>
            {
                reads++; Assert.Equal(artifactId, request.GetProperty("artifactId").GetGuid());
                return Task.FromResult(new ArtifactDocument(artifactId, "Validation", "video-game.technical-design.v1", "Submitted", revisionId, revisionId, null,
                    null, assignment.TaskItemId, [new(revisionId, 1, null, scenario == "wrong-content" ? content + " tampered" : content, sha, "Submitted", now, now, null)]));
            })
            .RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (request, _) =>
            {
                reviews++;
                var message = request.GetProperty("messages").EnumerateArray().Last();
                var prompt = JsonSerializer.Deserialize<JsonElement>(message.GetProperty("text").GetString()!);
                Assert.Equal(content, prompt.GetProperty("content").GetString());
                var report = new WorkArtifactQualityResult(artifactId, revisionId, sha, true, "Inspected exact API evidence",
                    [new(planning.AcceptanceCriteria.Single(), true, "Exact document identifies the runtime API")], []);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { text = JsonSerializer.Serialize(report, Json), role = "assistant" }));
            });
        var context = qaRuntime.CreateContext(assignment.OrganizationId.ToString(), qaInstallation.ToString());
        var client = context.CreateChatClient(new(Guid.NewGuid(), "test-model"));
        Assert.True(ArtifactDeliveryReview.Supports(qa));
        if (scenario is "bad-latest" or "wrong-content")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ArtifactDeliveryReview.ExecuteAsync(qa, context, client, default));
            Assert.Equal(scenario == "bad-latest" ? 0 : 1, reads);
            Assert.Equal(0, reviews);
            return;
        }
        var reviewed = await ArtifactDeliveryReview.ExecuteAsync(qa, context, client, default);
        var reviewedOutcome = reviewed.Value!.Value.Deserialize<WorkExecutionOutcomeV1>(Json)!;
        Assert.Equal("passed", reviewedOutcome.OutcomeCode);
        // SDK reauthorizes the exact revision after the independent verdict.
        Assert.Equal(2, reads); Assert.Equal(1, reviews);
        var result = reviewedOutcome.Output.Deserialize<WorkArtifactQualityResult>(Json)!;
        Assert.Equal(artifactId, result.ArtifactId); Assert.Equal(revisionId, result.RevisionId); Assert.Equal(sha, result.Sha256);
    }
}
