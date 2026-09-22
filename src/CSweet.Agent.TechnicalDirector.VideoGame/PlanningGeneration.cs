using System.Text.Json;
using CrosswiredStudios.VideoGame.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static GameProposedWorkItemV1 NormalizeCoreRoleSkills(GameProposedWorkItemV1 item)
    {
        if (item.AccountableRoleKey is not (VideoGameRoleKeys.Engineer or VideoGameRoleKeys.QualityAssurance) ||
            item.RequiredSpecializationKeys is null || item.PreferredSpecializationKeys is null)
            return item;
        return item with
        {
            RequiredSpecializationKeys = [],
            PreferredSpecializationKeys = item.RequiredSpecializationKeys
                .Concat(item.PreferredSpecializationKeys).Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    internal static async Task<(PlanningOutput? Output, string? Error)> GeneratePlanningAsync(
        Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>> generate,
        IReadOnlyList<ChatMessage> initialMessages, CancellationToken cancellationToken)
    {
        var messages = initialMessages.ToList();
        string issue = "No proposal returned.";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await generate(messages, cancellationToken);
            try
            {
                var output = JsonSerializer.Deserialize<PlanningOutput>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (output?.DeliveryItems is { } items)
                {
                    output = output with { DeliveryItems = items.Select(NormalizeCoreRoleSkills).ToArray() };
                    if (IsValidPlan(output.DeliveryItems)) return (output, null);
                }
                issue = "The plan must contain an Epic > Story > Task hierarchy with separate engineer and QA tasks, testable criteria, unique keys, allowed roles/types/skills, and resolvable acyclic parents and dependencies.";
            }
            catch (JsonException exception)
            {
                issue = $"JSON does not match the requested contract at {exception.Path ?? "$"}, line {exception.LineNumber}, byte {exception.BytePositionInLine}. Return one complete JSON object; deliveryItems is an array of objects and all three findings/constraints/decisions fields are arrays of strings.";
            }
            if (attempt == 0)
            {
                messages.Add(new(ChatRole.Assistant, text));
                messages.Add(new(ChatRole.User, $"Correct your proposal: {issue} Preserve the accepted project scope and all required work. Return the complete corrected proposal, without Markdown or commentary."));
            }
        }
        return (null, $"Technical planning could not produce a valid proposal after two attempts. {issue}");
    }
}