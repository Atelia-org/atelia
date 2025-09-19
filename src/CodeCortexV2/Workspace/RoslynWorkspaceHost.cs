using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Atelia.Diagnostics; // DebugUtil

namespace CodeCortexV2.Workspace;

public sealed class RoslynWorkspaceHost {
    public MSBuildWorkspace Workspace { get; }

    private RoslynWorkspaceHost(MSBuildWorkspace workspace) {
        Workspace = workspace;
    }

    public static async Task<RoslynWorkspaceHost> LoadAsync(string solutionPath, CancellationToken ct) {
        // Ensure MSBuild registered
        if (!MSBuildLocator.IsRegistered) {
            var instance = MSBuildLocator.RegisterDefaults();
            DebugUtil.Print("Workspace", $"MSBuild registered: {instance.MSBuildPath}");
        }

        var ws = MSBuildWorkspace.Create();
        ws.WorkspaceFailed += (s, e) => DebugUtil.Print("Workspace", $"{e.Diagnostic.Kind}: {e.Diagnostic.Message}");
        DebugUtil.Print("Workspace", $"Loading solution: {solutionPath}");
        _ = await ws.OpenSolutionAsync(solutionPath, cancellationToken: ct).ConfigureAwait(false);
        DebugUtil.Print("Workspace", $"Solution loaded: Projects={ws.CurrentSolution.Projects.Count()}");
        return new RoslynWorkspaceHost(ws);
    }
}

