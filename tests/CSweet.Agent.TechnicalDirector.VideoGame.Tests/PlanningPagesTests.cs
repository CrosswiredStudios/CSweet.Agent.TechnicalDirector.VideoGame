using System.Text.Json;
using CSweet.Agent.TechnicalDirector.VideoGame;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class PlanningPagesTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static ChatResponse Response<T>(T value) => new(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(value, Json))) { FinishReason = ChatFinishReason.Stop };
    private static SpecialistAgent.PlanningOutput Outline()
    {
        var example = SpecialistAgent.PlanningContractExample();
        return example with { DeliveryItems = [.. example.DeliveryItems, .. Enumerable.Range(0, 5).Select(index =>
            example.DeliveryItems[2] with { ProposalKey = "extra-" + index, Title = "Required deliverable " + index,
                AcceptanceCriteria = ["Distinct accepted criterion " + index] })] };
    }
    private static SpecialistAgent.PlanningDescriptionPage Page(IEnumerable<string> keys) => new(keys.Select(key =>
        new SpecialistAgent.PlanningDescription(key, SpecialistAgent.PlanningContractExample().DeliveryItems[2].Description)).ToArray());

    [Theory]
    [InlineData("50d197a2-6b53-4216-85ac-865a3caef2fc", "{\"deliveryItems\":[{requiredSpecializationKeys\":[]}")]
    [InlineData("ed0b8184-e3fc-448b-b7e6-8949a48a23d4", "{\"deliveryItems\":[{\"requiredCapabilityKeys\":")]
    [InlineData("ee2e6cc1-2e1b-4ba7-a5e9-146a9a0d35f9", "{\"deliveryItems\":[{\"title\":\"Pro")]
    public async Task Captured_incomplete_responses_retry_only_the_failed_page_and_preserve_every_task(string capture, string synthetic)
    {
        // Optional retained incident fixtures stay outside the package/repository: the same
        // regression runs against the full originals when diagnosing an authorized live case.
        var root = Environment.GetEnvironmentVariable("CSWEET_PLANNING_CAPTURE_ROOT");
        var malformed = root is null ? synthetic : await File.ReadAllTextAsync(Path.Combine(root, capture + ".json"));
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(malformed));
        var outline = Outline();
        var first = outline.DeliveryItems.Skip(2).Take(3).Select(x => x.ProposalKey).ToArray();
        var saved = new SpecialistAgent.PlanningCheckpoint("inputs", outline,
            Page(first).Items.ToDictionary(x => x.ProposalKey, x => x.Description));
        var checkpoint = saved;
        var calls = 0;
        var pending = outline.DeliveryItems.Skip(2).Where(x => !saved.Descriptions.ContainsKey(x.ProposalKey)).ToArray();
        var result = await SpecialistAgent.GeneratePlanningPagesAsync((messages, type, _) =>
        {
            Assert.Equal(typeof(SpecialistAgent.PlanningDescriptionPage), type);
            Assert.DoesNotContain(messages, x => x.Text?.Contains("Construct the proposal in this order") == true);
            Assert.Contains(messages, x => x.Text?.Contains("Current phase: descriptions for existing tasks only") == true);
            Assert.DoesNotContain(messages, x => x.Role == ChatRole.Assistant); // never echo a huge malformed response
            calls++;
            if (calls == 1) return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, malformed)) { FinishReason = ChatFinishReason.Stop });
            var page = pending.Skip((calls - 2) * SpecialistAgent.PlanningPageSize).Take(SpecialistAgent.PlanningPageSize);
            if (calls == 2) Assert.Contains("Correct only this page", messages.Last().Text);
            return Task.FromResult(Response(Page(page.Select(x => x.ProposalKey))));
        }, [new(ChatRole.User, "Retain the complete accepted game scope")], "inputs", saved,
            (value, _) => { checkpoint = SpecialistAgent.MergePlanningCheckpoint(checkpoint, value); return Task.CompletedTask; }, default);
        Assert.NotNull(result.Output);
        Assert.Equal(outline.DeliveryItems.Select(x => x.ProposalKey), result.Output.DeliveryItems.Select(x => x.ProposalKey));
        Assert.Equal(outline.DeliveryItems.Select(x => JsonSerializer.Serialize(x.AcceptanceCriteria)),
            result.Output.DeliveryItems.Select(x => JsonSerializer.Serialize(x.AcceptanceCriteria)));
        Assert.Equal(7, checkpoint.Descriptions.Count);
        Assert.Equal(3, calls);
        Assert.Empty(SpecialistAgent.PlanValidationIssues(result.Output.DeliveryItems));
        Assert.Empty(SpecialistAgent.EngineeringBriefIssues(result.Output.DeliveryItems));
    }

    [Fact]
    public async Task Saved_pages_survive_cancellation_and_resume_without_regeneration()
    {
        var outline = Outline();
        SpecialistAgent.PlanningCheckpoint? checkpoint = null;
        using var cancel = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => SpecialistAgent.GeneratePlanningPagesAsync((_, type, _) =>
        {
            calls++;
            return Task.FromResult(type == typeof(SpecialistAgent.PlanningOutput) ? Response(outline) :
                Response(Page(outline.DeliveryItems.Skip(2).Take(3).Select(x => x.ProposalKey))));
        }, [], "inputs", null, (value, _) => { checkpoint = value; if (value.Descriptions.Count == 3) cancel.Cancel(); return Task.CompletedTask; }, cancel.Token));
        Assert.Equal(2, calls);
        Assert.Equal(3, checkpoint!.Descriptions.Count);
        var remaining = outline.DeliveryItems.Skip(2).Where(x => !checkpoint.Descriptions.ContainsKey(x.ProposalKey)).ToArray();
        calls = 0;
        var result = await SpecialistAgent.GeneratePlanningPagesAsync((_, type, _) =>
        {
            Assert.Equal(typeof(SpecialistAgent.PlanningDescriptionPage), type);
            return Task.FromResult(Response(Page(remaining.Skip(calls++ * 3).Take(3).Select(x => x.ProposalKey))));
        }, [], "inputs", checkpoint, (value, _) => Task.CompletedTask, default);
        Assert.NotNull(result.Output); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Exhausted_page_is_blocked_without_publishing_or_discarding_prior_pages()
    {
        var saved = new SpecialistAgent.PlanningCheckpoint("inputs", Outline(), new Dictionary<string, string>());
        var calls = 0; var saves = 0;
        var result = await SpecialistAgent.GeneratePlanningPagesAsync((_, _, _) =>
        { calls++; return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"items\":[")) { FinishReason = ChatFinishReason.Length }); },
            [], "inputs", saved, (_, _) => { saves++; return Task.CompletedTask; }, default);
        Assert.Null(result.Output); Assert.Equal(3, calls); Assert.Equal(1, saves);
        Assert.Contains("finish=length", result.Error); Assert.Contains("no partial backlog was published", result.Error);
    }

    [Fact]
    public async Task Changed_inputs_cannot_consume_a_retained_plan()
    {
        var calls = 0;
        var result = await SpecialistAgent.GeneratePlanningPagesAsync((_, _, _) => { calls++; throw new Exception(); },
            [], "changed", new("original", Outline(), new Dictionary<string, string>()), (_, _) => Task.CompletedTask, default);
        Assert.Null(result.Output); Assert.Equal(0, calls); Assert.Contains("input binding changed", result.Error);
    }

    [Fact]
    public void Concurrent_checkpoint_merge_preserves_pages_and_rejects_replaced_scope_or_briefs()
    {
        var current = new SpecialistAgent.PlanningCheckpoint("inputs", Outline(), new Dictionary<string, string> { ["a"] = "A" });
        var next = current with { Descriptions = new Dictionary<string, string> { ["b"] = "B" } };
        Assert.Equal(2, SpecialistAgent.MergePlanningCheckpoint(current, next).Descriptions.Count);
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.MergePlanningCheckpoint(current, next with { InputDigest = "changed" }));
        Assert.Throws<InvalidOperationException>(() => SpecialistAgent.MergePlanningCheckpoint(current, next with {
            Descriptions = new Dictionary<string, string> { ["a"] = "different" } }));
    }
}
