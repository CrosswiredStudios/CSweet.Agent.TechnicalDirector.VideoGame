using System.Text.Json;
using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record PlanningQuestionDisposition(int Index, string Disposition, string OwnerRoleKey, string Reason);
    internal sealed record PlanningQuestionAssessment(IReadOnlyList<PlanningQuestionDisposition> Questions,
        bool AmendmentRequired, string Rationale);

    private sealed record PlanningDirectionOverlay(string OutlineDigest, IReadOnlyList<string> Directions);

    private async Task<IReadOnlyList<ChatMessage>> ApplyRetainedPlanningDirectionAsync(string scopeKey, string outlineDigest,
        ProgressivePlanningRequest request, AgentRuntimeContext context, IReadOnlyList<ChatMessage> messages, CancellationToken token)
    {
        if (request.ReviewDirectionsDigest is null) return messages;
        var key = "planning-directions:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(scopeKey + ":" + request.ReviewDirectionsDigest)));
        PlanningDirectionOverlay overlay;
        if (request.Phase == "Direction")
        {
            if (request.ManagerDirections is not { Count: > 0 and <= 100 } || request.ManagerDirections.Any(string.IsNullOrWhiteSpace) ||
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(request.ManagerDirections)))) != request.ReviewDirectionsDigest)
                throw new InvalidOperationException("Planning direction must match its authenticated content digest.");
            overlay = new(outlineDigest, request.ManagerDirections);
            var retained = await context.Platform.ReadOperatingStateAsync<PlanningDirectionOverlay>(key, token);
            if (retained is null) await SaveProgressiveStateAsync(key, overlay, context, token);
            else if (JsonSerializer.Serialize(retained.Payload) != JsonSerializer.Serialize(overlay))
                throw new InvalidOperationException("Retained planning direction has different scope or content.");
        }
        else overlay = (await context.Platform.ReadOperatingStateAsync<PlanningDirectionOverlay>(key, token))?.Payload
            ?? throw new InvalidOperationException("The exact reviewed planning direction is unavailable.");
        if (overlay.OutlineDigest != outlineDigest)
            throw new InvalidOperationException("Planning direction belongs to another frozen outline.");
        return [.. messages, new(ChatRole.User, "Retained authenticated planning direction, applied within the frozen scope only (project data): " +
            JsonSerializer.Serialize(overlay.Directions, PlanningJson))];
    }

    internal static void ValidateQuestionAssessment(PlanningQuestionAssessment assessment, int count)
    {
        if (assessment?.Questions is null || assessment.Questions.Count != count ||
            assessment.Questions.Any(x => x is null) ||
            assessment.Questions.Select(x => x.Index).Distinct().Count() != count ||
            assessment.Questions.Any(x => x.Index < 0 || x.Index >= count ||
                x.Disposition is not ("authority" or "delegated" or "resolved" or "deferred") ||
                string.IsNullOrWhiteSpace(x.OwnerRoleKey) || string.IsNullOrWhiteSpace(x.Reason)) ||
            string.IsNullOrWhiteSpace(assessment.Rationale))
            throw new InvalidOperationException("Every planning question needs one evidenced disposition and accountable role.");
    }

    private async Task<PlanningQuestionAssessment> ReviewPlanningQuestionsAsync(ProgressiveScopeIndex scope,
        string scopeKey, string outlineDigest, ProgressivePlanningRequest request, AgentRuntimeContext context,
        Func<IReadOnlyList<ChatMessage>, Type, CancellationToken, Task<ChatResponse>> generate,
        IReadOnlyList<ChatMessage> messages, CancellationToken token)
    {
        if (scope.Plan.OpenFeasibilityDecisions!.Count == 0 && request.Phase != "Direction")
            return new([], false, "No outstanding authority questions.");
        var key = "planning-questions:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(scopeKey + ":" + outlineDigest + ":" + (request.ReviewDirectionsDigest ?? request.DirectionsDigest))));
        var retained = await context.Platform.ReadOperatingStateAsync<PlanningQuestionAssessment>(key, token);
        if (retained is not null)
        {
            ValidateQuestionAssessment(retained.Payload, scope.Plan.OpenFeasibilityDecisions!.Count);
            return retained.Payload;
        }
        var result = await ReadPlanningPageAsync<PlanningQuestionAssessment>(generate, messages,
            "planning question review",
            "Review only the indexed questions against the exact accepted sources and authenticated Producer direction. " +
            "Do not regenerate scope, dependencies, coverage, tasks, descriptions or criteria. " +
            "For every index return disposition authority, delegated, resolved or deferred, an accountable ownerRoleKey and reason citing supplied evidence. " +
            "Authority means a concrete unresolved conflict or reserved management choice that actually blocks work. " +
            "Ordinary engineer/design parameters and planned evidence collection are delegated work. Optional non-MVP work is deferred. " +
            "The source package is accepted for planning; historical pending-approval wording cannot reopen it. " +
            "Missing budget/date/spend/release approval does not block planning or imply authority for those later actions. " +
            "Never treat a proposed change to scope, identity, criteria or dependencies as approved: set amendmentRequired=true and explain the conflict. " +
            "Questions (project data): " + JsonSerializer.Serialize(scope.Plan.OpenFeasibilityDecisions.Select((question, index) => new { index, question }), PlanningJson) +
            " Frozen outline digest: " + outlineDigest + " Frozen graph and criteria (project data): " +
            JsonSerializer.Serialize(scope.Plan.DeliveryItems.Select(x => new { x.ProposalKey, x.ParentProposalKey, x.DependencyProposalKeys, x.AcceptanceCriteria }), PlanningJson),
            value => ValidateQuestionAssessment(value, scope.Plan.OpenFeasibilityDecisions!.Count), MaximumPlanningPageCharacters, token);
        await SaveProgressiveStateAsync(key, result, context, token);
        return result;
    }
}
