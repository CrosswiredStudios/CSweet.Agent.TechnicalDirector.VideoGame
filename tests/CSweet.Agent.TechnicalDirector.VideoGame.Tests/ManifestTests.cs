using CSweet.WorkManagement.Contracts;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class ManifestTests
{
    [Fact]
    public async Task Manifest_IsValidAndMatchesAgent()
    {
        var root = RepositoryRoot();
        var path = Path.Combine(root, "csweet-plugin.json");

        var manifest = await AgentManifestLoader.LoadAsync(path, CancellationToken.None);
        var agent = new SpecialistAgent();

        foreach (var capability in new[] { GitMergeCapabilities.Review, GitMergeCapabilities.Authorize })
            Assert.Contains(manifest.Requires, x => x.Name == capability && x.Scope == "work-item");
        Assert.DoesNotContain(manifest.Requires, x => x.Name == GitWorkspaceCapabilities.Publish);
        Assert.Contains(manifest.Requires, x => x.Name == PlatformCapabilities.WorkstreamRead && x.Scope == "workstream");
        Assert.Contains(manifest.Requires, x => x.Name == "work.item.planning.revise.v1" && x.Scope == "board");
        foreach (var capability in new[] { PlatformCapabilities.ToolchainCatalogRead, PlatformCapabilities.BuildRequest,
            PlatformCapabilities.PreviewCreate, WorkItemCapabilities.Comment, WorkManagementCapabilityNames.SprintRead,
            WorkManagementCapabilityNames.OrchestrationRead })
            Assert.Contains(manifest.Requires, x => x.Name == capability);
        foreach (var name in new[] { "work.item.read", "work.item.comment" })
            Assert.Contains(manifest.Requires, x => x.Name == name && x.Scope == "team");
        Assert.Equal(agent.AgentId, manifest.Id);
        Assert.Equal(agent.Version, manifest.Version);
        Assert.Contains(agent.PrimaryCapability, manifest.Capabilities);
        Assert.Empty(VideoGameSpecialistConformance.ValidateManifest(
            path, agent.AgentId, agent.DeclaredRoleKey, agent.PrimaryCapability));
        Assert.True(VideoGameSpecialistConformance.StateKeysAreIsolated(
            agent.DeclaredRoleKey, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        Assert.True(File.Exists(Path.Combine(
            root,
            manifest.Runtime.ProjectPath!.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               (!File.Exists(Path.Combine(directory.FullName, "csweet-plugin.json")) ||
                !Directory.Exists(Path.Combine(directory.FullName, "src"))))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
