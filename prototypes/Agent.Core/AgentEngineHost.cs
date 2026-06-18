using Atelia.Agent.Core.App;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.Persistence;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.Agent.Core;

/// <summary>
/// 用真实 <see cref="Repository"/> 承载 <see cref="AgentEngine"/> durable state 的宿主。
/// </summary>
/// <remarks>
/// 本类型把 create/open、root 恢复、profile registry 注入，以及 repo-backed StepAsync 自动 commit 串成一条稳定生命周期。
/// </remarks>
public sealed class AgentEngineHost : IDisposable {
    private const string DefaultBranchName = "main";

    private readonly Repository _repo;
    private readonly AgentEngineStateRoot _stateRoot;
    private readonly AgentEngine _engine;
    private bool _disposed;

    private AgentEngineHost(
        string repoDir,
        string branchName,
        Repository repo,
        AgentEngineStateRoot stateRoot,
        AgentEngine engine,
        AgentEngineHostRuntime runtime
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(stateRoot);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(runtime);

        RepoDir = repoDir;
        BranchName = branchName;
        Runtime = runtime;
        _repo = repo;
        _stateRoot = stateRoot;
        _engine = engine;
    }

    public string RepoDir { get; }

    public string BranchName { get; }

    public AgentEngineHostRuntime Runtime { get; }

    public AgentEngine Engine {
        get {
            EnsureNotDisposed();
            return _engine;
        }
    }

    public AgentEngineStateRoot StateRoot {
        get {
            EnsureNotDisposed();
            return _stateRoot;
        }
    }

    public static AgentEngineHost CreateNew(
        string repoDir,
        AgentEngineHostCreateOptions? options = null,
        AgentEngineHostRuntime? runtime = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        options ??= new AgentEngineHostCreateOptions();
        runtime ??= new AgentEngineHostRuntime();

        if (Directory.Exists(repoDir) && Directory.EnumerateFileSystemEntries(repoDir).Any()) {
            throw new InvalidOperationException(
                $"Repository directory '{repoDir}' already exists and is not empty. Use OpenExisting or a dev open-or-create flow instead."
            );
        }

        var repo = Repository.Create(repoDir).Unwrap();
        try {
            var revision = repo.CreateBranch(options.BranchName).Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);
            var stateRoot = AgentEngineStateRoot.Create(workspaceRoot, options.SystemPrompt);
            repo.Commit(stateRoot.Root).Unwrap();

            var engine = runtime.BuildRepositoryBackedEngine(repo, stateRoot);
            return new AgentEngineHost(repoDir, options.BranchName, repo, stateRoot, engine, runtime);
        }
        catch {
            repo.Dispose();
            throw;
        }
    }

    public static AgentEngineHost OpenExisting(
        string repoDir,
        AgentEngineHostRuntime? runtime = null,
        string branchName = DefaultBranchName
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        runtime ??= new AgentEngineHostRuntime();

        var repo = Repository.Open(repoDir).Unwrap();
        try {
            var revision = repo.CheckoutBranch(branchName).Unwrap();
            if (revision.GraphRoot is not DurableDict<string> root) { throw new InvalidDataException("Repository graph root is not a valid agent-engine-state."); }

            var workspaceRoot = AgentWorkspaceRoot.FromRoot(root);
            var stateRoot = AgentEngineStateRoot.FromWorkspaceRoot(workspaceRoot);
            var engine = runtime.BuildRepositoryBackedEngine(repo, stateRoot);
            return new AgentEngineHost(repoDir, branchName, repo, stateRoot, engine, runtime);
        }
        catch {
            repo.Dispose();
            throw;
        }
    }

    public Task<AgentStepResult> StepAsync(LlmProfile profile, CancellationToken cancellationToken = default) {
        EnsureNotDisposed();
        return _engine.StepAsync(profile, cancellationToken);
    }

    public Task<AgentStepResult> StepAsync(
        LlmProfile profile,
        CompletionStreamObserver? completionObserver,
        CancellationToken cancellationToken = default
    ) {
        EnsureNotDisposed();
        return _engine.StepAsync(profile, completionObserver, cancellationToken);
    }

    public void SaveAndCommit() {
        EnsureNotDisposed();
        _engine.PersistStableBoundaryIfAttached();
    }

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        _engine.DetachPersistenceSession();
        _repo.Dispose();
    }

    private void EnsureNotDisposed() {
        if (_disposed) { throw new ObjectDisposedException(nameof(AgentEngineHost)); }
    }
}

/// <summary>
/// 创建新的 repo-backed <see cref="AgentEngineHost"/> 时使用的参数。
/// </summary>
public sealed record AgentEngineHostCreateOptions(
    string BranchName = "main",
    string? SystemPrompt = null
);

/// <summary>
/// 打开或创建 repo-backed <see cref="AgentEngineHost"/> 时使用的运行时依赖包。
/// </summary>
public sealed class AgentEngineHostRuntime {
    public AgentEngineHostRuntime(
        LlmProfileRegistry? profileRegistry = null,
        IEnumerable<IApp>? initialApps = null,
        IEnumerable<ITool>? initialTools = null,
        IIdleObservationProvider? idleProvider = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        AutoCompactionOptions? autoCompaction = null
    ) {
        ProfileRegistry = profileRegistry;
        InitialApps = Freeze(initialApps);
        InitialTools = Freeze(initialTools);
        IdleProvider = idleProvider;
        UtcNowProvider = utcNowProvider;
        AutoCompaction = autoCompaction;
    }

    public LlmProfileRegistry? ProfileRegistry { get; }

    public IReadOnlyList<IApp> InitialApps { get; }

    public IReadOnlyList<ITool> InitialTools { get; }

    public IIdleObservationProvider? IdleProvider { get; }

    public Func<DateTimeOffset>? UtcNowProvider { get; }

    public AutoCompactionOptions? AutoCompaction { get; }

    internal AgentEngine BuildRepositoryBackedEngine(Repository repo, AgentEngineStateRoot stateRoot) {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(stateRoot);

        return AgentEngine.CreateForRepository(
            repo,
            stateRoot,
            ProfileRegistry,
            InitialApps,
            InitialTools,
            IdleProvider,
            UtcNowProvider,
            AutoCompaction
        );
    }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T>? source) where T : class {
        if (source is null) { return Array.Empty<T>(); }

        return source.Where(static item => item is not null).ToArray();
    }
}
