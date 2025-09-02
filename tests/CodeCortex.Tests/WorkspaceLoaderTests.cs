using System.Threading.Tasks;
using Xunit;
using CodeCortex.Workspace;
using System.IO;

namespace CodeCortex.Tests;

public class WorkspaceLoaderTests {
    private static string GetSolutionPath() {
        var baseDir = AppContext.BaseDirectory; // .../tests/CodeCortex.Tests/bin/Debug/net9.0/
        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Atelia.sln"))) {
            dir = dir.Parent;
        }
        if (dir == null) {
            throw new FileNotFoundException("Cannot locate Atelia.sln from test base directory");
        }

        return Path.Combine(dir.FullName, "Atelia.sln");
    }

    [Fact]
    public async Task MsBuildForce_LoadsMultipleProjectsAsync() {
        var sln = GetSolutionPath();
        var loader = new MsBuildWorkspaceLoader(MsBuildMode.Force);
        var loaded = await loader.LoadAsync(sln);
        Assert.True(loaded.Projects.Count >= 2); // solution has multiple projects
    }

    [Fact]
    public async Task Fallback_YieldsSingleSyntheticProjectAsync() {
        var sln = GetSolutionPath();
        var loader = new MsBuildWorkspaceLoader(MsBuildMode.Fallback);
        var loaded = await loader.LoadAsync(sln);
        Assert.Single(loaded.Projects); // synthetic project
    }
}
