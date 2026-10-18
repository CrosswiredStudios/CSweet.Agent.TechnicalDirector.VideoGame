using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class WorkflowReviewAssignmentTests
{
    [Fact]
    public void LongReviewProcessingFailureRetainsDiagnosticsWithAStorageSafeStatus()
    {
        var (assignment, _) = Create("technical-review", "game-technical-director");
        var reason = "C-Sweet review-processing issue: " + new string('e', 16000);
        var outcome = SpecialistAgent.ReviewBlockedOutcome(assignment, reason);
        Assert.True(outcome.Summary.Length <= 4096);
        Assert.Contains("Full review-processing error is retained", outcome.Summary);
        Assert.Equal(reason, outcome.Output.GetProperty("reviewProcessingError").GetString());
        Assert.Equal(reason, Assert.Single(outcome.Diagnostics));
        Assert.Equal("Blocked", outcome.Disposition);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_callback_reaches_platform_and_preserves_actionable_denials(bool wrongRole)
    {
        var (assignment, input) = Create("technical-review", "game-technical-director");
        if (wrongRole)
            assignment = assignment with { Input = JsonSerializer.SerializeToElement(input with
                { AssignmentRequirements = input.AssignmentRequirements! with { RequiredRoleKey = "game-engineer" } },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        var reads = 0;
        var runtime = new AgentTestRuntime().RegisterCapability<WorkItemReference, WorkItem>(
            WorkManagementCapabilityNames.ItemRead, (_, _) =>
            {
                reads++;
                throw new PlatformCapabilityException(WorkManagementCapabilityNames.ItemRead,
                    PlatformCapabilityErrorCode.Denied, "The current item read grant is missing.");
            });
        var agent = await ConfiguredAsync(runtime);
        var result = await runtime.ExecuteCapabilityAsync(agent, WorkManagementCapabilityNames.ExecutionRun, assignment);
        Assert.True(result.Succeeded);
        var outcome = result.Value!.Value.Deserialize<WorkExecutionOutcomeV1>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Blocked", outcome.Disposition);
        Assert.Equal(wrongRole ? 0 : 1, reads);
        Assert.Contains(wrongRole ? "canonical assignment" : "The current item read grant is missing.", outcome.Summary);
        Assert.Empty(outcome.Evidence);
    }

    [Theory]
    [InlineData("technical-review", "game-technical-director")]
    [InlineData("quality", "game-quality-assurance")]
    public void Manager_assigned_review_accepts_original_plan_that_only_delegated_implementation(string stage, string role)
    {
        var (assignment, input) = Create(stage, role);
        var validated = SpecialistAssignmentValidator.Validate(assignment, role);
        Assert.Single(validated.Planning!.DelegationRecommendations);
        Assert.Equal("specialist-execution", validated.Planning.DelegationRecommendations[0].StageKey);
        Assert.Equal(input.Planning!.ArtifactPackageDigest!.Sha256, validated.Planning.ArtifactPackageDigest!.Sha256);
    }

    [Theory]
    [InlineData("wrong-role")]
    [InlineData("wrong-stage-role")]
    [InlineData("missing-selection")]
    [InlineData("missing-requirements")]
    [InlineData("wrong-package")]
    [InlineData("unknown-stage")]
    [InlineData("standalone-owner")]
    public void Review_still_requires_canonical_eligibility_and_approved_inputs(string problem)
    {
        var (assignment, input) = Create("technical-review", "game-technical-director");
        input = problem switch
        {
            "wrong-role" => input with { AssignmentRequirements = input.AssignmentRequirements! with { RequiredRoleKey = "game-engineer" } },
            "missing-selection" => input with { AssignmentSelection = null },
            "missing-requirements" => input with { AssignmentRequirements = null },
            "wrong-package" => input with { Planning = input.Planning! with { ArtifactPackageDigest = input.Planning.ArtifactPackageDigest! with { Sha256 = new string('b', 64) } } },
            _ => input
        };
        assignment = assignment with
        {
            StageKey = problem switch { "wrong-stage-role" => "quality", "unknown-stage" => "custom-review", "standalone-owner" => "specialist-execution", _ => assignment.StageKey },
            Input = JsonSerializer.SerializeToElement(input, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        Assert.ThrowsAny<Exception>(() => SpecialistAssignmentValidator.Validate(assignment, "game-technical-director"));
    }

    [Fact]
    public async Task RawPlatformAssignmentReadsCurrentDeliveryAndUsesReferenceReviewWithoutFallback()
    {
        var (assignment, input) = Create("technical-review", "game-technical-director");
        var repository = Guid.NewGuid();
        var delivery = new WorkItemDeliverySpecification(repository, ["Works"], ["Works"])
            { DeliveryPlanId = Guid.NewGuid(), BaseBranch = "story" };
        // Match WorkOrchestrator.DispatchAsync's actual wire shape, not a synthetic typed WorkItem.
        assignment = assignment with { PlanningRevision = 2, DeliveryPlanId = delivery.DeliveryPlanId,
            Input = JsonSerializer.SerializeToElement(input with { PlanningRevision = 2 }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Item = JsonSerializer.SerializeToElement(new
        {
            id = assignment.ItemId, title = "Engine setup", planningRevision = 2,
            deliverySpecificationJson = JsonSerializer.Serialize(delivery, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        }) };
        var current = JsonSerializer.Deserialize<WorkItem>("{}")! with
        {
            Id = assignment.TaskItemId, PlanningRevision = 2, Delivery = delivery, Planning = input.Planning,
            StageAssignments = [new("technical-review", "AgentInstallation",
                AgentInstallationId: Guid.Parse("00000000-0000-0000-0000-000000000001"))]
        };
        var reads = 0;
        var publication = Guid.NewGuid();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<WorkItemReference, WorkItem>(WorkManagementCapabilityNames.ItemRead, (_, _) => Task.FromResult(current))
            .RegisterCapability<ReviewGitMergeRequest, GitMergeReview>(GitMergeCapabilities.Review, (request, _) =>
            {
                reads++;
                Assert.False(request.IncludePatch);
                Assert.Null(request.DiffRead);
                return Task.FromResult(new GitMergeReview(publication, repository, current.Id, "Game",
                    new string('a', 40), null, "", [], [], "AwaitingValidation")
                {
                    TargetBranch = "story", TargetCommitSha = new string('b', 40), PatchDigest = new string('c', 64),
                    ChangedFiles = ["dist/assets/game.js"]
                });
            });
        var result = await runtime.ExecuteCapabilityAsync(await ConfiguredAsync(runtime), WorkManagementCapabilityNames.ExecutionRun, assignment);
        Assert.True(result.Succeeded);
        var outcome = result.Value!.Value.Deserialize<WorkExecutionOutcomeV1>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("rejected", outcome.OutcomeCode);
        Assert.Contains("generated", outcome.Summary);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task StandaloneMergeDecisionIsRejectedWithoutReadingOrAuthorizingGit()
    {
        var (assignment, _) = Create("merge-decision", "game-technical-director");
        var result = await new AgentTestRuntime().ExecuteCapabilityAsync(new SpecialistAgent(), WorkManagementCapabilityNames.ExecutionRun, assignment);
        Assert.False(result.Succeeded);
        Assert.Contains("platform", result.Error);
    }

    private static async Task<SpecialistAgent> ConfiguredAsync(AgentTestRuntime runtime)
    {
        var agent = new SpecialistAgent();
        var result = await runtime.ExecuteCapabilityAsync(agent, AgentConfigurationCapabilities.Update,
            new { settings = new { llmProviderId = Guid.NewGuid(), llmModel = "test" } });
        Assert.True(result.Succeeded, result.Error);
        return agent;
    }

    private static (WorkExecutionAssignment, WorkExecutionInputV1) Create(string stage, string role)
    {
        var digest = new string('a', 64);
        var members = new[] { new ArtifactPackageMemberDigest(Guid.NewGuid(), Guid.NewGuid(), "brief", digest) };
        var package = Guid.NewGuid();
        var input = new WorkExecutionInputV1(Guid.NewGuid(), Guid.NewGuid(), 1,
            new WorkItemPlanningSpecification(["Implement"], ["Test"], [])
            {
                DelegationRecommendations = [new("specialist-execution", "game-engineer", ["work.execution.run"], null, true, "Original approved planning")],
                ArtifactPackageDigest = new(package, 1, ArtifactPackageDigestCalculator.Calculate(package, 1, members), DateTimeOffset.UtcNow, members)
            })
        {
            AssignmentRequirements = new(role, [], [], ["work.execution.run"]),
            AssignmentSelection = new(Guid.NewGuid(), 5, digest, [], digest, DateTimeOffset.UtcNow)
        };
        var assignment = JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with
        {
            Scope = "Task", PolicyRevisionId = Guid.NewGuid(),
            SprintExecutionId = Guid.NewGuid(), ExecutionId = Guid.NewGuid(), StageExecutionId = Guid.NewGuid(), AttemptId = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(), BoardId = Guid.NewGuid(), SprintId = Guid.NewGuid(), ItemId = Guid.NewGuid(),
            AssignmentRevision = 1, Attempt = 1, Deadline = DateTimeOffset.UtcNow.AddMinutes(5), StageKey = stage,
            Item = JsonSerializer.SerializeToElement(new { }), Evidence = [],
            Input = JsonSerializer.SerializeToElement(input, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        return (assignment, input);
    }
}
