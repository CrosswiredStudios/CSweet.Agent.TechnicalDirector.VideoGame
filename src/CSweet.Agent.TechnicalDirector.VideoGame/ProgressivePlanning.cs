using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using CrosswiredStudios.VideoGame.AgentKit;
using CrosswiredStudios.VideoGame.Contracts;
using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record ProgressivePlanningRequest(string Phase, string DirectionsDigest, string? ScopeKey = null,
        string? OutlineDigest = null, string? StoryKey = null, IReadOnlyList<string>? DetailedStoryKeys = null);
    internal sealed record ScopeCoverage(string SourceKey, IReadOnlyList<string> ProposalKeys);
    internal sealed record ScopeSource(string SourceKey, string Text);
    internal sealed record ProgressiveScopeIndex(PlanningOutput Plan, IReadOnlyList<ScopeCoverage> Coverage);
    internal sealed record ProgressiveStoryIndex(IReadOnlyList<GameProposedWorkItemV1> Items, IReadOnlyList<ScopeCoverage> Coverage);
    private sealed record ProgressiveBriefPage(string IndexDigest, IReadOnlyDictionary<string, string> Descriptions);

    internal static string ProgressiveScopeKey(GameProductionPlanningCycleV1 cycle, string directionsDigest) =>
        "technical-progressive:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            cycle.WorkstreamId, cycle.TeamId, cycle.BoardId, cycle.ProfileDigest, cycle.ApprovedPackageId,
            cycle.ApprovedPackageVersion, cycle.ApprovedPackageDigest, cycle.PlanningFingerprint, directionsDigest })));

    internal static IReadOnlyList<ScopeSource> AcceptedScopeSources(IReadOnlyList<string> documents) => documents.SelectMany((text, document) =>
        text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((paragraph, index) => new ScopeSource($"document-{document}:paragraph-{index}", paragraph))).ToArray();

    internal static void ValidateScopeCoverage(IReadOnlyList<ScopeCoverage> coverage, IReadOnlyList<ScopeSource> sources,
        IReadOnlySet<string> keys)
    {
        if (coverage is null || coverage.Any(x => x is null))
            throw new InvalidOperationException("coverage must be an array of nonnull source mappings.");
        var expected = sources.Select(x => x.SourceKey).ToHashSet(StringComparer.Ordinal);
        var supplied = coverage.Select(x => x.SourceKey).ToHashSet(StringComparer.Ordinal);
        var issues = new List<string>();
        void List(string label, IEnumerable<string> values)
        {
            var bounded = values.Distinct(StringComparer.Ordinal).Take(20).ToArray();
            if (bounded.Length > 0) issues.Add(label + ": " + JsonSerializer.Serialize(bounded));
        }
        List("Missing sourceKeys on THIS page", expected.Except(supplied));
        List("Unexpected sourceKeys outside THIS page (omit them)", supplied.Except(expected));
        List("Duplicate sourceKeys (return each once)", coverage.GroupBy(x => x.SourceKey).Where(x => x.Count() > 1).Select(x => x.Key));
        List("Source mappings with no owning Story/task", coverage.Where(x => x.ProposalKeys is not { Count: > 0 }).Select(x => x.SourceKey));
        List("Unknown proposalKeys (use the frozen ledger)", coverage.Where(x => x.ProposalKeys is not null)
            .SelectMany(x => x.ProposalKeys).Where(key => !keys.Contains(key)));
        if (issues.Count > 0)
            throw new InvalidOperationException("Every exact accepted source on this page must map once to retained scope. " + string.Join("; ", issues));
    }

    internal static void ValidateProgressiveOutline(ProgressiveScopeIndex outline, IReadOnlyList<ScopeSource> sources)
    {
        if (outline?.Plan?.DeliveryItems is not { Count: > 0 } items || items.Any(x => x is null || !IsPlanningContainer(x)))
            throw new InvalidOperationException("The complete scope outline needs valid Epic and Story containers only; future executable tasks are not generated upfront.");
        var issues = items.SelectMany(PlanningItemMetadataIssues).ToArray();
        if (issues.Length > 0) throw new InvalidOperationException(string.Join("; ", issues));
        if (ValidContainerOutline(items)?.Count != items.Count)
            throw new InvalidOperationException("The scope outline has invalid identities, hierarchy or dependencies.");
        var stories = items.Where(x => x.WorkItemTypeKey != VideoGameWorkItemTypeKeys.Milestone).Select(x => x.ProposalKey).ToHashSet();
        if (items.Where(x => x.WorkItemTypeKey == VideoGameWorkItemTypeKeys.Milestone).Any(epic => items.Count(x => x.ParentProposalKey == epic.ProposalKey) != 1))
            throw new InvalidOperationException("Each increment Epic must contain exactly one bounded Story. Split larger milestones into independently deliverable increments without removing scope.");
        ValidateScopeCoverage(outline.Coverage, sources, stories);
        if (outline.Plan.FeasibilityFindings is null || outline.Plan.TechnicalConstraints is null || outline.Plan.OpenFeasibilityDecisions is null)
            throw new InvalidOperationException("Planning findings, constraints and decisions must be arrays.");
    }

    internal static IReadOnlyList<ScopeSource> StoryScopeSources(GameProposedWorkItemV1 story) =>
        [new("description", story.Description), .. story.AcceptanceCriteria.Select((text, index) => new ScopeSource("criterion-" + index, text))];

    internal static void ValidateStoryRefinement(ProgressiveStoryIndex index, GameProposedWorkItemV1 story,
        PlanningOutput outline, IReadOnlyList<GameProposedWorkItemV1> priorLeaves)
    {
        if (index.Items is not { Count: > 0 and <= 12 } || index.Items.Any(x => x is null || IsPlanningContainer(x) || x.ParentProposalKey != story.ProposalKey))
            throw new InvalidOperationException("Refine exactly the requested Story into at most twelve small executable tasks; retain all other scope for later increments.");
        var known = outline.DeliveryItems.Concat(priorLeaves).Select(x => x.ProposalKey).ToHashSet();
        if (index.Items.Any(x => known.Contains(x.ProposalKey))) throw new InvalidOperationException("A refined task cannot replace an existing scope identity.");
        var executableKeys = priorLeaves.Concat(index.Items).Select(x => x.ProposalKey).ToHashSet();
        if (index.Items.Any(x => x.DependencyProposalKeys.Any(key => !executableKeys.Contains(key))))
            throw new InvalidOperationException("Increment tasks may depend only on current or retained executable tasks, never an unrefined future container.");
        var sources = StoryScopeSources(story);
        ValidateScopeCoverage(index.Coverage, sources, index.Items.Select(x => x.ProposalKey).ToHashSet());
        foreach (var source in sources.Where(x => x.SourceKey.StartsWith("criterion-", StringComparison.Ordinal)))
            if (!index.Coverage.Single(x => x.SourceKey == source.SourceKey).ProposalKeys.Any(key =>
                index.Items.Single(x => x.ProposalKey == key).AcceptanceCriteria.Contains(source.Text, StringComparer.Ordinal)))
                throw new InvalidOperationException("Each original Story criterion must survive verbatim on at least one mapped execution/QA task.");
        var issues = PlanValidationIssues(outline.DeliveryItems.Concat(priorLeaves).Concat(index.Items).ToArray());
        if (issues.Count > 0) throw new InvalidOperationException(string.Join("; ", issues));
    }

    internal async Task<AgentCoordinationTurnResult> GenerateProgressivePlanningAsync(AgentRuntimeContext context,
        IChatClient client, IReadOnlyList<ChatMessage> messages, IReadOnlyList<string> documents,
        GameProductionPlanningCycleV1 cycle, ProgressivePlanningRequest request, CancellationToken token)
    {
        try
        {
            if (request is null || request.Phase is not ("Outline" or "Story") || request.DirectionsDigest.Length != 64)
                throw new InvalidOperationException("Unsupported or unbound progressive planning request.");
            var scopeKey = ProgressiveScopeKey(cycle, request.DirectionsDigest);
            if (request.ScopeKey is not null && request.ScopeKey != scopeKey) throw new InvalidOperationException("The accepted inputs or Producer directions changed.");
            async Task<ChatResponse> Generate(IReadOnlyList<ChatMessage> input, Type type, CancellationToken ct)
            {
                var options = ResponseOptions(PlanningOutputTokens);
                options.ResponseFormat = ChatResponseFormat.ForJsonSchema(PlanningResponseSchema(type), "progressive_planning");
                return await client.GetResponseAsync(input, options, ct);
            }
            var sources = AcceptedScopeSources(documents);
            messages = messages.Append(new ChatMessage(ChatRole.User, "Exact accepted source ledger (project data): " +
                JsonSerializer.Serialize(sources, PlanningJson))).ToArray();
            var scope = await ReadOrGenerateScopeOutlineAsync(context, Generate, messages, scopeKey, sources,
                request.Phase == "Outline", token);
            ValidateProgressiveOutline(scope, sources);
            var outlineDigest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(scope)));
            if (request.OutlineDigest is not null && request.OutlineDigest != outlineDigest) throw new InvalidOperationException("The frozen scope outline changed.");
            var detailed = (request.DetailedStoryKeys ?? []).ToHashSet(StringComparer.Ordinal);
            var stories = scope.Plan.DeliveryItems.Where(x => x.WorkItemTypeKey != VideoGameWorkItemTypeKeys.Milestone).ToDictionary(x => x.ProposalKey);
            if (detailed.Any(key => !stories.ContainsKey(key)) || request.Phase == "Story" && (request.StoryKey is null || !stories.ContainsKey(request.StoryKey)))
                throw new InvalidOperationException("Only Stories from the exact published outline can be refined.");
            var leaves = new List<GameProposedWorkItemV1>();
            foreach (var story in scope.Plan.DeliveryItems.Where(x => detailed.Contains(x.ProposalKey)))
                leaves.AddRange(await LoadCompletedProgressiveStoryAsync(scopeKey, outlineDigest, story.ProposalKey, context, token));
            if (request.Phase == "Story" && !detailed.Contains(request.StoryKey!))
            {
                var story = stories[request.StoryKey!];
                var indexKey = ProgressiveStoryKey(scopeKey, story.ProposalKey);
                var retained = await context.Platform.ReadOperatingStateAsync<ProgressiveStoryIndex>(indexKey, token);
                var index = retained?.Payload ?? await ReadPlanningPageAsync<ProgressiveStoryIndex>(Generate, messages, "next increment task index",
                    "Refine ONLY the requested Story into a concise dependency-closed index of small tasks. Return items and coverage. " +
                    "Do not detail or recreate future Stories, milestones or existing tasks. Descriptions are short scope summaries; " +
                    "implementation briefs follow in separate pages. Include independent QA for the work that needs validation. " +
                    "Every original Story criterion must appear verbatim in at least one mapped task's acceptanceCriteria. Map its " +
                    "description and every criterion sourceKey exactly once in coverage. Dependencies may name tasks in this response " +
                    "or the existing prior task ledger, never tasks in unrefined future Stories. Requested Story and sources: " +
                    JsonSerializer.Serialize(new { story, sources = StoryScopeSources(story) }, PlanningJson) +
                    "\nExisting identity/dependency ledger: " + JsonSerializer.Serialize(scope.Plan.DeliveryItems.Concat(leaves).Select(x =>
                        new { x.ProposalKey, x.Title, x.ParentProposalKey, x.WorkItemTypeKey }), PlanningJson),
                    value => ValidateStoryRefinement(value, story, scope.Plan, leaves), MaximumPlanningPageCharacters, token);
                index = index with { Items = index.Items.Select(NormalizeCoreRoleSkills).ToArray() };
                ValidateStoryRefinement(index, story, scope.Plan, leaves);
                await SaveProgressiveStateAsync(indexKey, index, context, token);
                var indexDigest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(index)));
                var descriptions = leaves.ToDictionary(x => x.ProposalKey, x => x.Description);
                var pages = index.Items.Chunk(PlanningPageSize).ToArray();
                var persisted = new HashSet<int>();
                for (var number = 0; number < pages.Length; number++)
                {
                    var page = await context.Platform.ReadOperatingStateAsync<ProgressiveBriefPage>($"{indexKey}:page:{number}", token);
                    if (page is null) continue;
                    if (page.Payload.IndexDigest != indexDigest || !page.Payload.Descriptions.Keys.ToHashSet().SetEquals(pages[number].Select(x => x.ProposalKey)))
                        throw new InvalidOperationException("A retained increment page belongs to a different task index.");
                    foreach (var (key, text) in page.Payload.Descriptions) descriptions.Add(key, text);
                    persisted.Add(number);
                }
                var plan = scope.Plan with { DeliveryItems = [.. scope.Plan.DeliveryItems, .. leaves, .. index.Items] };
                var generated = await GeneratePlanningPagesAsync(Generate, messages,
                    indexDigest, new(indexDigest, plan, descriptions), async (checkpoint, ct) => {
                        for (var number = 0; number < pages.Length; number++)
                        {
                            if (persisted.Contains(number) || !pages[number].All(x => checkpoint.Descriptions.ContainsKey(x.ProposalKey))) continue;
                            await SaveProgressiveStateAsync($"{indexKey}:page:{number}", new ProgressiveBriefPage(indexDigest,
                                pages[number].ToDictionary(x => x.ProposalKey, x => checkpoint.Descriptions[x.ProposalKey])), context, ct);
                            persisted.Add(number);
                        }
                    }, token);
                if (generated.Output is null) return AgentCoordinationTurnResult.Blocked(PlanningGeneratorFailure + " C-Sweet technical issue: " + generated.Error);
                leaves = generated.Output.DeliveryItems.Where(x => !IsPlanningContainer(x)).ToList();
                var completed = index.Items.Select(x => x with { Description = leaves.Single(leaf => leaf.ProposalKey == x.ProposalKey).Description }).ToArray();
                await SaveProgressiveStateAsync(indexKey + ":completed", new CompletedProgressiveStory(outlineDigest, indexDigest,
                    completed.Select(x => x.ProposalKey).ToArray()), context, token);
                detailed.Add(story.ProposalKey);
            }
            var output = scope.Plan with { DeliveryItems = [.. scope.Plan.DeliveryItems, .. leaves] };
            var result = CompletePlanningProposal(cycle, output);
            var payload = JsonNode.Parse(result.Artifact!.Payload.GetRawText())!.AsObject();
            payload["progressivePlanning"] = JsonSerializer.SerializeToNode(new { phase = request.Phase, scopeKey, outlineDigest,
                directionsDigest = request.DirectionsDigest, detailedStoryKeys = detailed.Order().ToArray(), scopeCoverage = scope.Coverage }, PlanningJson);
            return result with { Content = detailed.Count == 0 ? "Complete approved scope outline ready for publication; execution detail is deferred to the next increment." :
                "The requested increment has validated briefs. Future Stories remain visible scope and have not been detailed or marked complete.",
                Artifact = result.Artifact with { Payload = JsonSerializer.SerializeToElement(payload) } };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return AgentCoordinationTurnResult.Blocked(PlanningGeneratorFailure + " C-Sweet technical issue: progressive planning could not generate or validate its output. " +
                "Approved scope and validated pages are retained. Escalate through the reporting manager to the CEO for a C-Sweet code correction; no creative decision is requested. Diagnostic: " + error.Message);
        }
    }

    private sealed record CompletedProgressiveStory(string OutlineDigest, string IndexDigest, IReadOnlyList<string> ProposalKeys);
    private static string ProgressiveStoryKey(string scopeKey, string story) => scopeKey + ":story:" +
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(story)))[..16];
    private static async Task<IReadOnlyList<GameProposedWorkItemV1>> LoadCompletedProgressiveStoryAsync(string scopeKey, string outlineDigest,
        string story, AgentRuntimeContext context, CancellationToken token)
    {
        var saved = await context.Platform.ReadOperatingStateAsync<CompletedProgressiveStory>(ProgressiveStoryKey(scopeKey, story) + ":completed", token);
        if (saved?.Payload.OutlineDigest != outlineDigest)
            throw new InvalidOperationException("A claimed detailed Story has no matching validated saved increment.");
        var indexKey = ProgressiveStoryKey(scopeKey, story);
        var index = await context.Platform.ReadOperatingStateAsync<ProgressiveStoryIndex>(indexKey, token);
        if (index is null || Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(index.Payload))) != saved.Payload.IndexDigest ||
            !saved.Payload.ProposalKeys.SequenceEqual(index.Payload.Items.Select(x => x.ProposalKey)))
            throw new InvalidOperationException("The saved increment's task index changed.");
        var items = new List<GameProposedWorkItemV1>(); var number = 0;
        foreach (var page in index.Payload.Items.Chunk(PlanningPageSize))
        {
            var brief = await context.Platform.ReadOperatingStateAsync<ProgressiveBriefPage>($"{indexKey}:page:{number++}", token);
            if (brief?.Payload.IndexDigest != saved.Payload.IndexDigest || !brief.Payload.Descriptions.Keys.ToHashSet().SetEquals(page.Select(x => x.ProposalKey)))
                throw new InvalidOperationException("A completed increment is missing its exact validated brief page.");
            items.AddRange(page.Select(x => x with { Description = brief.Payload.Descriptions[x.ProposalKey] }));
        }
        if (items.Any(x => x.ParentProposalKey != story) || EngineeringBriefIssues(items).Count > 0)
            throw new InvalidOperationException("A completed increment has invalid retained implementation briefs.");
        return items;
    }
    private static Task<AgentOperatingState<T>> SaveProgressiveStateAsync<T>(string key, T value, AgentRuntimeContext context, CancellationToken token) where T : class =>
        new RevisionSafeProjectState(context.Platform).MergeAsync<T>(key, "video-game.progressive-planning.v1", 1,
            current => current is null || JsonSerializer.Serialize(current) == JsonSerializer.Serialize(value) ? value :
                throw new InvalidOperationException("Progressive planning cannot replace validated scope or a saved increment."),
            new Dictionary<string, string>(), "progressive-state:" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))), token);
}
