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
        IReadOnlyList<GameProposedWorkItemV1>? acceptedContainers = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await generate(messages, cancellationToken);
            try
            {
                var output = JsonSerializer.Deserialize<PlanningOutput>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (output?.DeliveryItems is { } items)
                {
                    output = output with { DeliveryItems = items.Select(NormalizeCoreRoleSkills).ToArray() };
                    acceptedContainers ??= ValidContainerOutline(output.DeliveryItems);
                    if (acceptedContainers is not null && !PreservesContainerOutline(output.DeliveryItems, acceptedContainers))
                        issue = "The accepted milestone and story outline changed. Restore every accepted container key, title, scope, criterion, parent, and dependency; repair only the executable leaves.";
                    else if (IsValidPlan(output.DeliveryItems)) return (output, null);
                    else
                        issue = "The plan must contain an Epic > Story > Task hierarchy with separate engineer and QA tasks, testable criteria, unique keys, allowed roles/types/skills, and resolvable acyclic parents and dependencies.";
                }
                else issue = "The plan must contain deliveryItems.";
            }
            catch (JsonException exception)
            {
                issue = $"JSON does not match the requested contract at {exception.Path ?? "$"}, line {exception.LineNumber}, byte {exception.BytePositionInLine}. Return one complete JSON object; deliveryItems is an array of objects and all three findings/constraints/decisions fields are arrays of strings.";
            }
            if (attempt == 0)
            {
                messages.Add(new(ChatRole.Assistant, text));
                messages.Add(new(ChatRole.User, Correction(issue, acceptedContainers)));
            }
            else if (attempt == 1)
            {
                // Do not feed two large malformed responses back into the model. A fresh,
                // compact pass is bounded and still must satisfy the full plan validator.
                messages = initialMessages.ToList();
                messages.Add(new(ChatRole.User, Correction(issue, acceptedContainers, compact: true)));
            }
        }
        return (null, $"Technical planning could not produce a valid proposal after three attempts. {issue}");
    }

    private static string Correction(string issue, IReadOnlyList<GameProposedWorkItemV1>? accepted, bool compact = false) =>
        $"Correct your proposal: the prior response did not validate ({issue}). " +
        (accepted is null
            ? "No container outline was accepted; regenerate a lean Epic > Story > Task hierarchy from the accepted scope. "
            : $"Accepted container outline (immutable): {JsonSerializer.Serialize(accepted)} Repair only the executable leaves; do not rename, remove, reorder, or replace these containers. ") +
        $"Return one complete JSON object{(compact ? " with at most 20 concise deliveryItems" : "")}, including separate testable engineer and QA tasks. " +
        "Preserve all accepted deliverables, do not invent approvals, and add no Markdown or commentary.";

    internal static IReadOnlyList<GameProposedWorkItemV1>? ValidContainerOutline(
        IReadOnlyList<GameProposedWorkItemV1> items)
    {
        var containers = items.Where(x => x.WorkItemTypeKey is VideoGameWorkItemTypeKeys.Milestone or
            VideoGameWorkItemTypeKeys.Feature or VideoGameWorkItemTypeKeys.Content).ToList();
        if (containers.Count == 0 || !containers.Any(x => x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Milestone) ||
            !containers.Any(x => x.WorkItemTypeKey is VideoGameWorkItemTypeKeys.Feature or VideoGameWorkItemTypeKeys.Content))
            return null;
        var keys = containers.Select(x => x.ProposalKey).ToHashSet(StringComparer.Ordinal);
        var roles = Constants(typeof(VideoGameRoleKeys));
        var skills = Constants(typeof(VideoGameSpecializationKeys));
        if (keys.Count != containers.Count || containers.Any(x => string.IsNullOrWhiteSpace(x.ProposalKey) ||
            string.IsNullOrWhiteSpace(x.Title) || string.IsNullOrWhiteSpace(x.Description) ||
            x.AcceptanceCriteria is null || x.AcceptanceCriteria.Count == 0 || x.AcceptanceCriteria.Any(string.IsNullOrWhiteSpace) ||
            !roles.Contains(x.AccountableRoleKey) || x.RequiredSpecializationKeys is null ||
            x.RequiredSpecializationKeys.Any(key => !skills.Contains(key)) || x.PreferredSpecializationKeys is null ||
            x.PreferredSpecializationKeys.Any(key => !skills.Contains(key)) || x.RequiredCapabilityKeys is null ||
            x.RequiredCapabilityKeys.Any(key => key != "work.execution.run.v1") ||
            x.DependencyProposalKeys is null || x.DependencyProposalKeys.Any(key => !keys.Contains(key)) ||
            x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Milestone && x.ParentProposalKey is not null ||
            x.WorkItemTypeKey != VideoGameWorkItemTypeKeys.Milestone &&
                (x.ParentProposalKey is null || !keys.Contains(x.ParentProposalKey))))
            return null;
        var byKey = containers.ToDictionary(x => x.ProposalKey, StringComparer.Ordinal);
        if (containers.Any(x => x.WorkItemTypeKey != VideoGameWorkItemTypeKeys.Milestone &&
                byKey[x.ParentProposalKey!].WorkItemTypeKey != VideoGameWorkItemTypeKeys.Milestone))
            return null;
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        while (resolved.Count < containers.Count)
        {
            var ready = containers.Where(x => !resolved.Contains(x.ProposalKey) &&
                x.DependencyProposalKeys.All(resolved.Contains) &&
                (x.ParentProposalKey is null || resolved.Contains(x.ParentProposalKey))).ToList();
            if (ready.Count == 0) return null;
            foreach (var item in ready) resolved.Add(item.ProposalKey);
        }
        return containers;
    }

    internal static bool PreservesContainerOutline(
        IReadOnlyList<GameProposedWorkItemV1> items,
        IReadOnlyList<GameProposedWorkItemV1> accepted)
    {
        var current = ValidContainerOutline(items);
        if (current is null || current.Count != accepted.Count) return false;
        var byKey = current.ToDictionary(x => x.ProposalKey, StringComparer.Ordinal);
        return accepted.All(x => byKey.TryGetValue(x.ProposalKey, out var candidate) &&
            JsonSerializer.Serialize(x) == JsonSerializer.Serialize(candidate));
    }
}
