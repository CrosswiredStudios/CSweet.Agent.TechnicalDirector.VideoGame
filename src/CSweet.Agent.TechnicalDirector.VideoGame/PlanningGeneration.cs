using System.Text.Json;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
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
                if (output is not null && IsValidPlan(output.DeliveryItems)) return (output, null);
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