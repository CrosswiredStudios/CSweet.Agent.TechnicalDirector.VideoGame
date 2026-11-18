using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Agent.SDK.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static System.Text.Json.Nodes.JsonObject PlanningProposalPayload(
        CSweet.Agent.SDK.VideoGame.Contracts.GameTechnicalDeliveryProposalV1 proposal)
    {
        var payload = JsonSerializer.SerializeToNode(proposal)!.AsObject();
        // Authority to work through an open decision comes from an explicit scoped decision.
        payload["engineeringPrerequisiteKeys"] = new System.Text.Json.Nodes.JsonArray();
        return payload;
    }

    internal sealed record DependencyPin(string Package, string ExactVersion);

    internal static PlanningOutput BindDependencyPins(PlanningOutput output)
    {
        if (output.DependencyPins.Count > 16 || output.DependencyPins.Any(x => x is null || string.IsNullOrWhiteSpace(x.Package) ||
            x.Package.Length > 100 || !Regex.IsMatch(x.ExactVersion ?? "", @"\A\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\z")) ||
            output.DependencyPins.Select(x => x.Package).Distinct(StringComparer.OrdinalIgnoreCase).Count() != output.DependencyPins.Count)
            throw new InvalidOperationException("dependencyPins requires unique package names and concrete exact versions; ranges and future kickoff actions are unresolved.");
        var items = output.DeliveryItems.ToList();
        if (output.DependencyPins.Count > 0)
        {
            var pins = JsonSerializer.Serialize(output.DependencyPins);
            var pinInstruction = "\nConcrete dependency pins (dependency-pins.v1): " + pins + ". Use these project versions. The setup owner verifies the restore and records any lockfile required by the chosen workflow.";
            items = items.Select(x => x.AccountableRoleKey == VideoGameRoleKeys.Engineer && x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Task
                && !x.Description.Contains(pinInstruction, StringComparison.Ordinal)
                ? x with { Description = x.Description + pinInstruction } : x).ToList();
        }
        return output with { DeliveryItems = items };
    }
}
