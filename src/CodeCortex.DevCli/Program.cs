using System.CommandLine;
using CodeCortex.Workspace;
using Microsoft.CodeAnalysis;
using System.Linq;
using CodeCortex.Core.Index;
using CodeCortex.Core.Symbols;
using CodeCortex.Core.IO;

using CodeCortex.DevCli;
using CodeCortex.DevCli.Util;
using System.Text.Json;

var root = new RootCommand("CodeCortex Dev CLI (Phase1 + ServiceV2 on-demand)");
// v2: outline2 (on-demand, no cache)
root.Add(CodeCortex.DevCli.V2Commands.CreateOutline2());
// v2: e2e-sim (simulate mutation + verify outline)
root.Add(CodeCortex.DevCli.V2Commands.CreateE2eSim());


// 注册 window 命令（Prompt 窗口生成）

var windowOutlineOption = new Option<string>("--outline-dir", () => Path.Combine(Directory.GetCurrentDirectory(), ".codecortex", "types"), "Outline 文件目录");
var windowPinnedOption = new Option<string>("--pinned-path", () => Path.Combine(Directory.GetCurrentDirectory(), ".codecortex", "pinned.json"), "Pinned 列表路径");
var windowRecentOption = new Option<string>("--recent-path", () => Path.Combine(Directory.GetCurrentDirectory(), ".codecortex", "recent.json"), "Recent 访问路径（可选）");
var windowCmd = new System.CommandLine.Command("window", "生成 Prompt 窗口 markdown 文件 (Pinned/Focus/Recent)");
windowCmd.AddOption(windowOutlineOption);
windowCmd.AddOption(windowPinnedOption);
windowCmd.AddOption(windowRecentOption);
windowCmd.SetHandler(
    (string outlineDir, string pinnedPath, string recentPath) => {
        var ctxRoot = Path.GetDirectoryName(outlineDir) ?? Directory.GetCurrentDirectory();
        var fs = new DefaultFileSystem();
        // 加载 pinned
        var pinnedSet = new CodeCortex.Core.Prompt.PinnedSet<string>(pinnedPath, fs);
        var pinned = pinnedSet.Items.ToList();
        // recentPath 预留，当前未用
        var accessTracker = new CodeCortex.Core.Prompt.AccessTracker<string>(32); // TODO: recentPath 持久化
        var focus = accessTracker.GetRecent(8);
        var recent = accessTracker.GetAll().Except(pinned).Except(focus).ToList();
        var builder = new CodeCortex.Core.Prompt.PromptWindowBuilder(outlineDir, fs);
        var content = builder.BuildWindow(pinned, focus, recent);
        var outPath = Path.Combine(ctxRoot, "prompt", "prompt_window.md");
        fs.CreateDirectory(Path.GetDirectoryName(outPath)!);
        fs.WriteAllText(outPath, content);
        Console.WriteLine($"Prompt 窗口已生成: {outPath} (Length={content.Length})");
    }, windowOutlineOption, windowPinnedOption, windowRecentOption
);
root.Add(windowCmd);

var modeOption = new Option<string>("--msbuild-mode", () => "auto", "MSBuild mode: auto|force|fallback");
var pathArg = new Argument<string>("path", ".sln or .csproj path");
var reuseModeOption = new Option<string>("--reuse-mode", () => "timestamp", "Index reuse strategy: exists|timestamp|hash|none");
var scanCmd = new Command("scan", "Load solution/project and enumerate types; optionally generate first outline") { pathArg, modeOption };
var genAllOption = new Option<bool>("--gen-outlines", () => false, "Generate outlines for all public/internal types (Phase1 S2)");
scanCmd.AddOption(genAllOption);
scanCmd.AddOption(reuseModeOption);

scanCmd.SetHandler(
    async (string path, string mode, bool genOutlines, string reuseMode) => {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IndexBuildLogger.Initialize(Directory.GetCurrentDirectory());
        var originalPath = path;
        if (string.IsNullOrWhiteSpace(path)) {
            await Console.Error.WriteLineAsync("ERROR: path argument is empty.");
            return;
        }
        // Normalize path (directory -> unique .sln)
        path = SlnPathHelper.ResolveEntryPath(path);
        IndexBuildLogger.Log($"CLI.Scan Start mode={mode} rawPath={originalPath} normalized={path}");

        var msMode = mode.ToLowerInvariant() switch {
            "force" => MsBuildMode.Force,
            "fallback" => MsBuildMode.Fallback,
            _ => MsBuildMode.Auto
        };
        // Phase1 S3: attempt reuse of existing index (short-circuit heavy work)
        var ctxRoot = Path.Combine(Directory.GetCurrentDirectory(), ".codecortex");
        var fs = new DefaultFileSystem();
        fs.CreateDirectory(ctxRoot);
        var scanStore = new CodeCortex.Core.Index.IndexStore(ctxRoot);
        var reuseModeNorm = (reuseMode ?? "timestamp").ToLowerInvariant();
        var reused = scanStore.TryLoad(out var scanLoadReason);
        if (reused != null && reuseModeNorm != "none") {
            bool reuse = false;
            int changed = 0, total = 0, hashChecked = 0;
            string modeTag = reuseModeNorm;
            if (reuseModeNorm == "exists") { reuse = true; } else if (reuseModeNorm == "timestamp") { reuse = CodeCortex.Core.Index.IndexReuseDecider.IsReusable(reused, out changed, out total); } else if (reuseModeNorm == "hash") { reuse = CodeCortex.Core.Index.IndexReuseDecider.IsReusableHash(reused, out changed, out total, out hashChecked); }
            if (reuse) {
                reused.Build.Reused = true;
                Console.WriteLine($"Index reuse ({modeTag}): Files={(total == 0 ? reused.FileManifest.Count : total)} Changed=0 Projects={reused.Stats.ProjectCount} Types={reused.Stats.TypeCount} Reason={scanLoadReason}");
                IndexBuildLogger.Log($"ReuseDecision OK Mode={modeTag} Files={(total == 0 ? reused.FileManifest.Count : total)} Changed=0 HashChecked={hashChecked}");
                return;
            }
            else {
                IndexBuildLogger.Log($"ReuseDecision REBUILD Mode={modeTag} Files={total} Changed={changed} HashChecked={hashChecked}");
            }
        }
        var loader = new MsBuildWorkspaceLoader(msMode);
        var loaded = await loader.LoadAsync(path);
        CodeCortex.Core.Ids.TypeIdGenerator.Initialize(Directory.GetCurrentDirectory());
        var outDir = Path.Combine(ctxRoot, "types");
        fs.CreateDirectory(outDir);
        var builder = new CodeCortex.Workspace.IndexBuilder(new RoslynTypeEnumerator(), new CodeCortex.Core.Hashing.TypeHasher(), new CodeCortex.Core.Outline.OutlineExtractor());
        var req = new CodeCortex.Core.Index.IndexBuildRequest(path, loaded.Projects.ToList(), genOutlines, new CodeCortex.Core.Hashing.HashConfig(), new CodeCortex.Core.Outline.OutlineOptions(), CodeCortex.Core.Index.SystemClock.Instance, new CodeCortex.Core.Index.FileOutlineWriter(outDir));
        var index = builder.Build(req);
        try { scanStore.Save(index); } catch (Exception ex) { await Console.Error.WriteLineAsync("Index save failed: " + ex.Message); }
        sw.Stop();
        IndexBuildLogger.Log($"Summary Projects={index.Stats.ProjectCount} Types={index.Stats.TypeCount} DurationMs={sw.ElapsedMilliseconds} Outlines={(genOutlines ? index.Stats.TypeCount : 0)}");
        Console.WriteLine($"Mode={msMode} Projects={index.Stats.ProjectCount} Types={index.Stats.TypeCount} Outlines={(genOutlines ? index.Stats.TypeCount : 0)} DurationMs={sw.ElapsedMilliseconds}");
    }, pathArg, modeOption, genAllOption, reuseModeOption
);

root.Add(scanCmd);

// outline-all command (full generation independent of scan metrics)
var outlineAll = new Command("outline-all", "Generate outlines for all public/internal types") { pathArg, modeOption };
outlineAll.AddOption(reuseModeOption);
outlineAll.SetHandler(
    async (string path, string mode, string reuseMode) => {
        IndexBuildLogger.Initialize(Directory.GetCurrentDirectory());
        // Normalize path (directory -> unique .sln)
        path = SlnPathHelper.ResolveEntryPath(path);
        var msMode = mode.ToLowerInvariant() switch { "force" => MsBuildMode.Force, "fallback" => MsBuildMode.Fallback, _ => MsBuildMode.Auto };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var loader = new MsBuildWorkspaceLoader(msMode);
        var loaded = await loader.LoadAsync(path);
        CodeCortex.Core.Ids.TypeIdGenerator.Initialize(Directory.GetCurrentDirectory());
        var ctxRoot = Path.Combine(Directory.GetCurrentDirectory(), ".codecortex");
        var fs = new DefaultFileSystem();
        fs.CreateDirectory(ctxRoot);
        var outlineStore = new CodeCortex.Core.Index.IndexStore(ctxRoot);
        var reuseModeNorm = (reuseMode ?? "timestamp").ToLowerInvariant();
        var existing = outlineStore.TryLoad(out var reason);
        if (existing != null && reuseModeNorm != "none") {
            bool reuse = false;
            int changed = 0, total = 0, hashChecked = 0;
            string modeTag = reuseModeNorm;
            if (reuseModeNorm == "exists") {
                reuse = true;
            }
            else if (reuseModeNorm == "timestamp") {
                reuse = CodeCortex.Core.Index.IndexReuseDecider.IsReusable(existing, out changed, out total);
            }
            else if (reuseModeNorm == "hash") {
                reuse = CodeCortex.Core.Index.IndexReuseDecider.IsReusableHash(existing, out changed, out total, out hashChecked);
            }

            if (reuse) {
                existing.Build.Reused = true;
                Console.WriteLine($"Index reuse ({modeTag}): Files={(total == 0 ? existing.FileManifest.Count : total)} Changed=0 Projects={existing.Stats.ProjectCount} Types={existing.Stats.TypeCount} Reason={reason}");
                IndexBuildLogger.Log($"ReuseDecision OK Mode={modeTag} Files={(total == 0 ? existing.FileManifest.Count : total)} Changed=0 HashChecked={hashChecked}");
                return;
            }
            else {
                IndexBuildLogger.Log($"ReuseDecision REBUILD Mode={modeTag} Files={total} Changed={changed} HashChecked={hashChecked}");
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
    }, pathArg, modeOption, reuseModeOption
);

root.Add(outlineAll);

// resolve command (S4 symbol resolution)
var queryArg = new Argument<string>("query", description: "Symbol query: exact FQN, simple name, suffix, wildcard (* ?), or fuzzy (small typos)");
var limitOption = new Option<int>("--limit", () => 20, "Maximum results to return");
var jsonOption = new Option<bool>("--json", () => false, "Output JSON array (Fqn, Kind, Match, Distance, Id)");
var resolveCmd = new Command("resolve", "Resolve symbol name(s) against existing index (.codecortex)") { queryArg, limitOption, jsonOption };
resolveCmd.SetHandler(
    (string query, int limit, bool json) => {
        var ctxRoot = Path.Combine(Directory.GetCurrentDirectory(), ".codecortex");
        var store = new CodeCortex.Core.Index.IndexStore(ctxRoot);
        var index = store.TryLoad(out var reason);
        if (index == null) {
            Console.Error.WriteLine("Index not found (.codecortex/index.json). Run 'scan' first.");
            Environment.ExitCode = 2;
            return;
        }
        var resolver = new SymbolResolver(index);
        var matches = resolver.Resolve(query, limit);
        if (json) {
            var payload = matches.Select(m => new { m.Fqn, m.Kind, Match = m.MatchKind.ToString(), m.Distance, m.Id, m.IsAmbiguous });
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = matches.Count > 0 ? 0 : 3;
            return;
        }
        if (matches.Count == 0) {
            Console.Error.WriteLine("No matches.");
            Environment.ExitCode = 3;
            return;
        }
        Console.WriteLine($"Matches ({matches.Count}):");
        foreach (var m in matches) {
            var amb = m.IsAmbiguous ? " *AMB*" : string.Empty;
            Console.WriteLine($"{m.MatchKind,-14} {m.Fqn} [{m.Kind}] (Id={m.Id}{(m.Distance != null ? $",d={m.Distance}" : "")}){amb}");
        }
    }, queryArg, limitOption, jsonOption
);
root.Add(resolveCmd);

// watch command (S5 initial incremental prototype - partial incremental rebuild)
var watchPathArg = new Argument<string>("path", () => ".", "Solution or project path (or directory) to watch");
var watchCmd = new Command("watch", "Watch source tree for .cs changes and perform incremental outline rebuild (Phase1 S5 prototype)") { watchPathArg, modeOption };
watchCmd.SetHandler(
    async (string path, string mode) => {
        IndexBuildLogger.Initialize(Directory.GetCurrentDirectory());
        // Normalize path (directory -> unique .sln)
        path = SlnPathHelper.ResolveEntryPath(path);
        var msMode = mode.ToLowerInvariant() switch { "force" => MsBuildMode.Force, "fallback" => MsBuildMode.Fallback, _ => MsBuildMode.Auto };
        CodeCortex.Core.Ids.TypeIdGenerator.Initialize(Directory.GetCurrentDirectory());
        var ctxRoot = Path.Combine(Directory.GetCurrentDirectory(), ".codecortex");
        var fs = new DefaultFileSystem();
        fs.CreateDirectory(ctxRoot);
        var typesDir = Path.Combine(ctxRoot, "types");
        fs.CreateDirectory(typesDir);
        var store = new CodeCortex.Core.Index.IndexStore(ctxRoot);
        var existing = store.TryLoad(out _);
        if (existing == null) {
            Console.WriteLine("No existing index. Building full index first...");
            var loader0 = new MsBuildWorkspaceLoader(msMode);
            var loaded0 = await loader0.LoadAsync(path);
            var builder0 = new CodeCortex.Workspace.IndexBuilder(new RoslynTypeEnumerator(), new CodeCortex.Core.Hashing.TypeHasher(), new CodeCortex.Core.Outline.OutlineExtractor());
            var req0 = new CodeCortex.Core.Index.IndexBuildRequest(path, loaded0.Projects.ToList(), true, new CodeCortex.Core.Hashing.HashConfig(), new CodeCortex.Core.Outline.OutlineOptions(), CodeCortex.Core.Index.SystemClock.Instance, new CodeCortex.Core.Index.FileOutlineWriter(typesDir));
            existing = builder0.Build(req0);
            store.Save(existing);
        }
        // Prepare workspace loader for project resolution; keep solution in memory for simple mapping.
        var loader = new MsBuildWorkspaceLoader(msMode);
        var loaded = await loader.LoadAsync(path);
        var sol = loaded.Solution;
        Project? ResolveProject(string file) {
            // naive: find first project containing a document with matching path
            foreach (var p in sol.Projects) {
                if (p.Documents.Any(d => string.Equals(d.FilePath, file, StringComparison.OrdinalIgnoreCase))) { return p; }
            }
            return null;
        }
        INamedTypeSymbol? ResolveById(string id) {
            // Phase1 simplified: linear scan (acceptable for small scale)
            foreach (var p in sol.Projects) {
                var comp = p.GetCompilationAsync().GetAwaiter().GetResult();
                if (comp == null) { continue; }
                foreach (var t in new RoslynTypeEnumerator().Enumerate(comp)) {
                    var tid = CodeCortex.Core.Ids.TypeIdGenerator.GetId(t);
                    if (tid == id) { return t; }
                }
            }
            return null;
        }
        var batcher = new CodeCortex.Workspace.Incremental.DebounceFileChangeBatcher();
        var classifier = new CodeCortex.Workspace.Incremental.ChangeClassifier();
        var impactAnalyzer = new CodeCortex.Workspace.Incremental.ImpactAnalyzer();
        var incr = new CodeCortex.Workspace.Incremental.IncrementalProcessor();
        var revCache = new CodeCortex.Workspace.Incremental.ReverseIndexCache(existing);
        batcher.Flushed += raw => {
            try {
                var classified = classifier.Classify(raw);
                var impact = impactAnalyzer.Analyze(existing!, classified, ResolveProject, CancellationToken.None);
                if (impact.AffectedTypeIds.Count == 0 && impact.RemovedTypeIds.Count == 0) {
                    Console.WriteLine($"Incremental: No affected types (Files={classified.Count})");
                    return;
                }
                var result = incr.Process(existing!, impact, new CodeCortex.Core.Hashing.TypeHasher(), new CodeCortex.Core.Outline.OutlineExtractor(), ResolveById, typesDir, new DefaultFileSystem(), CancellationToken.None);
                store.Save(existing!);
                Console.WriteLine($"Incremental: Files={classified.Count} ChangedTypes={result.ChangedTypeCount} Removed={result.RemovedTypeCount} DurationMs={result.DurationMs}");
            }
            catch (Exception ex) {
                Console.Error.WriteLine("Incremental batch error: " + ex.Message);
            }
        };
        using var watcher = new CodeCortex.Workspace.Incremental.SolutionFileWatcher(Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory(), batcher);
        watcher.Start();
        Console.WriteLine("Watching for changes. Press Ctrl+C to exit.");
        var done = new TaskCompletionSource();
        Console.CancelKeyPress += (s, e) => {
            e.Cancel = true;
            done.TrySetResult();
        };
        await done.Task.ConfigureAwait(false);
    }, watchPathArg, modeOption
);
root.Add(watchCmd);

await root.InvokeAsync(args);
