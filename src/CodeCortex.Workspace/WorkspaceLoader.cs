using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeCortex.Workspace;

public interface IWorkspaceLoader {
    Task<LoadedSolution> LoadAsync(string entryPath, CancellationToken ct = default);
}

public sealed record LoadedSolution(Solution Solution, IReadOnlyList<Project> Projects);

/// <summary>
/// MSBuild workspace loader with minimal retry + logging.
/// TODO: S3 replace plain logging with AtomicIO wrapper.
/// </summary>
public enum MsBuildMode { Auto, Force, Fallback }

public sealed partial class MsBuildWorkspaceLoader : IWorkspaceLoader {
    private static int _registered;
    private readonly MsBuildMode _mode;
    public MsBuildWorkspaceLoader(MsBuildMode mode = MsBuildMode.Auto) => _mode = mode;

    public async Task<LoadedSolution> LoadAsync(string entryPath, CancellationToken ct = default) {
        if (_mode == MsBuildMode.Fallback) {
            return EnsureFallback(entryPath);
        }

        EnsureMsBuildRegistered();
        var attempt = 0;
        var maxAttempts = _mode == MsBuildMode.Force ? 1 : 3;
        Exception? last = null;
        while (attempt < maxAttempts) {
            attempt++;
            try {
                IndexBuildLogger.Log($"WorkspaceLoad.Attempt={attempt} path={entryPath}");
                var solution = await LoadViaMsBuild(entryPath, ct).ConfigureAwait(false);
                IndexBuildLogger.Log($"WorkspaceLoad.MSBuild path={entryPath} Projects={solution.Projects.Count}");
                return solution;
            } catch (Exception ex) when (_mode != MsBuildMode.Force) {
                last = ex;
                IndexBuildLogger.Log($"WorkspaceLoad.Error attempt={attempt} {ex.GetType().Name} {ex.Message}");
                // brief backoff except last
                if (attempt < maxAttempts) {
                    try { await Task.Delay(TimeSpan.FromMilliseconds((100 * attempt)), ct); } catch { }
                }
            }
        }
        if (_mode == MsBuildMode.Auto) {
            IndexBuildLogger.Log($"WorkspaceLoad.MSBuild.Fail {last?.GetType().Name} {last?.Message} -> fallback");
            return EnsureFallback(entryPath);
        }
        throw last ?? new InvalidOperationException("Workspace load failed with unknown error");
    }

    private static void EnsureMsBuildRegistered() {
        if (Interlocked.Exchange(ref _registered, 1) == 1) {
            return;
        }

        if (!MSBuildLocator.IsRegistered) {
            MSBuildLocator.RegisterDefaults();
            IndexBuildLogger.Log("MSBuildLocator.RegisteredDefaults");
        }
    }

    private static async Task<LoadedSolution> LoadViaMsBuild(string entryPath, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(entryPath)) {
            throw new ArgumentException("entryPath empty", nameof(entryPath));
        }

        using var ws = MSBuildWorkspace.Create();
        ws.WorkspaceFailed += (s, e) => IndexBuildLogger.Log($"WorkspaceWarning {e.Diagnostic.Kind} {e.Diagnostic.Message}");
        Solution solution;
        if (entryPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)) {
            solution = await ws.OpenSolutionAsync(entryPath, cancellationToken: ct).ConfigureAwait(false);
        } else if (entryPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) {
            var project = await ws.OpenProjectAsync(entryPath, cancellationToken: ct).ConfigureAwait(false);
            solution = project.Solution;
        } else {
            throw new ArgumentException("Entry path must be a .sln or .csproj", nameof(entryPath));
        }
        return new LoadedSolution(solution, solution.Projects.ToList());
    }

    private static LoadedSolution EnsureFallback(string entryPath) {
        var fb = BuildFallbackSolution(entryPath) ?? throw new InvalidOperationException("Fallback workspace build failed");
        IndexBuildLogger.Log($"WorkspaceLoad.Fallback path={entryPath} Projects={fb.Projects.Count}");
        return fb;
    }
}

internal static class FallbackBuilder {
    public static LoadedSolution? Build(string entryPath) {
        try {
            if (string.IsNullOrWhiteSpace(entryPath)) {
                entryPath = Directory.GetCurrentDirectory();
            }

            string rootDir;
            if (Directory.Exists(entryPath)) {
                rootDir = entryPath;
            } else if (File.Exists(entryPath)) {
                rootDir = Path.GetDirectoryName(Path.GetFullPath(entryPath)) ?? Directory.GetCurrentDirectory();
            } else {
                rootDir = Directory.GetCurrentDirectory();
            }

            var csFiles = Directory.EnumerateFiles(rootDir!, "*.cs", SearchOption.AllDirectories)
                .Where(
                p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                            !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
            )
                .ToList();
            var adhoc = new AdhocWorkspace();
            var projId = ProjectId.CreateNewId();
            var projInfo = ProjectInfo.Create(projId, VersionStamp.Create(), "AdhocFallback", "AdhocFallback", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );
            adhoc.AddProject(projInfo);
            var references = new[]
            {
                typeof(object).Assembly,
                typeof(Enumerable).Assembly,
                typeof(Console).Assembly
            }.Distinct().Select(a => MetadataReference.CreateFromFile(a.Location));
            var proj = adhoc.CurrentSolution.GetProject(projId)!;
            proj = proj.AddMetadataReferences(references);
            foreach (var file in csFiles) {
                var text = File.ReadAllText(file);
                var docId = DocumentId.CreateNewId(projId);
                proj = proj.AddDocument(Path.GetFileName(file), SourceText.From(text), filePath: file).Project;
            }
            var sol = proj.Solution;
            return new LoadedSolution(sol, sol.Projects.ToList());
        } catch (Exception ex) {
            IndexBuildLogger.Log($"WorkspaceLoad.Fallback.Error {ex.Message}");
            return null;
        }
    }
}

partial class MsBuildWorkspaceLoader {
    private static LoadedSolution? BuildFallbackSolution(string entryPath) => FallbackBuilder.Build(entryPath);
}
