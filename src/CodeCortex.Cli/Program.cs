using System.CommandLine;
using CodeCortex.Workspace;
using Microsoft.CodeAnalysis;
using System.Linq;
using CodeCortex.Core.Index;

var root = new RootCommand("CodeCortex CLI (Phase1 - S1-S3 Scan+Index)");

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
        // Phase1 S3: attempt reuse of existing index (short-circuit heavy work)
        var ctxRoot = Path.Combine(Directory.GetCurrentDirectory(), ".codecortex");
        Directory.CreateDirectory(ctxRoot);
        var scanStore = new CodeCortex.Core.Index.IndexStore(ctxRoot);
        var reused = scanStore.TryLoad(out var scanLoadReason);
        if (reused != null) {
            if (CodeCortex.Core.Index.IndexReuseDecider.IsReusable(reused, out var changed, out var total)) {
                reused.Build.Reused = true;
                Console.WriteLine($"Index reuse (timestamp): Files={total} Changed=0 Projects={reused.Stats.ProjectCount} Types={reused.Stats.TypeCount} Reason={scanLoadReason}");
                IndexBuildLogger.Log($"ReuseDecision OK Files={total} Changed=0");
                return;
            } else {
                IndexBuildLogger.Log($"ReuseDecision REBUILD Files={total} Changed={changed}");
            }
        }
        var loader = new MsBuildWorkspaceLoader(msMode);
        var loaded = await loader.LoadAsync(path);
        CodeCortex.Core.Ids.TypeIdGenerator.Initialize(Directory.GetCurrentDirectory());
        var outDir = Path.Combine(ctxRoot, "types");
        var builder = new CodeCortex.Workspace.IndexBuilder(new RoslynTypeEnumerator(), new CodeCortex.Core.Hashing.TypeHasher(), new CodeCortex.Core.Outline.OutlineExtractor());
        var req = new CodeCortex.Core.Index.IndexBuildRequest(path, loaded.Projects.ToList(), genOutlines, new CodeCortex.Core.Hashing.HashConfig(), new CodeCortex.Core.Outline.OutlineOptions(), CodeCortex.Core.Index.SystemClock.Instance, new CodeCortex.Core.Index.FileOutlineWriter(outDir));
        var index = builder.Build(req);
        try { scanStore.Save(index); } catch (Exception ex) { await Console.Error.WriteLineAsync("Index save failed: " + ex.Message); }
        sw.Stop();
        IndexBuildLogger.Log($"Summary Projects={index.Stats.ProjectCount} Types={index.Stats.TypeCount} DurationMs={sw.ElapsedMilliseconds} Outlines={(genOutlines ? index.Stats.TypeCount : 0)}");
        Console.WriteLine($"Mode={msMode} Projects={index.Stats.ProjectCount} Types={index.Stats.TypeCount} Outlines={(genOutlines ? index.Stats.TypeCount : 0)} DurationMs={sw.ElapsedMilliseconds}");
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
        var msMode = mode.ToLowerInvariant() switch { "force" => MsBuildMode.Force, "fallback" => MsBuildMode.Fallback, _ => MsBuildMode.Auto };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var loader = new MsBuildWorkspaceLoader(msMode);
        var loaded = await loader.LoadAsync(path);
        CodeCortex.Core.Ids.TypeIdGenerator.Initialize(Directory.GetCurrentDirectory());
        var ctxRoot = Path.Combine(Directory.GetCurrentDirectory(), ".codecortex");
        Directory.CreateDirectory(ctxRoot);
        var outlineStore = new CodeCortex.Core.Index.IndexStore(ctxRoot);
        var existing = outlineStore.TryLoad(out var reason);
        if (existing != null) {
            if (CodeCortex.Core.Index.IndexReuseDecider.IsReusable(existing, out var changed, out var total)) {
                existing.Build.Reused = true;
                Console.WriteLine($"Index reuse (timestamp): Files={total} Changed=0 Projects={existing.Stats.ProjectCount} Types={existing.Stats.TypeCount} Reason={reason}");
                IndexBuildLogger.Log($"ReuseDecision OK Files={total} Changed=0");
                return;
            } else {
                IndexBuildLogger.Log($"ReuseDecision REBUILD Files={total} Changed={changed}");
            }
        }
        var outDir = Path.Combine(ctxRoot, "types");
        var builder = new CodeCortex.Workspace.IndexBuilder(new RoslynTypeEnumerator(), new CodeCortex.Core.Hashing.TypeHasher(), new CodeCortex.Core.Outline.OutlineExtractor());
        var req = new CodeCortex.Core.Index.IndexBuildRequest(path, loaded.Projects.ToList(), true, new CodeCortex.Core.Hashing.HashConfig(), new CodeCortex.Core.Outline.OutlineOptions(), CodeCortex.Core.Index.SystemClock.Instance, new CodeCortex.Core.Index.FileOutlineWriter(outDir));
        var index = builder.Build(req);
        try { outlineStore.Save(index); } catch (Exception ex) { await Console.Error.WriteLineAsync("Index save failed: " + ex.Message); }
        sw.Stop();
        IndexBuildLogger.Log($"OutlineAll Projects={index.Stats.ProjectCount} Outlines={index.Stats.TypeCount} DurationMs={sw.ElapsedMilliseconds}");
        Console.WriteLine($"Generated {index.Stats.TypeCount} outlines in {sw.ElapsedMilliseconds} ms");
    }, pathArg, modeOption
);

root.Add(outlineAll);

await root.InvokeAsync(args);
