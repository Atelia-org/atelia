using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using CodeCortex.Core.Hashing;
using CodeCortex.Core.Index;
using CodeCortex.Core.IO;
using CodeCortex.Core.Outline;
using CodeCortex.Workspace.Incremental;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Atelia.Diagnostics;

namespace CodeCortex.Service;

/// <summary>
/// 封装文件监控和增量更新管线，集成到Service进程中
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

        // 从环境变量读取debounce时间，默认400ms
        var debounceMs = Environment.GetEnvironmentVariable("CODECORTEX_DEBOUNCE_MS");
        var debounceTime = int.TryParse(debounceMs, out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.FromMilliseconds(400);

        DebugUtil.Print("Incremental", $"Debounce time: {debounceTime.TotalMilliseconds}ms");

        // 初始化增量处理组件
        _batcher = new DebounceFileChangeBatcher(debounceTime);
        var watchRoot = Directory.Exists(_solutionRoot) ? _solutionRoot : (Path.GetDirectoryName(_solutionRoot) ?? Directory.GetCurrentDirectory());
        _watcher = new SolutionFileWatcher(watchRoot, _batcher);
        _classifier = new ChangeClassifier();
        _impactAnalyzer = new ImpactAnalyzer(_fs);
        _processor = new IncrementalProcessor();

        // 初始化处理器组件
        _hasher = new TypeHasher();
        _outlineExtractor = new OutlineExtractor();

        // 订阅文件变更事件
        _batcher.Flushed += OnFileChangesFlushed;

        DebugUtil.Print("Incremental", $"IncrementalHost initialized for {_solutionRoot}");
    }

    /// <summary>
    /// 创建IncrementalHost实例
    /// </summary>
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

    /// <summary>
    /// 当前监控队列深度
    /// </summary>
    public int QueueDepth => _batcher.PendingCount;

    /// <summary>
    /// 启动文件监控
    /// </summary>
    public void Start() {
        try {
            _watcher.Start();
            DebugUtil.Print("Watcher", $"File watcher started for {_solutionRoot}");
        } catch (Exception ex) {
            DebugUtil.Print("Watcher", $"Failed to start file watcher: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 处理文件变更批次
    /// </summary>
    private void OnFileChangesFlushed(IReadOnlyList<RawFileChange> rawChanges) {
        try {
            DebugUtil.Print("Incremental", $"Processing {rawChanges.Count} file changes");

            _indexManager.UpdateIndex(
                index => {
                    // 1. 分类文件变更
                    var classified = _classifier.Classify(rawChanges);
                    DebugUtil.Print("Incremental", $"Classified changes: {classified.Count}");

                    // 1.5 将磁盘上的最新源码应用到 Roslyn Solution（确保后续符号/哈希来自最新文本）
                    ApplyDocumentUpdates(classified);

                    // 2. 分析影响的类型
                    var impact = _impactAnalyzer.Analyze(
                        index,
                        classified,
                        ResolveProject,
                        CancellationToken.None
                    );

                    if (impact.AffectedTypeIds.Count == 0 && impact.RemovedTypeIds.Count == 0) {
                        DebugUtil.Print("Incremental", "No affected types found");
                        return;
                    }

                    DebugUtil.Print("Incremental",
                        $"Impact analysis: Affected={impact.AffectedTypeIds.Count}, Removed={impact.RemovedTypeIds.Count}"
                    );

                    // 3. 执行增量处理（批次级编译缓存 + 失败查找缓存）
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

                    bool SeenNegative(ProjectId pid, string fqn) {
                        return negativeLookup.TryGetValue(pid, out var set) && set.Contains(fqn);
                    }
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

                    var result = _processor.Process(
                        index,
                        impact,
                        _hasher,
                        _outlineExtractor,
                        ResolveUsingCache,
                        _outlineDir,
                        _fs,
                        CancellationToken.None
                    );

                    DebugUtil.Print("Incremental",
                        $"Incremental update completed: Changed={result.ChangedTypeCount}, " +
                        $"Removed={result.RemovedTypeCount}, Duration={result.DurationMs}ms, " +
                        $"OutlineWritten={result.OutlineWrittenCount}, OutlineSkipped={result.OutlineSkippedCount}"
                    );

                    DebugUtil.Print("Incremental", $"Compilation cache: requests={compRequests}, hits={compCacheHits}, builds={compBuilds}");
                    DebugUtil.Print("Incremental", $"Negative lookup: skips={negativeSkips}, marked={negativeMarks}");
                    DebugUtil.Print("Incremental", $"Resolve: attempts={resolveAttempts}, hits={resolveHits}");

                }
            );
        } catch (Exception ex) {
            DebugUtil.Print("Incremental", $"Incremental update failed: {ex.Message}");
            // 不重新抛出异常，避免影响Service稳定性
        }
    }

    /// <summary>
    /// 初始化Solution（用于项目解析）
    /// </summary>
    private async Task InitializeSolutionAsync() {
        try {
            DebugUtil.Print("Incremental", $"Loading solution for project resolution: {_solutionRoot}");
            var loader = new CodeCortex.Workspace.MsBuildWorkspaceLoader(CodeCortex.Workspace.MsBuildMode.Auto);
            var loaded = await loader.LoadAsync(_solutionRoot).ConfigureAwait(false);
            _solution = loaded.Solution;
            DebugUtil.Print("Incremental", $"Solution loaded: {loaded.Projects.Count} projects");
        } catch (Exception ex) {
            DebugUtil.Print("Incremental", $"Failed to load solution: {ex.Message}");
            DebugUtil.Print("Incremental", $"Exception details: {ex}");
            _solution = null;
            // 不重新抛出异常，让Service继续运行，只是没有项目解析功能
        }
    }

    /// <summary>
    /// 解析项目（根据文件路径查找包含该文件的项目）
    /// </summary>
    private Microsoft.CodeAnalysis.Project? ResolveProject(string filePath) {
        if (_solution == null) {
            DebugUtil.Print("Incremental", "Solution not loaded, cannot resolve project");
            return null;
        }

        // 查找包含指定文件的项目
        foreach (var project in _solution.Projects) {
            if (project.Documents.Any(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase))) {
                DebugUtil.Print("Incremental", $"Resolved project: {project.Name} for file: {filePath}");
                return project;
            }
        }

        DebugUtil.Print("Incremental", $"No project found for file: {filePath}");
        return null;
    }

    /// <summary>
    /// 根据TypeId解析类型符号（使用传入的index，避免锁冲突）
    /// </summary>
    private INamedTypeSymbol? ResolveTypeByIdWithIndex(string typeId, CodeCortexIndex index) {
        if (_solution == null) {
            DebugUtil.Print("Incremental", "Solution not loaded, cannot resolve type by ID");
            return null;
        }

        // 从传入的索引中查找TypeId对应的FQN（避免再次获取读锁）
        var typeEntry = index.Types.FirstOrDefault(t => t.Id == typeId);

        if (typeEntry == null) {
            DebugUtil.Print("Incremental", $"Type not found in index: {typeId}");
            return null;
        }

        DebugUtil.Print("Incremental", $"Resolving type: {typeId} -> {typeEntry.Fqn}");

        // 在所有项目中搜索该类型
        foreach (var project in _solution.Projects) {
            try {
                var compilation = project.GetCompilationAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                if (compilation == null) {
                    continue;
                }

                var symbol = compilation.GetTypeByMetadataName(typeEntry.Fqn);
                if (symbol != null) {
                    DebugUtil.Print("Incremental", $"Found symbol: {typeEntry.Fqn} in project {project.Name}");
                    return symbol;
                }
            } catch (Exception ex) {
                DebugUtil.Print("Incremental", $"Error getting compilation for project {project.Name}: {ex.Message}");
                continue;
            }
        }
        DebugUtil.Print("Incremental", $"Symbol not found in any project: {typeEntry.Fqn}");
        return null;
    }

    /// <summary>
    /// 将磁盘上的最新源代码应用到当前 _solution，以便 Compilation/符号反映最新更改
    /// </summary>
    private void ApplyDocumentUpdates(IReadOnlyList<ClassifiedFileChange> classified) {
        try {
            if (_solution == null) {
                return;
            }

            var paths = classified
                .Where(c => c.Kind != ClassifiedKind.Delete)
                .Select(c => c.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0) {
                return;
            }

            var solution = _solution;
            int applied = 0;
            foreach (var path in paths) {
                try {
                    var doc = solution.Projects.SelectMany(p => p.Documents)
                        .FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
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
                } catch (Exception ex) {
                    DebugUtil.Print("Incremental", $"ApplyDocumentUpdates failed for {path}: {ex.Message}");
                }
            }
            if (applied > 0) {
                _solution = solution;
                DebugUtil.Print("Incremental", $"Applied document updates: {applied}");
            }
        } catch (Exception ex) {
            DebugUtil.Print("Incremental", $"ApplyDocumentUpdates error: {ex.Message}");
        }
    }

    /// <summary>
    /// 根据TypeId解析类型符号（保留原方法用于其他地方）
    /// </summary>
    private INamedTypeSymbol? ResolveTypeById(string typeId) {
        if (_solution == null) {
            DebugUtil.Print("Incremental", "Solution not loaded, cannot resolve type by ID");
            return null;
        }
        // 使用索引快照，避免在后续解析期间持有锁
        var snapshot = _indexManager.ReadIndex(index => index);
        return ResolveTypeByIdWithIndex(typeId, snapshot);
    }

    public void Dispose() {
        try {
            _batcher?.Dispose();
            _watcher?.Dispose();
            DebugUtil.Print("Incremental", "IncrementalHost disposed");
        } catch (Exception ex) {
            DebugUtil.Print("Incremental", $"Error during disposal: {ex.Message}");
        }
    }
}
