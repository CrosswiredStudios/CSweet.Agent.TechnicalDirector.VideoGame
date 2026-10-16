using Microsoft.Extensions.AI;
using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

internal static class ImplementationBrief
{
    internal const string Guidance = """
        Own the implementation design in each engineering ticket's description. Split recursively:
        if a task can produce two independently deliverable, verifiable changes, make two tasks.
        A dependency between the changes does not justify combining them. Keep the tests needed to
        prove a change with that change; do not create empty bookkeeping tasks. Never combine scope
        to meet a preferred ticket count. When a contract limit is reached, report the planning limit
        rather than hide multiple deliverables inside one ticket.
        Use these nonempty sections in every engineering task description:
        Outcome: one observable deliverable, its boundary and what is out of scope.
        Context: relevant repository paths, types, members, interfaces and existing behavior to update.
        Distinguish verified symbols from proposed locations; never invent repository inspection.
        Pseudocode: concrete ordered steps, state/data flow, conditions and relevant failure handling.
        Assumptions: prerequisite results and API/data assumptions the developer must recheck against
        the current workspace, including what evidence would invalidate them.
        Verification: focused checks and expected results proving this deliverable.
        Keep each section concise but actionable; a one-sentence description is not enough.
        Developers must read current ticket comments before work. For stale assumptions, they request
        a revision in comments and pause. A Technical Director correction explicitly identifies the
        superseded guidance and supplies replacement context, pseudocode, assumptions and checks.
        Comments may supersede implementation guidance within accepted scope, not waive acceptance,
        independent review, security or authority. If the deliverable changes substantially, recommend
        replacing the ticket: the Producer closes it as superseded/cancelled, never completed, creates
        smaller linked replacement tickets, and repairs dependencies and sprint readiness first.
        """;

    internal static IReadOnlyList<string> Issues(string? description)
    {
        var lines = (description ?? "").Replace("\r", "").Split('\n');
        string[] sections = ["Outcome:", "Context:", "Pseudocode:", "Assumptions:", "Verification:"];
        var issues = new List<string>();
        foreach (var section in sections)
        {
            var index = Array.FindIndex(lines, line => line.TrimStart().StartsWith(section, StringComparison.Ordinal));
            if (index < 0) { issues.Add($"description needs a nonempty {section} section"); continue; }
            var content = lines[index].TrimStart()[section.Length..].Trim();
            var following = lines.Skip(index + 1).TakeWhile(line => !sections.Any(s => line.TrimStart().StartsWith(s, StringComparison.Ordinal)));
            if (string.IsNullOrWhiteSpace(content) && !following.Any(line => !string.IsNullOrWhiteSpace(line)))
                issues.Add($"description needs a nonempty {section} section");
        }
        return issues;
    }
}

// The SDK owns the lightweight planning protocol; keep role-specific authoring policy local.
internal sealed class ImplementationPlanningClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var guided = messages.Append(new ChatMessage(ChatRole.System, ImplementationBrief.Guidance)).ToList();
        var response = await base.GetResponseAsync(guided, options, cancellationToken);
        try
        {
            var plan = JsonSerializer.Deserialize<ProjectDeliveryPlan>(SpecialistAgent.UnwrapPlanningJson(response.Text),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (plan?.Items is null) return response; // The SDK corrects schema/graph failures.
            var issues = plan.Items.SelectMany(x => ImplementationBrief.Issues(x.Description).Select(issue => $"{x.Key}: {issue}")).ToArray();
            if (issues.Length == 0) return response;
            guided.Add(new(ChatRole.Assistant, response.Text));
            guided.Add(new(ChatRole.User, "Correct these implementation briefs without changing accepted scope or combining deliverables: " + string.Join("; ", issues)));
            return await base.GetResponseAsync(guided, options, cancellationToken);
        }
        catch (JsonException) { return response; }
    }
}
