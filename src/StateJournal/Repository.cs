using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Atelia.Data;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

/// <summary>进程独占的单写者目录——管理 repo 级的 branches、segment 轮换与崩溃恢复。</summary>
/// <remarks>
/// 目录结构：
/// <code>
/// {repoDir}/
///   state-journal.lock            - 身份标记 + 进程独占锁（OS 文件锁）
///   refs/
///     branches/
///   recent/
///     {segNumHex8}.sj.rbf         - 文件名 = segment number（1-based, hex8）
/// </code>
/// </remarks>
public sealed partial class Repository : IDisposable {
    private const string LockFileName = "state-journal.lock";
    private const string RefsDirName = "refs";
    private const string BranchesDirName = "branches";
    private const string RecentDirName = "recent";
    private const long DefaultRotationThreshold = 2L * 1024 * 1024 * 1024; // 2 GB

    public string DirectoryPath { get; }

    // 保护 Repository 自身的可变元数据：branches、已打开 segment、active segment。
    // 它不试图让 Revision / DurableObject 的任意读写自动线程安全。
    private readonly Lock _gate = new(); // System.Threading.Lock
    private readonly Dictionary<string, BranchState> _branches;
    private readonly SegmentCatalog _segments;
    private readonly FileStream _lockStream;
    private uint _maxCommittedSegmentNumber;
    private bool _disposed;
    private bool _isPoisoned;
    private long _rotationThreshold = DefaultRotationThreshold;

    /// <summary>Branch 名称的最大长度。</summary>
    public const int MaxBranchNameLength = 256;

    // 白名单：仅允许 ASCII 字母、数字、. _ - / 组成的名称。
    // 锚定 ^…$ 保证整串匹配。
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._\-/]*$")]
    private static partial Regex BranchNamePattern();

    /// <summary>
    /// 验证 branch 名称是否符合命名规则。
    /// - 仅允许 ASCII 字母、数字、<c>.</c> <c>_</c> <c>-</c> <c>/</c>
    /// - 必须以字母或数字开头
    /// - 不得以 <c>/</c> <c>.</c> <c>-</c> 结尾
    /// - 不得包含连续 <c>//</c>
    /// - 任何 <c>/</c> 分隔的 component 不得为 <c>.</c> 或 <c>..</c>
    /// - 不得包含 <c>.lock</c> 后缀的 component（与 git 兼容）
    /// - 长度 1–256
    /// </summary>
    /// <returns>合法返回 null；不合法返回描述原因的错误消息。</returns>
    public static string? ValidateBranchName(string? name) {
        if (string.IsNullOrWhiteSpace(name)) { return "Branch name must not be empty or whitespace."; }
        if (name.Length > MaxBranchNameLength) { return $"Branch name exceeds maximum length of {MaxBranchNameLength}."; }

        if (!BranchNamePattern().IsMatch(name)) { return "Branch name contains illegal characters. Only ASCII letters, digits, '.', '_', '-', and '/' are allowed, and it must start with a letter or digit."; }

        char last = name[^1];
        if (last is '/' or '.' or '-') { return $"Branch name must not end with '{last}'."; }

        foreach (var component in name.Split('/')) {
            if (component.Length == 0) { return "Branch name contains consecutive '/' separators."; }
            if (component is "." or "..") { return $"Branch name component '{component}' is not allowed (path traversal)."; }
            if (component.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) { return $"Branch name component '{component}' must not end with '.lock'."; }
        }

        return null;
    }

    /// <summary>返回文件轮换阈值（字节），超过此大小时下次 Commit 切换到新 segment。默认 2GB。</summary>
    public long GetRotationThreshold() {
        using var scope = _gate.EnterScope();
        // 允许_disposed / _isPoisoned状态读取用于debug / log用途。
        return _rotationThreshold;
    }

    /// <summary>设置文件轮换阈值（字节），超过此大小时下次 Commit 切换到新 segment。默认 2GB。</summary>
    public void SetRotationThreshold(long value) {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, SizedPtr.MaxOffset);

        using var scope = _gate.EnterScope();
        if (!EnsureUsable(out var err)) { throw new InvalidOperationException(err.Message); }
        _rotationThreshold = value;
    }

    private Repository(
        string directoryPath,
        FileStream lockStream,
        Dictionary<string, BranchState> branches,
        SegmentCatalog segments
    ) {
        DirectoryPath = directoryPath;
        _lockStream = lockStream;
        _branches = branches;
        _segments = segments;

        uint maxSeg = 0;
        foreach (var bs in branches.Values) {
            if (bs.Head is { } h && h.SegmentNumber > maxSeg) { maxSeg = h.SegmentNumber; }
        }
        _maxCommittedSegmentNumber = maxSeg;
    }

    /// <summary>
    /// 在指定目录创建一个全新的 Repository。
    /// 初始状态不自动创建任何 branch；调用方需显式调用 <see cref="CreateBranch(string)"/>。
    /// </summary>
    public static AteliaResult<Repository> Create(string directoryPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullPath = Path.GetFullPath(directoryPath);

        if (File.Exists(fullPath)) {
            return new SjRepositoryError(
                $"Repository path '{fullPath}' is a file, not a directory.",
                RecoveryHint: "Choose a non-existent path or an empty directory."
            );
        }

        Directory.CreateDirectory(fullPath);
        if (!DirectoryIsEmpty(fullPath)) {
            return new SjRepositoryError(
                $"Repository directory '{fullPath}' must be empty before creation.",
                RecoveryHint: "Remove existing contents, or call Repository.Open() for an existing repository."
            );
        }

        FileStream lockStream;
        try {
            lockStream = AcquireLock(fullPath, FileMode.CreateNew);
        }
        catch (IOException ex) {
            return new SjRepositoryError(
                $"Failed to acquire lock on '{fullPath}': {ex.Message}",
                RecoveryHint: "Another process may be creating this repository, or the directory contents changed concurrently. Retry after the directory becomes empty."
            );
        }

        try {
            if (!DirectoryContainsOnlyLockFile(fullPath)) {
                lockStream.Dispose();
                TryDeleteLockFile(fullPath);
                return new SjRepositoryError(
                    $"Repository directory '{fullPath}' must be empty before creation.",
                    RecoveryHint: "Remove existing contents, or call Repository.Open() for an existing repository."
                );
            }

            var branchesDir = GetBranchesDirectoryPath(fullPath);
            var recentDir = Path.Combine(fullPath, RecentDirName);
            Directory.CreateDirectory(branchesDir);
            Directory.CreateDirectory(recentDir);

            var segments = SegmentCatalog.CreateNew(fullPath);

            var branches = new Dictionary<string, BranchState>(StringComparer.Ordinal);

            return new Repository(
                fullPath,
                lockStream,
                branches,
                segments
            );
        }
        catch (Exception ex) {
            lockStream.Dispose();
            TryDeleteLockFile(fullPath);
            return new SjRepositoryError(
                $"Failed to create repository at '{fullPath}': {ex.Message}",
                RecoveryHint: "Check directory permissions and disk space."
            );
        }
    }

    /// <summary>
    /// 打开已有的 Repository。恢复 branches 与 segment 布局。
    /// 打开后只加载 branch 元数据；具体 Revision 按需通过 <see cref="CheckoutBranch(string)"/> 打开。
    /// </summary>
    public static AteliaResult<Repository> Open(string directoryPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullPath = Path.GetFullPath(directoryPath);

        if (File.Exists(fullPath)) {
            return new SjRepositoryError(
                $"Repository path '{fullPath}' is a file, not a directory.",
                RecoveryHint: "Open a repository directory path instead."
            );
        }

        if (!Directory.Exists(fullPath)) {
            return new SjRepositoryError(
                $"Repository directory not found: '{fullPath}'.",
                RecoveryHint: "Use Repository.Create() to create a new repository."
            );
        }

        FileStream lockStream;
        try {
            lockStream = AcquireLock(fullPath, FileMode.Open);
        }
        catch (FileNotFoundException) {
            return new SjRepositoryError(
                $"Directory '{fullPath}' is not a StateJournal repository.",
                RecoveryHint: "Use Repository.Create() to create a new repository."
            );
        }
        catch (IOException ex) {
            return new SjRepositoryError(
                $"Failed to acquire lock on '{fullPath}': {ex.Message}",
                RecoveryHint: "Another process may be using this repository. Close it and retry."
            );
        }

        SegmentCatalog? segments = null;
        try {
            var openLayout = RepositoryOpenValidator.Validate(fullPath);
            var branches = BuildBranchStates(openLayout.Branches);

            segments = SegmentCatalog.OpenFromScan(fullPath, openLayout.Segments);

            return new Repository(
                fullPath,
                lockStream,
                branches,
                segments
            );
        }
        catch (Exception ex) {
            segments?.Dispose();
            lockStream.Dispose();
            return new SjRepositoryError(
                $"Failed to open repository at '{fullPath}': {ex.Message}",
                RecoveryHint: "The repository may be corrupted. Check refs/branches/ and recent/."
            );
        }
    }

    /// <summary>
    /// 对外的简洁提交入口。从 <paramref name="graphRoot"/> 反查所属 Revision，再映射到对应 branch。
    /// </summary>
    public AteliaResult<CommitOutcome> Commit(DurableObject graphRoot) {
        using var scope = _gate.EnterScope();
        if (!EnsureUsable(out var err)) { return err; }
        ArgumentNullException.ThrowIfNull(graphRoot);

        var branchName = graphRoot.Revision.BranchName;
        if (branchName is null || !_branches.ContainsKey(branchName)) {
            return new SjRepositoryError(
                "The specified graphRoot does not belong to a Revision managed by this Repository.",
                RecoveryHint: "Open or create the object graph from this Repository before calling Commit()."
            );
        }

        return CommitCore(branchName, graphRoot);
    }

    /// <summary>
    /// 内部入口：推进指定 branch。
    /// </summary>
    internal AteliaResult<CommitOutcome> Commit(string branchName, DurableObject graphRoot) {
        using var scope = _gate.EnterScope();
        if (!EnsureUsable(out var err)) { return err; }
        ArgumentNullException.ThrowIfNull(graphRoot);
        return CommitCore(branchName, graphRoot);
    }

    /// <summary>
    /// checkout 指定 branch 对应的工作会话；若尚未加载则按需从持久化快照恢复。
    /// </summary>
    public AteliaResult<Revision> CheckoutBranch(string branchName) {
        using var scope = _gate.EnterScope();
        if (!EnsureUsable(out var err)) { return err; }
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        return GetOrCheckoutBranchCore(branchName);
    }

    /// <summary>
    /// 创建一个新的空 branch，并返回其独立的工作会话。
    /// 新 branch 初始为 unborn 状态，不复制任何未提交工作态。
    /// </summary>
    public AteliaResult<Revision> CreateBranch(string branchName) {
        using var scope = _gate.EnterScope();
        if (!EnsureUsable(out var err)) { return err; }
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        return CreateBranchCore(branchName, sourceBranchName: null);
    }

    /// <summary>
    /// 从已有 branch 的当前已提交 HEAD 派生一个新 branch，并返回新 branch 的独立工作会话。
    /// 不复制源 branch 的未提交工作态。
    /// </summary>
    public AteliaResult<Revision> CreateBranch(string branchName, string fromBranchName) {
        using var scope = _gate.EnterScope();
        if (!EnsureUsable(out var err)) { return err; }
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromBranchName);

        return CreateBranchCore(branchName, fromBranchName);
    }

    private AteliaResult<CommitOutcome> CommitCore(string branchName, DurableObject graphRoot) {
        ArgumentNullException.ThrowIfNull(graphRoot);

        if (!_branches.TryGetValue(branchName, out var branchState) || branchState.LoadedRevision is null) {
            return new SjRepositoryError(
                $"Branch '{branchName}' is not loaded.",
                RecoveryHint: "Checkout the branch before committing."
            );
        }

        var revision = branchState.LoadedRevision;
        if (!ReferenceEquals(graphRoot.Revision, revision)) {
            return new SjRepositoryError(
                $"graphRoot belongs to a different Revision than branch '{branchName}'.",
                RecoveryHint: "Commit the object graph through the Repository that owns its Revision."
            );
        }

        var shouldRotate = HasCommittedBranchPointingIntoActiveSegment() && _segments.ShouldRotate(_rotationThreshold);

        AteliaResult<CommitOutcome> commitResult;
        uint targetSegmentNumber;

        if (shouldRotate) {
            SegmentCatalog.PendingRotation pendingRotation;
            try {
                pendingRotation = _segments.OpenPendingRotation();
            }
            catch (Exception ex) {
                return new SjRepositoryError(
                    $"Failed to create new segment: {ex.Message}",
                    RecoveryHint: "Check disk space and permissions."
                );
            }

            commitResult = revision.SaveAs(graphRoot, pendingRotation.File);
            if (commitResult.IsSuccess) {
                _segments.CommitRotation(pendingRotation);
                targetSegmentNumber = pendingRotation.SegmentNumber;
            }
            else {
                _segments.RollbackRotation(pendingRotation);
                return commitResult;
            }
        }
        else {
            commitResult = ReferenceEquals(revision.BoundFile, _segments.ActiveFile)
                ? revision.Commit(graphRoot)
                : revision.SaveAs(graphRoot, _segments.ActiveFile);
            if (commitResult.IsFailure) { return commitResult; }
            targetSegmentNumber = _segments.ActiveSegmentNumber;
        }

        var expectedHead = branchState.Head;
        var newHead = CommitAddress.Create(targetSegmentNumber, commitResult.Value.HeadCommitId);

        try {
            CompareAndSwapBranchAtomically(DirectoryPath, branchName, expectedHead, newHead);
        }
        catch (Exception ex) {
            _isPoisoned = true;
            return new SjRepositoryError(
                $"Commit data was written, but advancing branch '{branchName}' failed: {ex.Message}",
                RecoveryHint: "Dispose this Repository instance and reopen it before continuing."
            );
        }

        branchState.Head = newHead;
        if (newHead.SegmentNumber > _maxCommittedSegmentNumber) { _maxCommittedSegmentNumber = newHead.SegmentNumber; }
        return commitResult;
    }

    private AteliaResult<Revision> CreateBranchCore(string branchName, string? sourceBranchName) {
        var nameError = ValidateBranchName(branchName);
        if (nameError is not null) {
            return new SjRepositoryError(
                $"Invalid branch name '{branchName}': {nameError}",
                RecoveryHint: "Use Repository.ValidateBranchName() to check name validity before creating."
            );
        }

        if (_branches.ContainsKey(branchName)) {
            return new SjRepositoryError(
                $"Branch '{branchName}' already exists.",
                RecoveryHint: "Choose a different branch name or checkout the existing branch."
            );
        }

        BranchState sourceState;
        if (sourceBranchName is null) {
            sourceState = new BranchState {
                BranchName = branchName,
                Head = null,
                LoadedRevision = null,
            };
        }
        else {
            if (!_branches.TryGetValue(sourceBranchName, out var existingSource)) {
                return new SjRepositoryError(
                    $"Source branch '{sourceBranchName}' was not found.",
                    RecoveryHint: "Checkout or create the source branch before branching from it."
                );
            }

            sourceState = existingSource;
        }

        AteliaResult<Revision> revisionResult = CreateDetachedRevisionForBranch(sourceState);
        if (revisionResult.IsFailure) { return revisionResult.Error!; }

        var revision = revisionResult.Value!;
        revision.BranchName = branchName;
        var newBranchState = new BranchState {
            BranchName = branchName,
            Head = sourceState.Head,
            LoadedRevision = revision,
        };

        try {
            WriteNewBranchAtomically(DirectoryPath, branchName, newBranchState.Head);
        }
        catch (Exception ex) {
            return new SjRepositoryError(
                $"Failed to create branch '{branchName}': {ex.Message}",
                RecoveryHint: "Check branch naming conflicts and repository metadata files."
            );
        }

        _branches.Add(branchName, newBranchState);
        if (sourceState.Head is { } srcHead && srcHead.SegmentNumber > _maxCommittedSegmentNumber) {
            _maxCommittedSegmentNumber = srcHead.SegmentNumber;
        }
        return revision;
    }

    public void Dispose() {
        using var scope = _gate.EnterScope();
        if (_disposed) { return; }
        _disposed = true;

        _segments.Dispose();
        _lockStream.Dispose();
    }

    private AteliaResult<Revision> CreateDetachedRevisionForBranch(BranchState branchState) {
        try {
            if (branchState.Head is not { } head) { return new Revision(_segments.ActiveFile); }

            var file = _segments.GetFileForSegment(head.SegmentNumber);
            return Revision.Open(head.CommitId, file);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) {
            return new SjRepositoryError(
                $"Failed to materialize Revision for branch '{branchState.BranchName}': {ex.Message}",
                RecoveryHint: "Check the branch metadata and referenced segment files."
            );
        }
    }

    private AteliaResult<Revision> GetOrCheckoutBranchCore(string branchName) {
        if (!_branches.TryGetValue(branchName, out var branchState)) {
            return new SjRepositoryError(
                $"Unknown branch '{branchName}'.",
                RecoveryHint: "This is an internal consistency error."
            );
        }

        if (branchState.LoadedRevision is not null) { return branchState.LoadedRevision; }
        var revisionResult = CreateDetachedRevisionForBranch(branchState);
        if (revisionResult.IsFailure) { return revisionResult.Error!; }

        branchState.LoadedRevision = revisionResult.Value!;
        branchState.LoadedRevision.BranchName = branchName;
        return branchState.LoadedRevision;
    }

    private bool EnsureUsable([NotNullWhen(false)] out AteliaError? error) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isPoisoned) {
            error = new SjRepositoryError(
                "This Repository instance is in a poisoned state after a metadata divergence.",
                RecoveryHint: "Dispose it and reopen the repository before continuing."
            );
            return false;
        }
        error = null;
        return true;
    }
}
