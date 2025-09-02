using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis;

namespace CodeCortex.Workspace;

public interface IWorkspaceLoader
{
    Task<LoadedSolution> LoadAsync(string entryPath, CancellationToken ct = default);
}

public sealed record LoadedSolution(Solution Solution, IReadOnlyList<Project> Projects);

public sealed class MsBuildWorkspaceLoader : IWorkspaceLoader
{
    public async Task<LoadedSolution> LoadAsync(string entryPath, CancellationToken ct = default)
    {
        var workspace = MSBuildWorkspace.Create();
        Solution solution;
        if (entryPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            solution = await workspace.OpenSolutionAsync(entryPath, cancellationToken: ct);
        }
        else if (entryPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var project = await workspace.OpenProjectAsync(entryPath, cancellationToken: ct);
            solution = project.Solution;
        }
        else
        {
            throw new ArgumentException("Entry path must be a .sln or .csproj", nameof(entryPath));
        }
        return new LoadedSolution(solution, solution.Projects.ToList());
    }
}
