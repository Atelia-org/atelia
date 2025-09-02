using System.CommandLine;
using CodeCortex.Workspace;
using Microsoft.CodeAnalysis;

var root = new RootCommand("CodeCortex CLI (Phase1 - S1 Scan)");

var modeOption = new Option<string>("--msbuild-mode", () => "auto", "MSBuild mode: auto|force|fallback");
var pathArg = new Argument<string>("path", ".sln or .csproj path");
var scanCmd = new Command("scan", "Load solution/project and enumerate types; optionally generate first outline") { pathArg, modeOption };
var genAllOption = new Option<bool>("--gen-outlines", () => false, "Generate outlines for all public/internal types (Phase1 S2)");
scanCmd.AddOption(genAllOption);

scanCmd.SetHandler(
    async (string path, string mode, bool genOutlines) => {
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
        CodeCortex.Core.Ids.TypeIdGenerator.Initialize(Directory.GetCurrentDirectory());
        var hasher = new CodeCortex.Core.Hashing.TypeHasher();
        var outline = new CodeCortex.Core.Outline.OutlineExtractor();
        var hashCfg = new CodeCortex.Core.Hashing.HashConfig();
        var outRoot = Path.Combine(Directory.GetCurrentDirectory(), ".codecortex", "types");
        if (genOutlines) {
            Directory.CreateDirectory(outRoot);
        }

        int typeCount = 0;
        int outlineCount = 0;
        foreach (var p in loaded.Projects) {
            Compilation? c = await p.GetCompilationAsync();
            if (c == null) {
                continue;
            }

            foreach (var t in enumerator.Enumerate(c)) {
                if (t.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal) {
                    typeCount++;
                    if (genOutlines) {
                        var hashes = hasher.Compute(t, Array.Empty<string>(), hashCfg);
                        var text = outline.BuildOutline(t, hashes, new CodeCortex.Core.Outline.OutlineOptions());
                        var id = CodeCortex.Core.Ids.TypeIdGenerator.GetId(t);
                        var file = Path.Combine(outRoot, id + ".outline.md");
                        try {
                            File.WriteAllText(file, text, System.Text.Encoding.UTF8);
                            outlineCount++;
                        } catch { }
                    }
                }
            }
        }
        sw.Stop();
        IndexBuildLogger.Log($"Summary Projects={loaded.Projects.Count} Types={typeCount} DurationMs={sw.ElapsedMilliseconds} Outlines={outlineCount}");
        Console.WriteLine($"Mode={msMode} Projects={loaded.Projects.Count} Types={typeCount} Outlines={outlineCount} DurationMs={sw.ElapsedMilliseconds}");
    }, pathArg, modeOption, genAllOption
);

root.Add(scanCmd);

// outline-all command (full generation independent of scan metrics)
var outlineAll = new Command("outline-all", "Generate outlines for all public/internal types") { pathArg, modeOption };
outlineAll.SetHandler(
    async (string path, string mode) => {
        IndexBuildLogger.Initialize(Directory.GetCurrentDirectory());
        if (Directory.Exists(path)) {
            var slns = Directory.GetFiles(path, "*.sln", SearchOption.TopDirectoryOnly);
            if (slns.Length == 1) {
                path = slns[0];
            }
        }
        path = Path.GetFullPath(path);
        var msMode = mode.ToLowerInvariant() switch {
            "force" => MsBuildMode.Force,
            "fallback" => MsBuildMode.Fallback,
            _ => MsBuildMode.Auto
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var loader = new MsBuildWorkspaceLoader(msMode);
        var loaded = await loader.LoadAsync(path);
        var enumerator = new RoslynTypeEnumerator();
        CodeCortex.Core.Ids.TypeIdGenerator.Initialize(Directory.GetCurrentDirectory());
        var hasher = new CodeCortex.Core.Hashing.TypeHasher();
        var extractor = new CodeCortex.Core.Outline.OutlineExtractor();
        var cfg = new CodeCortex.Core.Hashing.HashConfig();
        var outRoot = Path.Combine(Directory.GetCurrentDirectory(), ".codecortex", "types");
        Directory.CreateDirectory(outRoot);
        int outlineCount = 0;
        foreach (var p in loaded.Projects) {
            var comp = await p.GetCompilationAsync();
            if (comp == null) {
                continue;
            }

            foreach (var t in enumerator.Enumerate(comp)) {
                if (t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)) {
                    continue;
                }

                var hashes = hasher.Compute(t, Array.Empty<string>(), cfg);
                var text = extractor.BuildOutline(t, hashes, new CodeCortex.Core.Outline.OutlineOptions());
                var id = CodeCortex.Core.Ids.TypeIdGenerator.GetId(t);
                var file = Path.Combine(outRoot, id + ".outline.md");
                try {
                    File.WriteAllText(file, text, System.Text.Encoding.UTF8);
                    outlineCount++;
                } catch { }
            }
        }
        sw.Stop();
        IndexBuildLogger.Log($"OutlineAll Projects={loaded.Projects.Count} Outlines={outlineCount} DurationMs={sw.ElapsedMilliseconds}");
        Console.WriteLine($"Generated {outlineCount} outlines in {sw.ElapsedMilliseconds} ms");
    }, pathArg, modeOption
);

root.Add(outlineAll);

await root.InvokeAsync(args);
