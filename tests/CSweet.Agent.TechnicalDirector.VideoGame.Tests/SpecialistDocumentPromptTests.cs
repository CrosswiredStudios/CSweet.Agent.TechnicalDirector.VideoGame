using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class SpecialistDocumentPromptTests
{
    [Fact]
    public void GenerationReceivesEveryRequiredSectionAndEvidenceBoundary()
    {
        string[] sections = ["Architecture", "Toolchain Feasibility", "Performance Budgets", "Technical Standards", "Risks", "Approval Criteria"];
        var prompt = SpecialistDocumentPrompt.Create("Own engine feasibility.", "game-technical-director", sections);
        foreach (var section in sections) Assert.Contains($"## {section}", prompt);
        Assert.Contains("game-technical-director accountability", prompt);
        Assert.Contains("unless the supplied evidence proves it", prompt);
        Assert.Contains("a written plan alone does not satisfy execution criteria", prompt);
    }
}
