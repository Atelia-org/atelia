using System.CommandLine;
using CodeCortex.Workspace;
using Microsoft.CodeAnalysis;

var root = new RootCommand("CodeCortex CLI (Phase1 - S1 Scan)");

var modeOption = new Option<string>("--msbuild-mode", () => "auto", "MSBuild mode: auto|force|fallback");
var pathArg = new Argument<string>("path", ".sln or .csproj path");
var scanCmd = new Command("scan", "Load solution/project and enumerate types") { pathArg, modeOption };

scanCmd.SetHandler(
    async (string path, string mode) => {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IndexBuildLogger.Initialize(Directory.GetCurrentDirectory());
        var originalPath = path;
        if (string.IsNullOrWhiteSpace(path)) {
            await Console.Error.WriteLineAsync("ERROR: path argument is empty.");
            return;
        }
        // Normalize path: if directory passed, try find single .sln inside.
        if (Directory.Exists(path)) {
            var slns = Directory.GetFiles(path, "*.sln", SearchOption.TopDirectoryOnly);
            if (slns.Length == 1) {
                path = slns[0];
            }
        }
        try { path = Path.GetFullPath(path); } catch { }
        IndexBuildLogger.Log($"CLI.Scan Start mode={mode} rawPath={originalPath} normalized={path}");

        var msMode = mode.ToLowerInvariant() switch {
            "force" => MsBuildMode.Force,
            "fallback" => MsBuildMode.Fallback,
            _ => MsBuildMode.Auto
        };
        var loader = new MsBuildWorkspaceLoader(msMode);
        var loaded = await loader.LoadAsync(path);
        var enumerator = new RoslynTypeEnumerator();
        int typeCount = 0;
        foreach (var p in loaded.Projects) {
            Compilation? c = await p.GetCompilationAsync();
            if (c == null) {
                continue;
            }

            foreach (var t in enumerator.Enumerate(c)) {
                if (t.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal) {
                    typeCount++;
                }
            }
        }
        sw.Stop();
        IndexBuildLogger.Log($"Summary Projects={loaded.Projects.Count} Types={typeCount} DurationMs={sw.ElapsedMilliseconds}");
        Console.WriteLine($"Mode={msMode} Projects={loaded.Projects.Count} Types={typeCount} DurationMs={sw.ElapsedMilliseconds}");
    }, pathArg, modeOption
);

root.Add(scanCmd);

await root.InvokeAsync(args);
