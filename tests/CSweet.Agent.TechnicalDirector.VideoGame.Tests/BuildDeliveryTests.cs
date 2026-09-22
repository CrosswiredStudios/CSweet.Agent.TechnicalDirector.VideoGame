using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class BuildDeliveryTests
{
    [Theory]
    [InlineData("Planned", 0)]
    [InlineData("Active", 1)]
    public async Task AttentionTreatsMissingSprintExecutionAsNoBuildWork(string sprintStatus, int expectedReads)
    {
        var workstream = Guid.NewGuid(); var team = Guid.NewGuid(); var repository = Guid.NewGuid();
        var boardId = Guid.NewGuid(); var sprintId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var portfolio = new SpecialistAgent.RepositoryPortfolio(new Dictionary<Guid, SpecialistAgent.RepositorySetup>
            { [workstream] = new(workstream, team, "Ready", repository) });
        var state = new AgentOperatingStateResponse(Guid.NewGuid(), "technical-director:repositories", "test", 1,
            "Active", new Dictionary<string, string>(), [], "fingerprint", [], Guid.NewGuid(),
            JsonSerializer.SerializeToElement(portfolio), 1, now, now);
        var board = new WorkBoardSummary(boardId, "Game", "Delivery", false, false, 1, [])
            { WorkstreamId = workstream, TeamId = team };
        var reads = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(
                PlatformCapabilities.AgentOperatingStateRead,
                (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(state)))
            .RegisterCapability<WorkBoardListRequest, IReadOnlyList<WorkBoardSummary>>(WorkBoardCapabilities.Read,
                (_, _) => Task.FromResult<IReadOnlyList<WorkBoardSummary>>([board]))
            .RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read,
                (_, _) => Task.FromResult(new WorkBoardDetail(board, [], [])))
            .RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(WorkSprintCapabilities.Read,
                (_, _) => Task.FromResult<IReadOnlyList<WorkSprint>>([new(sprintId, boardId, "First", "Goal", sprintStatus,
                    null, null, null, null, null, 1, 1, 1, 1, 1)]))
            .RegisterCapability<ReadWorkOrchestrationRequest, WorkSprintExecutionResponse?>(
                WorkOrchestrationCapabilities.Read,
                (_, _) => { reads++; return Task.FromResult<WorkSprintExecutionResponse?>(null); });

        await new SpecialistAgent().HandleAttentionReviewAsync(
            new(Guid.NewGuid(), now, now.AddMinutes(5), "Review"), runtime.CreateContext(), default);

        Assert.Equal(expectedReads, reads);
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("completed-sprint-attention")]
    [InlineData("Queued")]
    [InlineData("Failed")]
    [InlineData("lost-response")]
    [InlineData("wrong-commit")]
    [InlineData("missing-merge")]
    [InlineData("invalid-choice")]
    [InlineData("invalid-configuration")]
    public async Task ExactMergePlanSurvivesRetriesAndPreviewsOnlySuccessfulBuild(string scenario)
    {
        var stream = Guid.NewGuid(); var team = Guid.NewGuid(); var repository = Guid.NewGuid(); var board = Guid.NewGuid();
        var definitionId = Guid.NewGuid(); var providerId = Guid.NewGuid(); var buildId = Guid.NewGuid();
        var stageId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow; var sha = new string('b',40);
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with { Id = Guid.NewGuid(),
            Delivery = new(repository, ["Build static browser game"], ["Playable"], []) };
        var merge = new WorkStageExecutionResponse(stageId, "governed-merge", "TrustedPlatformAction", 0,
            "Completed", "PlatformAction", null, null, "source-control.merge.execute.v2", 1, "merged", "Merged", null, null, now)
            { LatestOutcome = scenario == "missing-merge" ? null : new(stageId, Guid.NewGuid(), "Completed", "merged", "Merged",
                JsonSerializer.SerializeToElement(new { mergeCommitSha = sha }), [], []) };
        var definition = new ToolchainAdapterDefinition(definitionId, "web", 1, "Static web", "provider", "1.0", "digest",
            [new("release", ["build"], ["browser"], JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }), [], [])],
            JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }), ["text/html"], ["web-static"], now);
        var setup = new SpecialistAgent.RepositorySetup(stream, team, "Ready", repository);
        AgentOperatingStateResponse State(string key, object payload) => new(Guid.NewGuid(), key, "test", 1, "Active",
            new Dictionary<string,string>(), [], "fingerprint", [], Guid.NewGuid(), JsonSerializer.SerializeToElement(payload), 1, now, now);
        var portfolio = State("technical-director:repositories", new SpecialistAgent.RepositoryPortfolio(
            new Dictionary<Guid, SpecialistAgent.RepositorySetup> { [stream] = setup }));
        AgentOperatingStateResponse? state = scenario == "completed-sprint-attention" ? State($"game-build:{stream:N}:{stageId:N}",
            new SpecialistAgent.BuildPlan(new(stream, team, definitionId, providerId, repository, sha, "release", "browser",
                JsonSerializer.SerializeToElement(new { }), 3, $"game-build:{stream:N}:{stageId:N}"), "digest", "web-static")) : null;
        var requests = new List<RequestBuildV2Request>(); var previews = new List<CreatePreviewV2Request>();
        var selections = 0; var comments = new List<CommentOnWorkItemRequest>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(request.StateKey == "technical-director:repositories" ? portfolio : state)))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                (request, _) => { state = new(Guid.NewGuid(), request.StateKey, request.SchemaId, request.SchemaVersion,
                    "Active", new Dictionary<string,string>(), [], "fingerprint", [], Guid.NewGuid(), request.Payload, 1, now, now); return Task.FromResult(state); })
            .RegisterCapability<ReadToolchainCatalogV2Request, IReadOnlyList<EligibleToolchainAdapter>>(PlatformCapabilities.ToolchainCatalogRead,
                (_, _) => Task.FromResult<IReadOnlyList<EligibleToolchainAdapter>>([new(definition,
                    new(definitionId, providerId, Guid.NewGuid(), "linux-x64", "image", now, now.AddDays(1), true))]))
            .RegisterCapability<RequestBuildV2Request, DeliveryBuildV2>(PlatformCapabilities.BuildRequest,
                (request, _) => { requests.Add(request); Assert.NotNull(state);
                    if (scenario == "lost-response" && requests.Count == 1) throw new IOException("Lost response");
                    return Task.FromResult(JsonSerializer.Deserialize<DeliveryBuildV2>("{}")! with { Id = buildId,
                        Configuration = request.Configuration, WorkstreamId = stream, RepositoryId = repository, SourceRevision = scenario == "wrong-commit" ? new string('c',40) : sha,
                        ToolchainDefinitionId = definitionId, ProviderInstallationId = providerId, DefinitionDigest = "digest",
                        RecipeKey = "release", TargetKey = "browser", Status = scenario is "lost-response" or "wrong-commit" or "completed-sprint-attention" ? "Succeeded" : scenario }); })
            .RegisterCapability<CreatePreviewV2Request, DeliveryPreviewV2>(PlatformCapabilities.PreviewCreate,
                (request, _) => { previews.Add(request); return Task.FromResult(new DeliveryPreviewV2(Guid.NewGuid(), stream, buildId,
                    "web-static", "Ready", "http://127.0.0.1:5000/game", [], now.AddDays(7), now)); })
            .RegisterCapability<CommentOnWorkItemRequest, WorkItemComment>(WorkItemCapabilities.Comment,
                (request, _) => { comments.Add(request); return Task.FromResult(new WorkItemComment(Guid.NewGuid(), item.Id,
                    "Agent", Guid.NewGuid(), "Technical Director", request.Body, 1, now, null)); });
        Task<SpecialistAgent.BuildChoice> Select(SpecialistAgent.BuildSelectionInput input, AgentRuntimeContext context, CancellationToken token)
        {
            selections++;
            return Task.FromResult(new SpecialistAgent.BuildChoice(scenario == "invalid-choice" ? Guid.NewGuid() : definitionId,
                providerId, "release", "browser", JsonSerializer.Deserialize<JsonElement>(scenario == "invalid-configuration" ? "{\"invented\":true}" : "{}"), "web-static", "Accepted browser target"));
        }
        var boardSummary = new WorkBoardSummary(board, "Game", "Delivery", false, false, 1, []) { WorkstreamId = stream, TeamId = team };
        var sprintId = Guid.NewGuid();
        runtime.RegisterCapability<WorkBoardListRequest, IReadOnlyList<WorkBoardSummary>>(WorkBoardCapabilities.Read,
            (_, _) => Task.FromResult<IReadOnlyList<WorkBoardSummary>>([boardSummary]));
        runtime.RegisterCapability<WorkBoardReference, WorkBoardDetail>(WorkItemCapabilities.Read,
            (_, _) => Task.FromResult(new WorkBoardDetail(boardSummary, [], [item])));
        runtime.RegisterCapability<WorkBoardReference, IReadOnlyList<WorkSprint>>(WorkSprintCapabilities.Read,
            (_, _) => Task.FromResult<IReadOnlyList<WorkSprint>>([new(sprintId, board, "Phase one", "Playable", "Completed",
                null, null, now.AddDays(-1), now, null, 1, 1, 1, 1, 1)]));
        runtime.RegisterCapability<ReadWorkOrchestrationRequest, WorkSprintExecutionResponse>(WorkManagementCapabilityNames.OrchestrationRead,
            (request, _) => { Assert.Equal(sprintId, request.SprintId); return Task.FromResult(new WorkSprintExecutionResponse(
                Guid.NewGuid(), board, sprintId, Guid.NewGuid(), Guid.NewGuid(), "Completed", 1, now.AddDays(-1), now, now,
                [new(Guid.NewGuid(), item.Id, "GAME-1", "done", 0, "Completed", null, [merge], now)])); });
        Task Run() => scenario == "completed-sprint-attention"
            ? new SpecialistAgent().HandleAttentionReviewAsync(new(Guid.NewGuid(), now, now.AddMinutes(5), "Resume"), runtime.CreateContext(), default)
            : SpecialistAgent.ReconcileBuildAsync(setup, board, item, merge, runtime.CreateContext(), Select, default);
        if (scenario is "wrong-commit" or "missing-merge" or "invalid-choice" or "invalid-configuration")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(Run);
            Assert.Empty(previews);
            if (scenario != "wrong-commit") { Assert.Empty(requests); Assert.Null(state); }
            return;
        }
        if (scenario == "lost-response") await Assert.ThrowsAsync<IOException>(Run);
        await Run(); await Run();
        Assert.Equal(scenario == "completed-sprint-attention" ? 0 : 1, selections);
        Assert.All(requests, request => { Assert.Equal(sha, request.SourceRevision); Assert.Equal(repository, request.RepositoryId);
            Assert.Equal(requests[0].IdempotencyKey, request.IdempotencyKey); });
        Assert.Equal(scenario is "Succeeded" or "lost-response" or "completed-sprint-attention", previews.Count > 0);
        Assert.All(previews, preview => { Assert.Equal(buildId, preview.BuildId); Assert.Equal(previews[0].IdempotencyKey, preview.IdempotencyKey); });
        Assert.Equal(comments[0].IdempotencyKey, comments[1].IdempotencyKey);
    }
}
