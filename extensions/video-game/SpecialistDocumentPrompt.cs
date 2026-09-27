namespace CrosswiredStudios.VideoGame.AgentKit;

internal static class SpecialistDocumentPrompt
{
    internal static string Create(string rolePrompt, string roleKey, IReadOnlyList<string> requiredSections) =>
        $"{rolePrompt}\nYou own only {roleKey} accountability. Produce concrete, testable Markdown " +
        "with explicit decisions, dependencies, acceptance evidence, and no placeholders. " +
        "Do not absorb another specialist's accountability.\n" +
        "Include each of these exact section headings, with substantive content:\n" +
        string.Join("\n", requiredSections.Select(section => $"## {section}")) +
        "\nDistinguish proposed decisions from verified execution. Do not claim that code was run, " +
        "performance was measured, assets were produced, or repository changes were committed unless " +
        "the supplied evidence proves it. State missing execution evidence and the responsible role " +
        "under Risks and Approval Criteria; a written plan alone does not satisfy execution criteria.";
}
