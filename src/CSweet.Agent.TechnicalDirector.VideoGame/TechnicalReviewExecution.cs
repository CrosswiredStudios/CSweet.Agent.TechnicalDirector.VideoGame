using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    protected override async Task<AgentWorkResult> ExecuteCapabilityCoreAsync(
        AgentCapabilityRequest request, AgentRuntimeContext context, CancellationToken token)
    {
        if (request.Capability != WorkManagementCapabilityNames.ExecutionRun)
            return await base.ExecuteCapabilityCoreAsync(request, context, token);
        var assignment = DeserializePayload<WorkExecutionAssignment>(request.Arguments);
        if (ProjectDeliveryReview.Supports(assignment) && assignment!.StageKey == "quality")
            return await ProjectDeliveryReview.ExecuteAsync(assignment, context,
                context.CreateChatClient(new AgentLlmSelection(Settings.GetGuid("llmProviderId") ??
                    throw new InvalidOperationException("Configure a review provider."), Settings.GetString("llmModel"))), false, token);
        if (assignment?.StageKey == "merge-decision")
            return AgentWorkResult.Failure("Task integration is owned by the platform; standalone merge-decision execution is unsupported.",
                "execution.unsupported_stage");
        if (assignment?.StageKey != "technical-review")
            return await base.ExecuteCapabilityCoreAsync(request, context, token);
        try
        {
            _ = SpecialistAssignmentValidator.Validate(assignment, RoleKey);
            // Dispatch carries raw deliverySpecificationJson. The shared reviewer reads the
            // current typed item and checks its independent assignment before reading a candidate.
            return await DeliveryTaskTechnicalReview.ExecuteAsync(assignment, context,
                context.CreateChatClient(new AgentLlmSelection(Settings.GetGuid("llmProviderId") ??
                    throw new InvalidOperationException("Configure an approved review provider."), Settings.GetString("llmModel"))), token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var reason = error switch
            {
                InvalidOperationException or ArgumentException or UnauthorizedAccessException => error.Message,
                PlatformCapabilityException capability => $"Technical review could not invoke {capability.Capability} ({capability.Code}): {capability.Message}",
                _ => $"Technical review could not complete ({error.GetType().Name}); no review decision was accepted."
            };
            return AgentWorkResult.Success(ReviewBlockedOutcome(assignment, reason));
        }
    }
    internal static WorkExecutionOutcomeV1 ReviewBlockedOutcome(WorkExecutionAssignment assignment, string reason)
    {
        const string suffix = "… [Full review-processing error is retained in output.reviewProcessingError and diagnostics.]";
        var summary = reason.Length <= 4096 ? reason : reason[..(4096 - suffix.Length)] + suffix;
        return new(assignment.StageExecutionId, assignment.AttemptId,
            WorkExecutionDispositions.Blocked, "blocked", summary,
            JsonSerializer.SerializeToElement(new { reviewProcessingError = reason }), [], [reason]);
    }
}
