using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using CrosswiredStudios.VideoGame.AgentKit;
using CrosswiredStudios.VideoGame.Contracts;
using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal const int PlanningPageSize = 3;
    internal const int MaximumPlanningPageCharacters = 128_000;
    internal const string PlanningGeneratorFailure = "[csweet.planning.generator]";
    private static readonly JsonSerializerOptions PlanningJson = CreatePlanningJson();
    internal sealed record PlanningDescription(string ProposalKey, string Description);
    internal sealed record PlanningDescriptionPage(IReadOnlyList<PlanningDescription> Items);
    internal sealed record PlanningCheckpoint(string InputDigest, PlanningOutput Outline,
        IReadOnlyDictionary<string, string> Descriptions);
    private sealed record PlanningBriefCheckpoint(string OutlineDigest, IReadOnlyDictionary<string, string> Descriptions);

    private static JsonSerializerOptions CreatePlanningJson()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        json.MakeReadOnly(populateMissingResolver: true);
        return json;
    }

    private async Task<(PlanningOutput? Output, string? Error)> GenerateCheckpointedPlanningAsync(
        AgentRuntimeContext context, IChatClient client, IReadOnlyList<ChatMessage> messages, CancellationToken token)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            messages.Select(x => new { role = x.Role.ToString(), text = x.Text }))));
        var key = "technical-planning-pages:" + digest;
        var saved = await context.Platform.ReadOperatingStateAsync<PlanningCheckpoint>(key, token);
        var retained = saved?.Payload;
        var indexPersisted = retained is not null;
        var persistedPages = new HashSet<int>();
        if (retained is not null)
        {
            var outlineDigest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(retained.Outline)));
            var descriptions = new Dictionary<string, string>();
            var pages = retained.Outline.DeliveryItems.Where(x => !IsPlanningContainer(x)).Chunk(PlanningPageSize).ToArray();
            for (var index = 0; index < pages.Length; index++)
            {
                var page = await context.Platform.ReadOperatingStateAsync<PlanningBriefCheckpoint>($"{key}:page:{index}", token);
                if (page is null) continue;
                if (page.Payload.OutlineDigest != outlineDigest ||
                    !page.Payload.Descriptions.Keys.ToHashSet().SetEquals(pages[index].Select(x => x.ProposalKey)))
                    throw new InvalidOperationException("The retained brief page does not match its frozen scope index.");
                foreach (var (proposalKey, description) in page.Payload.Descriptions) descriptions.Add(proposalKey, description);
                persistedPages.Add(index);
            }
            retained = retained with { Descriptions = descriptions };
        }
        return await GeneratePlanningPagesAsync(async (pageMessages, type, ct) =>
        {
            var options = ResponseOptions(PlanningOutputTokens);
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema(
                PlanningResponseSchema(type), "planning_page");
            return await client.GetResponseAsync(pageMessages, options, ct);
        }, messages, digest, retained, async (checkpoint, ct) =>
        {
            // Operating-state payloads are bounded to 128,000 characters. Keep the index
            // separate from each three-task brief page instead of growing one record.
            var indexCheckpoint = checkpoint with { Descriptions = new Dictionary<string, string>() };
            if (!indexPersisted)
            {
                await new RevisionSafeProjectState(context.Platform).MergeAsync<PlanningCheckpoint>(key,
                    "video-game.technical-planning-pages.v1", 1, current => MergePlanningCheckpoint(current, indexCheckpoint),
                    new Dictionary<string, string> { ["input"] = digest }, "planning-index:" + digest, ct);
                indexPersisted = true;
            }
            var outlineDigest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(checkpoint.Outline)));
            var pages = checkpoint.Outline.DeliveryItems.Where(x => !IsPlanningContainer(x)).Chunk(PlanningPageSize).ToArray();
            for (var index = 0; index < pages.Length; index++)
            {
                if (persistedPages.Contains(index) || !pages[index].All(x => checkpoint.Descriptions.ContainsKey(x.ProposalKey))) continue;
                var payload = new PlanningBriefCheckpoint(outlineDigest, pages[index].ToDictionary(x => x.ProposalKey, x => checkpoint.Descriptions[x.ProposalKey]));
                var pageKey = $"{key}:page:{index}";
                await new RevisionSafeProjectState(context.Platform).MergeAsync<PlanningBriefCheckpoint>(pageKey,
                    "video-game.technical-planning-briefs.v1", 1, current => current is null || JsonSerializer.Serialize(current) == JsonSerializer.Serialize(payload)
                        ? payload : throw new InvalidOperationException("A retained brief page changed during concurrent planning."),
                    new Dictionary<string, string> { ["outline"] = outlineDigest }, "planning-brief:" + digest + ":" + index, ct);
                persistedPages.Add(index);
            }
        }, token);
    }

    internal static PlanningCheckpoint MergePlanningCheckpoint(PlanningCheckpoint? current, PlanningCheckpoint next)
    {
        if (current is null) return next;
        if (current.InputDigest != next.InputDigest || JsonSerializer.Serialize(current.Outline) != JsonSerializer.Serialize(next.Outline))
            throw new InvalidOperationException("The planning checkpoint belongs to different inputs or a different accepted outline.");
        var descriptions = current.Descriptions.ToDictionary();
        foreach (var (key, value) in next.Descriptions)
        {
            if (descriptions.TryGetValue(key, out var retained) && retained != value)
                throw new InvalidOperationException("Concurrent planning changed a validated implementation brief.");
            descriptions[key] = value;
        }
        return next with { Descriptions = descriptions };
    }

    internal static async Task<(PlanningOutput? Output, string? Error)> GeneratePlanningPagesAsync(
        Func<IReadOnlyList<ChatMessage>, Type, CancellationToken, Task<ChatResponse>> generate,
        IReadOnlyList<ChatMessage> initialMessages, string inputDigest, PlanningCheckpoint? saved,
        Func<PlanningCheckpoint, CancellationToken, Task> save, CancellationToken token)
    {
        var grounded = initialMessages.Append(new ChatMessage(ChatRole.System, PlanningConstructionGuidance())).ToArray();
        var briefMessages = initialMessages.Append(new ChatMessage(ChatRole.System, ImplementationBrief.Guidance +
            "\nCurrent phase: descriptions for existing tasks only. Preserve the frozen scope and identities; return only the requested descriptions.")).ToArray();
        try
        {
            if (saved is not null && saved.InputDigest != inputDigest)
                throw new InvalidOperationException("The planning checkpoint input binding changed.");
            var outline = saved?.Outline ?? await ReadPlanningPageAsync<PlanningOutput>(generate, grounded,
                "compact scope index", "Return the COMPLETE scope index, including every milestone, story and independently deliverable task. " +
                "Preserve every accepted requirement and criterion, role, key, parent and dependency. " +
                "This phase supersedes the request for detailed implementation briefs: descriptions are concise scope summaries only; " +
                "do not include pseudocode yet. Detailed briefs will be generated in separate small pages. " +
                "Do not reduce scope or combine independent tasks to fit an item count. Return the PlanningOutput contract as JSON.",
                value => {
                    ValidatePlanningOutline(value);
                    if (JsonSerializer.Serialize(new PlanningCheckpoint(inputDigest, value, new Dictionary<string, string>())).Length > MaximumPlanningPageCharacters)
                        throw new InvalidOperationException("The compact scope index exceeds durable-state bounds. Remove repeated wording without dropping requirements or tasks.");
                }, MaximumPlanningPageCharacters, token);
            outline = outline with { DeliveryItems = outline.DeliveryItems.Select(NormalizeCoreRoleSkills).ToArray() };
            ValidatePlanningOutline(outline);
            var descriptions = saved?.Descriptions.ToDictionary() ?? new Dictionary<string, string>();
            var leaves = outline.DeliveryItems.Where(x => !IsPlanningContainer(x)).ToArray();
            if (descriptions.Any(x => !leaves.Any(leaf => leaf.ProposalKey == x.Key) || string.IsNullOrWhiteSpace(x.Value)))
                throw new InvalidOperationException("The retained planning page contains an unrelated or empty brief.");
            // Freeze the complete index before generating expensive details. No partial index
            // or unvalidated response is a proposal and no tickets are published here.
            await save(new(inputDigest, outline, descriptions.ToDictionary()), token);
            foreach (var page in leaves.Where(x => !descriptions.ContainsKey(x.ProposalKey)).Chunk(PlanningPageSize))
            {
                token.ThrowIfCancellationRequested();
                var keys = page.Select(x => x.ProposalKey).ToHashSet(StringComparer.Ordinal);
                var response = await ReadPlanningPageAsync<PlanningDescriptionPage>(generate, briefMessages,
                    "implementation briefs " + string.Join(", ", keys),
                    "Return ONLY {\"items\":[{\"proposalKey\":\"...\",\"description\":\"...\"}]} for exactly these tasks. " +
                    "Do not return or change the scope index, titles, roles, criteria, parents or dependencies. " +
                    "Engineering descriptions must include Outcome, Context, Pseudocode, Assumptions, Verification. " +
                    "Other roles need actionable role-appropriate instructions. Preserve all criteria and role boundaries. " +
                    "Existing identity/dependency ledger: " + JsonSerializer.Serialize(outline.DeliveryItems.Select(x =>
                        new { x.ProposalKey, x.Title, x.WorkItemTypeKey, x.ParentProposalKey, x.DependencyProposalKeys, x.AccountableRoleKey }), PlanningJson) +
                    "\nTasks on this page: " + JsonSerializer.Serialize(page, PlanningJson),
                    value =>
                    {
                        if (value.Items is null || value.Items.Any(x => x is null || string.IsNullOrWhiteSpace(x.Description)) ||
                            value.Items.Count != keys.Count || !value.Items.Select(x => x.ProposalKey).ToHashSet().SetEquals(keys))
                            throw new InvalidOperationException("Return exactly one nonempty description for each requested proposalKey, without duplicates or extra tasks.");
                        var issues = EngineeringBriefIssues(page.Select(item => item with {
                            Description = value.Items.Single(x => x.ProposalKey == item.ProposalKey).Description }).ToArray());
                        if (issues.Count > 0) throw new InvalidOperationException(string.Join("; ", issues));
                    }, MaximumPlanningPageCharacters, token);
                foreach (var item in response.Items) descriptions[item.ProposalKey] = item.Description;
                await save(new(inputDigest, outline, descriptions.ToDictionary()), token);
            }
            var output = outline with { DeliveryItems = outline.DeliveryItems.Select(x => IsPlanningContainer(x)
                ? x : x with { Description = descriptions[x.ProposalKey] }).ToArray() };
            ValidatePlanningOutline(output);
            var finalIssues = EngineeringBriefIssues(output.DeliveryItems);
            if (finalIssues.Count > 0) throw new InvalidOperationException(string.Join("; ", finalIssues));
            return (output, null);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return (null, "Technical planning could not produce a valid proposal after bounded page correction. " +
                "Validated pages are retained for the same accepted inputs; no partial backlog was published. " + error.Message);
        }
    }

    private static bool IsPlanningContainer(GameProposedWorkItemV1 item) => item.WorkItemTypeKey is
        VideoGameWorkItemTypeKeys.Milestone or VideoGameWorkItemTypeKeys.Feature or VideoGameWorkItemTypeKeys.Content;

    private static void ValidatePlanningOutline(PlanningOutput output)
    {
        _ = BindDependencyPins(output);
        var issues = PlanValidationIssues(output.DeliveryItems);
        if (issues.Count > 0) throw new InvalidOperationException(string.Join("; ", issues));
        if (output.FeasibilityFindings is null || output.TechnicalConstraints is null || output.OpenFeasibilityDecisions is null ||
            output.FeasibilityFindings.Concat(output.TechnicalConstraints).Concat(output.OpenFeasibilityDecisions).Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Findings, constraints and decisions must be arrays of nonblank strings; use [] when empty.");
    }

    private static async Task<T> ReadPlanningPageAsync<T>(
        Func<IReadOnlyList<ChatMessage>, Type, CancellationToken, Task<ChatResponse>> generate,
        IReadOnlyList<ChatMessage> grounded, string phase, string instruction, Action<T> validate,
        int maximumCharacters, CancellationToken token)
    {
        string? issue = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var messages = grounded.Append(new ChatMessage(ChatRole.System, instruction)).ToList();
            if (issue is not null) messages.Add(new(ChatRole.User, "Correct only this page: " + issue +
                ". Return one complete JSON object. Preserve all requested tasks and accepted scope."));
            var response = await generate(messages, typeof(T), token);
            try
            {
                if (response.FinishReason == ChatFinishReason.Length) throw new JsonException("The page exhausted its output budget.");
                if (response.Text.Length > maximumCharacters) throw new JsonException($"The page exceeds {maximumCharacters} characters; omit repetition, never scope.");
                using var document = JsonDocument.Parse(UnwrapPlanningJson(response.Text));
                RequireUniquePlanningProperties(document.RootElement);
                var value = document.RootElement.Deserialize<T>(PlanningJson) ?? throw new JsonException("The page is empty.");
                validate(value);
                return value;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            {
                issue = $"{phase} (finish={response.FinishReason?.ToString() ?? "unknown"}, characters={response.Text.Length}): {error.Message}";
            }
        }
        throw new InvalidOperationException("Page failed after three attempts: " + issue);
    }

    private static void RequireUniquePlanningProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate planning property: " + property.Name);
                RequireUniquePlanningProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RequireUniquePlanningProperties(item);
    }
}
