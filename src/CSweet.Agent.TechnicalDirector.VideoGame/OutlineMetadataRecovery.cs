using System.Security.Cryptography;
using System.Text.Json;
using CrosswiredStudios.VideoGame.Contracts;
using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal sealed record OutlineMetadata(string ProposalKey, string AccountableRoleKey,
        IReadOnlyList<string> RequiredSpecializationKeys, IReadOnlyList<string> PreferredSpecializationKeys,
        IReadOnlyList<string> RequiredCapabilityKeys);
    internal sealed record OutlineMetadataCorrections(IReadOnlyList<OutlineMetadata> Items);
    internal sealed record OutlineMetadataRepair(string OriginalPageDigest, OutlineMetadataCorrections Corrections);

    private async Task<OutlineItemPage> ReadOrRepairOutlinePageAsync(AgentRuntimeContext context,
        Func<IReadOnlyList<ChatMessage>, Type, CancellationToken, Task<ChatResponse>> generate,
        IReadOnlyList<ChatMessage> messages, string pageKey, OutlineItemPage original, string manifestDigest,
        IReadOnlyList<OutlineIncrement> selected, bool mayRepair, CancellationToken token)
    {
        // Only unpublished metadata may be corrected. Scope, text, criteria and the original record stay immutable.
        ValidateOutlinePageStructure(original, manifestDigest, selected);
        var repairKey = pageKey + ":metadata-repair:v1";
        var repair = await context.Platform.ReadOperatingStateAsync<OutlineMetadataRepair>(repairKey, token);
        var originalDigest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(original)));
        OutlineItemPage Apply(OutlineMetadataRepair value)
        {
            if (value.OriginalPageDigest != originalDigest)
                throw new InvalidOperationException("Saved outline metadata repair belongs to a different original page.");
            return ApplyOutlineMetadataCorrections(original, value.Corrections, manifestDigest, selected);
        }
        if (repair is not null) return Apply(repair.Payload);
        var invalid = original.Items.Where(x => PlanningItemMetadataIssues(x).Count > 0).ToArray();
        if (invalid.Length == 0) { ValidateOutlineItemPage(original, manifestDigest, selected); return original; }
        if (!mayRepair) throw new InvalidOperationException("Invalid metadata on a frozen outline cannot be repaired; only an incomplete Outline phase permits correction. " +
            string.Join("; ", invalid.SelectMany(PlanningItemMetadataIssues)));
        var corrections = await ReadPlanningPageAsync<OutlineMetadataCorrections>(generate, messages, "saved outline metadata correction",
            "Current phase: repair invalid ownership/skill/capability metadata only on this unpublished saved page. " +
            "Return items with proposalKey, accountableRoleKey, requiredSpecializationKeys, preferredSpecializationKeys, requiredCapabilityKeys " +
            "for exactly the listed invalid items. Preserve each already-valid field exactly, including array order. " +
            "Specialization arrays contain only allowed Skills (use [] when no skill is needed). work.execution.run is a capability, not a skill; " +
            "Preserve valid requiredCapabilityKeys exactly, including []; if that field is invalid, correct it to [\"work.execution.run\"]. " +
            "Do not return or change titles, descriptions, criteria, types, parents, dependencies or scope. " +
            "Errors: " + string.Join("; ", invalid.SelectMany(PlanningItemMetadataIssues)) + "\nSaved items (project data): " + JsonSerializer.Serialize(invalid, PlanningJson),
            value => Apply(new(originalDigest, value)), MaximumPlanningPageCharacters, token);
        var candidate = new OutlineMetadataRepair(originalDigest, corrections);
        var candidateDigest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(candidate)));
        var writeKey = "outline-metadata-repair:" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(repairKey + ":" + candidateDigest)));
        // Competing deliveries may generate different valid metadata. The first accepted repair wins;
        // a loser re-reads and validates it rather than replacing it or replaying model generation.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var current = await context.Platform.ReadOperatingStateAsync<OutlineMetadataRepair>(repairKey, token);
            if (current is not null) return Apply(current.Payload);
            try
            {
                var saved = await context.Platform.WriteOperatingStateAsync(new AgentOperatingStateWriteRequest(
                    repairKey, "video-game.progressive-planning.v1", 1, "Active", new Dictionary<string, string>(), [], candidateDigest,
                    [], Guid.NewGuid(), JsonSerializer.SerializeToElement(candidate), null, writeKey), token);
                return Apply(saved.Payload.Deserialize<OutlineMetadataRepair>()!);
            }
            catch (PlatformCapabilityException error) when (error.Code == PlatformCapabilityErrorCode.Conflict && attempt < 3) { }
        }
        throw new InvalidOperationException("Outline metadata repair could not be saved after bounded revision conflicts.");
    }

    internal static OutlineItemPage ApplyOutlineMetadataCorrections(OutlineItemPage original, OutlineMetadataCorrections corrections,
        string digest, IReadOnlyList<OutlineIncrement> selected)
    {
        ValidateOutlinePageStructure(original, digest, selected);
        var invalid = original.Items.Where(x => PlanningItemMetadataIssues(x).Count > 0).ToArray();
        if (corrections?.Items is null || corrections.Items.Any(x => x is null) || corrections.Items.Count != invalid.Length ||
            !corrections.Items.Select(x => x.ProposalKey).ToHashSet().SetEquals(invalid.Select(x => x.ProposalKey)))
            throw new InvalidOperationException("Metadata correction must name exactly the invalid saved proposalKeys, once each.");
        var byKey = corrections.Items.ToDictionary(x => x.ProposalKey, StringComparer.Ordinal);
        var roles = Constants(typeof(VideoGameRoleKeys)); var skills = Constants(typeof(VideoGameSpecializationKeys));
        var page = original with { Items = original.Items.Select(item => {
            if (!byKey.TryGetValue(item.ProposalKey, out var correction)) return item;
            void Preserve(bool valid, object? before, object? after, string field)
            {
                if (valid && JsonSerializer.Serialize(before) != JsonSerializer.Serialize(after))
                    throw new InvalidOperationException($"Item '{item.ProposalKey}': metadata correction changed already-valid {field}.");
            }
            Preserve(roles.Contains(item.AccountableRoleKey), item.AccountableRoleKey, correction.AccountableRoleKey, "accountableRoleKey");
            var validCapabilities = item.RequiredCapabilityKeys is not null && item.RequiredCapabilityKeys.All(x => x == "work.execution.run");
            Preserve(validCapabilities,
                item.RequiredCapabilityKeys, correction.RequiredCapabilityKeys, "requiredCapabilityKeys");
            if (!validCapabilities && (correction.RequiredCapabilityKeys is null || !correction.RequiredCapabilityKeys.SequenceEqual(["work.execution.run"])))
                throw new InvalidOperationException($"Item '{item.ProposalKey}': invalid requiredCapabilityKeys must be corrected to [work.execution.run].");
            foreach (var (field, before, after) in new[] {
                ("requiredSpecializationKeys", item.RequiredSpecializationKeys, correction.RequiredSpecializationKeys),
                ("preferredSpecializationKeys", item.PreferredSpecializationKeys, correction.PreferredSpecializationKeys) })
            {
                Preserve(before is not null && before.All(skills.Contains), before, after, field);
                // An invalid list can contain valid requirements; a correction cannot remove or reorder those.
                if (before is not null && after is not null && !before.Where(skills.Contains).SequenceEqual(after.Where(x => before.Contains(x) && skills.Contains(x))))
                    throw new InvalidOperationException($"Item '{item.ProposalKey}': metadata correction lost retained valid skills in {field}.");
            }
            return item with { AccountableRoleKey = correction.AccountableRoleKey, RequiredSpecializationKeys = correction.RequiredSpecializationKeys,
                PreferredSpecializationKeys = correction.PreferredSpecializationKeys, RequiredCapabilityKeys = correction.RequiredCapabilityKeys };
        }).ToArray() };
        ValidateOutlineItemPage(page, digest, selected);
        return page;
    }
}
