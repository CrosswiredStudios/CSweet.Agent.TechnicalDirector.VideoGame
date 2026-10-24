using System.Text.Json;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class PlanningSchemaRootTests
{
    [Theory]
    [InlineData(typeof(SpecialistAgent.OutlineManifest))]
    [InlineData(typeof(SpecialistAgent.OutlineDraftPage))]
    [InlineData(typeof(SpecialistAgent.OutlineCoverageDraft))]
    [InlineData(typeof(SpecialistAgent.OutlineMetadataCorrections))]
    [InlineData(typeof(SpecialistAgent.PlanningOutput))]
    [InlineData(typeof(SpecialistAgent.ProgressiveStoryIndex))]
    public void Planning_response_requires_a_nonnull_object_at_the_provider_boundary(Type responseType)
    {
        var schema = SpecialistAgent.PlanningResponseSchema(responseType);
        // OpenAI-compatible structured response providers reject nullable root type arrays.
        Assert.Equal(JsonValueKind.String, schema.GetProperty("type").ValueKind);
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.TryGetProperty("properties", out _));
    }
}
