using System.Text.Json;
using System.Text.RegularExpressions;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static System.Text.Json.Nodes.JsonObject PlanningProposalPayload(
        CrosswiredStudios.VideoGame.Contracts.GameTechnicalDeliveryProposalV1 proposal)
    {
        var payload = JsonSerializer.SerializeToNode(proposal)!.AsObject();
        // This declaration is derived from our deterministic task binder, not question prose.
        // Keep the original decisions and frozen outline digest unchanged.
        payload["engineeringPrerequisiteKeys"] = JsonSerializer.SerializeToNode(proposal.DeliveryItems
            .Where(x => IsPhaserPinDeliverable(x) &&
                x.AccountableRoleKey == VideoGameRoleKeys.Engineer && x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Task &&
                x.DependencyProposalKeys.Count == 0 && proposal.DeliveryItems.Any(other => other.DependencyProposalKeys.Contains(x.ProposalKey)))
            .Select(x => x.ProposalKey).ToArray());
        return payload;
    }

    internal sealed record DependencyPin(string Package, string ExactVersion);

    // A narrow recognition rule reuses an explicitly scoped existing deliverable. Never merge
    // tickets, change their criteria, or guess from a generic engine/version mention.
    internal static bool IsPhaserPinDeliverable(GameProposedWorkItemV1 item) =>
        item.ProposalKey == "dependency-pin-phaser" ||
        item.Title.Contains("Phaser", StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(item.Title, @"\block(?:file)?\b", RegexOptions.IgnoreCase) &&
        Regex.IsMatch(item.Title, @"\b(?:select|pin|commit|lock)\b", RegexOptions.IgnoreCase) &&
        item.AcceptanceCriteria.Any(x => x.Contains("exact", StringComparison.OrdinalIgnoreCase) &&
            x.Contains("version", StringComparison.OrdinalIgnoreCase)) &&
        item.AcceptanceCriteria.Any(x => x.Contains("lockfile", StringComparison.OrdinalIgnoreCase));

    private static bool UsesPhaser(GameProposedWorkItemV1 item) =>
        (item.Title + " " + item.Description + " " + string.Join(" ", item.AcceptanceCriteria)).Contains("Phaser", StringComparison.OrdinalIgnoreCase);

    internal static PlanningOutput BindDependencyPins(PlanningOutput output)
    {
        if (output.DependencyPins.Count > 16 || output.DependencyPins.Any(x => x is null || string.IsNullOrWhiteSpace(x.Package) ||
            x.Package.Length > 100 || !Regex.IsMatch(x.ExactVersion ?? "", @"\A\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\z")) ||
            output.DependencyPins.Select(x => x.Package).Distinct(StringComparer.OrdinalIgnoreCase).Count() != output.DependencyPins.Count)
            throw new InvalidOperationException("dependencyPins requires unique package names and concrete exact versions; ranges and future kickoff actions are unresolved.");
        var items = output.DeliveryItems.ToList();
        var engineers = items.Where(x => x.AccountableRoleKey == VideoGameRoleKeys.Engineer && x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Task).ToArray();
        if (engineers.Length == 0) return output;
        var owners = engineers.Where(IsPhaserPinDeliverable).ToArray();
        if (owners.Length > 1)
            throw new InvalidOperationException("Multiple Phaser pin/lockfile deliverables overlap. Reconcile the proposal before publication: retain one canonical owner and every accepted criterion, rewire its real consumers, and keep independent QA. Do not silently merge or cancel published work.");
        var mentionsPhaser = items.Any(x => (x.Title + " " + x.Description).Contains("Phaser", StringComparison.OrdinalIgnoreCase)) ||
            (output.TechnicalConstraints ?? []).Any(x => x.Contains("Phaser", StringComparison.OrdinalIgnoreCase));
        if (mentionsPhaser && !output.DependencyPins.Any(x => x.Package.Equals("phaser", StringComparison.OrdinalIgnoreCase)))
        {
            var existing = owners.SingleOrDefault() ?? items.SingleOrDefault(x => x.ProposalKey == "dependency-pin-phaser");
            // A future/global engine constraint alone does not create executable work in an
            // unrelated current increment. The pin is needed when an actual consumer is planned.
            if (existing is null && !engineers.Any(UsesPhaser)) return output;
            var key = existing?.ProposalKey ?? "dependency-pin-phaser";
            if (existing is not null && (existing.AccountableRoleKey != VideoGameRoleKeys.Engineer ||
                existing.WorkItemTypeKey != VideoGameWorkItemTypeKeys.Task || existing.DependencyProposalKeys is not { Count: 0 } ||
                existing.RequiredCapabilityKeys?.Contains("work.execution.run") != true ||
                existing.AcceptanceCriteria is not { Count: > 0 } || existing.AcceptanceCriteria.Any(string.IsNullOrWhiteSpace)))
                throw new InvalidOperationException("The canonical Phaser pin prerequisite must be a root game-engineer task with work.execution.run and testable acceptance criteria. The Director supplies the plan and review; engineering performs and commits the pin. Reconcile the task without weakening accepted scope.");
            if (existing is null)
            {
                var prerequisite = engineers[0] with { ProposalKey = key, Title = "Commit the exact Phaser dependency pin and lockfile",
                    Description = "Resolve the exact Phaser version within the approved engine constraints. Reuse an existing approved exact pin when present. Commit package.json and its lockfile, restore dependencies and verify the resolved installed version. If the approved engine constraints conflict, request the actual missing decision. This is executable engineering work; no human kickoff is required and the pin is unresolved until this task is integrated.",
                    AcceptanceCriteria = ["An exact Phaser version and reproducible lockfile are committed and integrated.", "A focused executed check proves the installed Phaser version matches the committed pin."],
                    DependencyProposalKeys = [] };
                items.Add(prerequisite);
            }
            // With an existing explicit owner, its declared dependency graph already owns sequencing.
            // A synthesized prerequisite gates only consumers that actually name Phaser, rather than
            // serializing every engineering task (including unrelated assets and tooling).
            if (existing is null)
                items = items.Select(x => x.AccountableRoleKey == VideoGameRoleKeys.Engineer && x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Task && x.ProposalKey != key &&
                    UsesPhaser(x)
                    ? x with { DependencyProposalKeys = x.DependencyProposalKeys.Append(key).Distinct(StringComparer.Ordinal).ToArray() } : x).ToList();
        }
        if (output.DependencyPins.Count > 0)
        {
            var pins = JsonSerializer.Serialize(output.DependencyPins);
            var pinInstruction = "\nConcrete dependency pins (dependency-pins.v1): " + pins + ". Commit and verify these exact versions and the lockfile; do not defer selection to a human kickoff.";
            items = items.Select(x => x.AccountableRoleKey == VideoGameRoleKeys.Engineer && x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Task
                && !x.Description.Contains(pinInstruction, StringComparison.Ordinal)
                ? x with { Description = x.Description + pinInstruction } : x).ToList();
        }
        return output with { DeliveryItems = items };
    }
}
