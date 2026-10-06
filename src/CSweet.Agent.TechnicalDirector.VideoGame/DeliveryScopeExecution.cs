using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    protected override Task<AgentWorkResult> ExecuteDeliveryScopeAsync(WorkExecutionAssignmentV2 assignment,
        AgentRuntimeContext context, CancellationToken ct) => DeliveryScopeReview.ExecuteAsync(assignment, context,
        context.CreateChatClient(new AgentLlmSelection(Settings.GetGuid("llmProviderId") ??
            throw new InvalidOperationException("Configure a technical readiness provider."), Settings.GetString("llmModel"))), ct);
}
