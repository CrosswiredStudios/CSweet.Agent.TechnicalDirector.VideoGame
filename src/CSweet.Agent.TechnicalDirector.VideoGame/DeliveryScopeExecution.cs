using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    protected override Task<AgentWorkResult> ExecuteDeliveryScopeAsync(WorkExecutionAssignment assignment,
        AgentRuntimeContext context, CancellationToken ct) => assignment.Handler is "build-readiness" or "compute-test" || assignment.StageKey == "build-readiness"
        ? DeliveryBuildReadiness.ExecuteAsync(assignment, context, ct)
        : DeliveryScopeReview.ExecuteAsync(assignment, context,
        context.CreateChatClient(new AgentLlmSelection(Settings.GetGuid("llmProviderId") ??
            throw new InvalidOperationException("Configure a technical readiness provider."), Settings.GetString("llmModel"))), ct);
}
