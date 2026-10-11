using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;
using TicketConversations;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    private Task<IChatClient> DiscussionClientAsync(AgentRuntimeContext context, CancellationToken token) =>
        Task.FromResult(context.CreateChatClient(new AgentLlmSelection(
            Settings.GetGuid("llmProviderId") ?? throw new InvalidOperationException("Configure a discussion provider."), Settings.GetString("llmModel"))));

    private Task RecoverDiscussionAsync(AgentRuntimeContext context, CancellationToken token) =>
        Discussion.RecoverAsync(context, ct => DiscussionClientAsync(context, ct), token);
    public override async Task HandleEventAsync(AgentEventEnvelope message, AgentRuntimeContext context, CancellationToken token)
    {
        if (message.EventType == Discussion.Changed)
        {
            await Discussion.HandleAsync(message, context, ct => DiscussionClientAsync(context, ct), token);
            return;
        }
        if (message.EventType is CSweet.Agent.SDK.Compute.ComputeEvents.Changed or CSweet.Agent.SDK.Compute.ComputeEvents.Available or CSweet.WorkManagement.Contracts.WorkDeliveryCapabilities.Changed)
        {
            await HandleAttentionReviewAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "StateChanged"), context, token);
            return;
        }
        await base.HandleEventAsync(message, context, token);
    }
}

