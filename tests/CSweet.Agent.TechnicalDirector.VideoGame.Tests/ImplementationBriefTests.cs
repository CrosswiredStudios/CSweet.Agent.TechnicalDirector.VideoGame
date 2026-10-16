using System.Text.Json;
using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;
using TicketConversations;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class ImplementationBriefTests
{
    [Theory]
    [InlineData("Context:")]
    [InlineData("Pseudocode:")]
    [InlineData("Assumptions:")]
    [InlineData("Verification:")]
    public async Task Missing_engineering_guidance_is_corrected_before_a_proposal_is_accepted(string section)
    {
        var plan = SpecialistAgent.PlanningContractExample();
        var bad = plan with { DeliveryItems = plan.DeliveryItems.Select(x => x.ProposalKey == "example-implementation"
            ? x with { Description = x.Description.Replace(section, "Omitted:") } : x).ToArray() };
        var calls = 0;
        var result = await SpecialistAgent.GeneratePlanningAsync((messages, _) => {
            if (calls++ > 0) Assert.Contains(section, messages.Last().Text);
            return Task.FromResult(JsonSerializer.Serialize(calls == 1 ? bad : plan));
        }, [], default);
        Assert.Equal(2, calls);
        Assert.NotNull(result.Output);
    }

    [Fact]
    public void Empty_headings_cannot_pass_brief_validation() =>
        Assert.Equal(5, ImplementationBrief.Issues("Outcome:\nContext:\nPseudocode:\nAssumptions:\nVerification:").Count);

    [Theory]
    [InlineData("continue")]
    [InlineData("replace")]
    [InlineData("clarify")]
    public void Revision_decisions_are_explicit_readable_comments(string decision)
    {
        var body = TaskRevisionComments.Format(JsonSerializer.Serialize(new TaskRevisionComments.Reply(decision,
            "Original lookup call", "Update Controller.Tick", "read current state; update position",
            "State API exists; check current implementation", "Run movement tests", "The previous task changed the state API.")));
        Assert.True(TaskRevisionComments.HasDecision(body, $"[task-revision:v1:{decision}]"));
        Assert.Contains("Supersedes: Original lookup call", body);
        Assert.Contains("Pseudocode: read current state", body);
    }

    [Theory]
    [InlineData("continue", "")]
    [InlineData("unknown", "updated pseudocode")]
    [InlineData("replace", "[task-revision:v1:continue]")]
    public void Incomplete_or_ambiguous_corrections_cannot_be_persisted(string decision, string pseudocode) =>
        Assert.Throws<InvalidOperationException>(() => TaskRevisionComments.Format(JsonSerializer.Serialize(
            new TaskRevisionComments.Reply(decision, "old guidance", "Controller", pseudocode, "assumptions", "tests", "reason"))));

    [Fact]
    public async Task Lightweight_planning_receives_the_same_sizing_and_implementation_policy()
    {
        using var client = new ImplementationPlanningClient(new InspectClient());
        await client.GetResponseAsync([new(ChatRole.User, "Approved project")]);
    }

    private sealed class InspectClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var policy = messages.Last();
            Assert.Equal(ChatRole.System, policy.Role);
            Assert.Contains("two independently deliverable", policy.Text);
            Assert.Contains("Pseudocode:", policy.Text);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
