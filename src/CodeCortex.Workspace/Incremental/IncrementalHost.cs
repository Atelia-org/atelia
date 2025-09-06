using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeCortex.Core.Hashing;
using CodeCortex.Core.Index;
using CodeCortex.Core.IO;
using CodeCortex.Core.Outline;
using CodeCortex.Workspace.Incremental;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Atelia.Diagnostics;

namespace CodeCortex.Workspace.Incremental;

/// <summary>
/// 封装文件监控和增量更新管线，供 Service/CLI 复用。
/// </summary>
public sealed class IncrementalHost : IDisposable {
    private readonly ServiceIndexManager _indexManager;
    private readonly string _solutionRoot;
    private readonly string _outlineDir;
    private readonly IFileSystem _fs;
    private Microsoft.CodeAnalysis.Solution? _solution;

    // 增量处理组件
    private readonly DebounceFileChangeBatcher _batcher;
    private readonly SolutionFileWatcher _watcher;
    private readonly ChangeClassifier _classifier;
    private readonly ImpactAnalyzer _impactAnalyzer;
    private readonly IncrementalProcessor _processor;

    // 处理器组件
    private readonly TypeHasher _hasher;
    private readonly OutlineExtractor _outlineExtractor;

    private IncrementalHost(
        ServiceIndexManager indexManager,
        string solutionRoot,
        string outlineDir,
        IFileSystem fs
    ) {
        _indexManager = indexManager ?? throw new ArgumentNullException(nameof(indexManager));
        _solutionRoot = solutionRoot ?? throw new ArgumentNullException(nameof(solutionRoot));
        _outlineDir = outlineDir ?? throw new ArgumentNullException(nameof(outlineDir));
        _fs = fs ?? new DefaultFileSystem();

        var debounceMs = Environment.GetEnvironmentVariable("CODECORTEX_DEBOUNCE_MS");
        var debounceTime = int.TryParse(debounceMs, out var ms) && ms > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromMilliseconds(400);
        DebugUtil.Print("Incremental", $"Debounce time: {debounceTime.TotalMilliseconds}ms");

        _batcher = new DebounceFileChangeBatcher(debounceTime);
        var watchRoot = Directory.Exists(_solutionRoot) ? _solutionRoot : (Path.GetDirectoryName(_solutionRoot) ?? Directory.GetCurrentDirectory());
        _watcher = new SolutionFileWatcher(watchRoot, _batcher);
        _classifier = new ChangeClassifier();
        _impactAnalyzer = new ImpactAnalyzer(_fs);
        _processor = new IncrementalProcessor();

        _hasher = new TypeHasher();
        _outlineExtractor = new OutlineExtractor();

        _batcher.Flushed += OnFileChangesFlushed;
        DebugUtil.Print("Incremental", $"IncrementalHost initialized for {_solutionRoot}");
    }

    public static async Task<IncrementalHost> CreateAsync(
        ServiceIndexManager indexManager,
        string solutionRoot,
        string outlineDir,
        IFileSystem? fs = null
    ) {
        var host = new IncrementalHost(indexManager, solutionRoot, outlineDir, fs ?? new DefaultFileSystem());
        await host.InitializeSolutionAsync();
        return host;
    }

    public int QueueDepth => _batcher.PendingCount;

    public void Start() {
        try {
            _watcher.Start();
            DebugUtil.Print("Watcher", $"File watcher started for {_solutionRoot}");
        } catch (Exception ex) {
            DebugUtil.Print("Watcher", $"Failed to start file watcher: {ex.Message}");
            throw;
        }
    }

    private void OnFileChangesFlushed(IReadOnlyList<RawFileChange> rawChanges) {
        try {
            DebugUtil.Print("Incremental", $"Processing {rawChanges.Count} file changes");
            _indexManager.UpdateIndex(
                index => {
                    var classified = _classifier.Classify(rawChanges);
                    DebugUtil.Print("Incremental", $"Classified changes: {classified.Count}");
                    ApplyDocumentUpdates(classified);
                    var impact = _impactAnalyzer.Analyze(index, classified, ResolveProject, CancellationToken.None);
                    if (impact.AffectedTypeIds.Count == 0 && impact.RemovedTypeIds.Count == 0) {
                        DebugUtil.Print("Incremental", "No affected types found");
                        return;
                    }
                    var compilationCache = new Dictionary<ProjectId, Compilation?>();
                    var negativeLookup = new Dictionary<ProjectId, HashSet<string>>();
                    int compRequests = 0, compBuilds = 0, compCacheHits = 0;
                    int negativeSkips = 0, negativeMarks = 0;
                    int resolveAttempts = 0, resolveHits = 0;

                    Compilation? GetCompilation(Project project) {
                        compRequests++;
                        if (compilationCache.TryGetValue(project.Id, out var cached)) {
                            compCacheHits++;
                            return cached;
                        }
                        try {
                            var comp = project.GetCompilationAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                            compilationCache[project.Id] = comp;
                            compBuilds++;
                            return comp;
                        } catch (Exception ex) {
                            DebugUtil.Print("Incremental", $"Error getting compilation for project {project.Name}: {ex.Message}");
                            compilationCache[project.Id] = null;
                            compBuilds++;
                            return null;
                        }
                    }
                    bool SeenNegative(ProjectId pid, string fqn) => negativeLookup.TryGetValue(pid, out var set) && set.Contains(fqn);
                    void MarkNegative(ProjectId pid, string fqn) {
                        if (!negativeLookup.TryGetValue(pid, out var set)) {
                            set = new HashSet<string>(StringComparer.Ordinal);
                            negativeLookup[pid] = set;
                        }

                        set.Add(fqn);
                    }
                    INamedTypeSymbol? ResolveUsingCache(string typeId) {
                        if (_solution == null) {
                            return null;
                        }

                        resolveAttempts++;
                        var typeEntry = index.Types.FirstOrDefault(t => t.Id == typeId);
                        if (typeEntry == null) {
                            return null;
                        }

                        var fqn = typeEntry.Fqn;
                        foreach (var project in _solution.Projects) {
                            if (SeenNegative(project.Id, fqn)) {
                                negativeSkips++;
                                continue;
                            }
                            var comp = GetCompilation(project);
                            if (comp == null) {
                                MarkNegative(project.Id, fqn);
                                negativeMarks++;
                                continue;
                            }
                            var symbol = comp.GetTypeByMetadataName(fqn);
                            if (symbol != null) {
                                resolveHits++;
                                return symbol;
                            }
                            MarkNegative(project.Id, fqn);
                            negativeMarks++;
                        }
                        return null;
                    }
                    var result = _processor.Process(index, impact, _hasher, _outlineExtractor, ResolveUsingCache, _outlineDir, _fs, CancellationToken.None);
                    DebugUtil.Print("Incremental", $"Incremental update completed: Changed={result.ChangedTypeCount}, Removed={result.RemovedTypeCount}, Duration={result.DurationMs}ms, OutlineWritten={result.OutlineWrittenCount}, OutlineSkipped={result.OutlineSkippedCount}");
                    DebugUtil.Print("Incremental", $"Compilation cache: requests={compRequests}, hits={compCacheHits}, builds={compBuilds}");
                    DebugUtil.Print("Incremental", $"Negative lookup: skips={negativeSkips}, marked={negativeMarks}");
                    DebugUtil.Print("Incremental", $"Resolve: attempts={resolveAttempts}, hits={resolveHits}");
                }
            );
        } catch (Exception ex) {
            DebugUtil.Print("Incremental", $"Incremental update failed: {ex.Message}");
        }
    }

    private async Task InitializeSolutionAsync() {
        try {
            DebugUtil.Print("Incremental", $"Loading solution for project resolution: {_solutionRoot}");
            var loader = new CodeCortex.Workspace.MsBuildWorkspaceLoader(CodeCortex.Workspace.MsBuildMode.Auto);
            var loaded = await loader.LoadAsync(_solutionRoot).ConfigureAwait(false);
            _solution = loaded.Solution;
            DebugUtil.Print("Incremental", $"Solution loaded: {loaded.Projects.Count} projects");
        } catch (Exception ex) {
            DebugUtil.Print("Incremental", $"Failed to load solution: {ex.Message}\n{ex}");
            _solution = null;
        }
    }

    private Microsoft.CodeAnalysis.Project? ResolveProject(string filePath) {
        if (_solution == null) {
            DebugUtil.Print("Incremental", "Solution not loaded, cannot resolve project");
            return null;
        }
        foreach (var project in _solution.Projects) {
            if (project.Documents.Any(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase))) {
                DebugUtil.Print("Incremental", $"Resolved project: {project.Name} for file: {filePath}");
                return project;
            }
        }
        DebugUtil.Print("Incremental", $"No project found for file: {filePath}");
        return null;
    }

    private void ApplyDocumentUpdates(IReadOnlyList<ClassifiedFileChange> classified) {
        try {
            if (_solution == null) {
                return;
            }

            var paths = classified.Where(c => c.Kind != ClassifiedKind.Delete).Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0) {
                return;
            }

            var solution = _solution;
            int applied = 0;
            foreach (var path in paths) {
                try {
                    var doc = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
                    if (doc == null) {
                        continue;
                    }

                    if (!_fs.FileExists(path)) {
                        continue;
                    }

                    var text = _fs.ReadAllText(path);
                    var src = SourceText.From(text, Encoding.UTF8);
                    solution = solution.WithDocumentText(doc.Id, src, PreservationMode.PreserveValue);
                    applied++;
                } catch (Exception ex) { DebugUtil.Print("Incremental", $"ApplyDocumentUpdates failed for {path}: {ex.Message}"); }
            }
            if (applied > 0) {
                _solution = solution;
                DebugUtil.Print("Incremental", $"Applied document updates: {applied}");
            }
        } catch (Exception ex) { DebugUtil.Print("Incremental", $"ApplyDocumentUpdates error: {ex.Message}"); }
    }

    public void Dispose() {
        try {
            _batcher?.Dispose();
            _watcher?.Dispose();
            DebugUtil.Print("Incremental", "IncrementalHost disposed");
        } catch (Exception ex) { DebugUtil.Print("Incremental", $"Error during disposal: {ex.Message}"); }
    }
}

