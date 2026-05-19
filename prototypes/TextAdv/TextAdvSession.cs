using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal sealed class TextAdvSession {
    public Repository Repo { get; }
    public DurableDict<string> Root { get; }
    public string BranchName { get; }
    public TerminalHelpMode HelpMode { get; }
    public CommitAddress? HeadAddress => Root.Revision.HeadAddress;

    private TextAdvSession(
        Repository repo,
        DurableDict<string> root,
        string branchName,
        TerminalHelpMode helpMode
    ) {
        Repo = repo;
        Root = root;
        BranchName = branchName;
        HelpMode = helpMode;
    }

    internal static AteliaResult<TextAdvSession> CreateNew(string repoDir) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        var createResult = Repository.Create(repoDir);
        if (!createResult.TryGetValue(out var repo) || repo is null) {
            return new TextAdvError(
                "TextAdv.SessionCreateFailed",
                $"无法创建游戏存档目录 '{repoDir}'：{createResult.Error?.Message ?? "未知错误"}",
                "检查目标目录是否为空、可写，并确认没有其他进程占用。",
                Cause: createResult.Error
            );
        }

        try {
            var root = GameSimulation.CreateNewWorld(repo);
            var sessionResult = new TextAdvSession(repo, root, TextAdvSessionState.DefaultBranchName, TerminalHelpMode.Off)
                .Persist(repoDir);
            return sessionResult.IsFailure ? FailAndDispose(repo, sessionResult.Error!) : sessionResult;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) {
            repo.Dispose();
            return new TextAdvError(
                "TextAdv.SessionBootstrapFailed",
                $"创建初始世界状态失败：{ex.Message}",
                "检查存档目录是否可写，以及初始世界构建过程中是否产生了非法状态。"
            );
        }
        catch {
            repo.Dispose();
            throw;
        }
    }

    internal static AteliaResult<TextAdvSession> Load(string repoDir) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        var openResult = Repository.Open(repoDir);
        if (!openResult.TryGetValue(out var repo) || repo is null) {
            return new TextAdvError(
                "TextAdv.SessionOpenFailed",
                $"无法打开游戏存档目录 '{repoDir}'：{openResult.Error?.Message ?? "未知错误"}",
                "确认目录是有效的 TextAdv / StateJournal 存档，并且没有被其他进程占用。",
                Cause: openResult.Error
            );
        }

        try {
            TextAdvSessionState sessionState;
            try {
                sessionState = TextAdvSessionStore.Load(repoDir);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) {
                return FailAndDispose(repo, new TextAdvError(
                    "TextAdv.SessionMetadataLoadFailed",
                    $"无法读取游戏会话元数据：{ex.Message}",
                    "检查 .textadv-session.json 是否损坏、是否有权限读取，或删除后重新设置当前会话。",
                    Cause: new TextAdvError("TextAdv.SessionMetadataLoadException", ex.Message)
                ));
            }

            var branchName = sessionState.GetNormalizedBranchName();
            AteliaResult<Revision> revisionResult;
            if (repo.HasBranch(branchName)) {
                revisionResult = repo.CheckoutBranch(branchName);
            }
            else if (string.Equals(branchName, TextAdvSessionState.DefaultBranchName, StringComparison.Ordinal)) {
                revisionResult = repo.GetOrCreateBranch(branchName);
            }
            else {
                return FailAndDispose(repo, new TextAdvError(
                    "TextAdv.SessionBranchMissing",
                    $"游戏会话指向了不存在的分支 '{branchName}'。",
                    "确认会话元数据中的 branch 名称是否正确，或手动切回一个存在的 branch 后再继续。"
                ));
            }

            if (!revisionResult.TryGetValue(out var revision) || revision is null) {
                return FailAndDispose(repo, new TextAdvError(
                    "TextAdv.SessionBranchOpenFailed",
                    $"无法打开游戏分支 '{branchName}'：{revisionResult.Error?.Message ?? "未知错误"}",
                    "检查该 branch 指向的版本是否仍然存在，或重新选择一个可用分支。",
                    Cause: revisionResult.Error
                ));
            }

            var sessionResult = CreateFromRevision(repo, revision, branchName, sessionState.GetNormalizedHelpMode());
            return sessionResult.IsFailure ? FailAndDispose(repo, sessionResult.Error!) : sessionResult;
        }
        catch {
            repo.Dispose();
            throw;
        }
    }

    internal AteliaResult<TextAdvSession> LoadVersionAsNewBranch(CommitAddress versionAddress) {
        var branchName = CreateLoadedBranchName(Repo, versionAddress);
        var revisionResult = Repo.CreateBranch(branchName, versionAddress);
        if (!revisionResult.TryGetValue(out var revision) || revision is null) { return revisionResult.Error!; }

        return CreateFromRevision(Repo, revision, branchName, HelpMode);
    }

    internal TextAdvSession WithHelpMode(TerminalHelpMode helpMode)
        => new(Repo, Root, BranchName, helpMode);

    internal AteliaResult<TextAdvSession> Persist(string repoDir) {
        var state = new TextAdvSessionState {
            CurrentBranchName = BranchName,
            HelpMode = HelpMode.ToString()
        };

        var saveResult = SaveSessionState(repoDir, state);
        return saveResult.IsFailure ? saveResult.Error! : this;
    }

    private static AteliaResult<TextAdvSession> CreateFromRevision(
        Repository repo,
        Revision revision,
        string branchName,
        TerminalHelpMode helpMode
    ) {
        var loadedRoot = revision.GraphRoot as DurableDict<string>;
        if (loadedRoot is null) {
            return new TextAdvError(
                "TextAdv.SessionRootTypeInvalid",
                $"游戏分支 '{branchName}' 中的根状态不是 DurableDict<string>。",
                "确认该 branch 对应的是 TextAdv 游戏存档，而不是其他原型写入的 StateJournal 数据。"
            );
        }

        return new TextAdvSession(repo, loadedRoot, branchName, helpMode);
    }

    private static AteliaResult<TextAdvSessionState> SaveSessionState(string repoDir, TextAdvSessionState sessionState) {
        try {
            TextAdvSessionStore.Save(repoDir, sessionState);
            return sessionState;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) {
            return new TextAdvError(
                "TextAdv.SessionMetadataSaveFailed",
                $"无法保存游戏会话元数据：{ex.Message}",
                "检查存档目录是否可写，或稍后重试。",
                Cause: new TextAdvError("TextAdv.SessionMetadataSaveException", ex.Message)
            );
        }
    }

    private static AteliaResult<TextAdvSession> FailAndDispose(Repository repo, AteliaError error) {
        repo.Dispose();
        return error;
    }

    private static string CreateLoadedBranchName(Repository repo, CommitAddress versionAddress) {
        var ticketText = versionAddress.CommitTicket.Ticket.Serialize().ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        var baseName = $"loads/{DateTime.UtcNow:yyyyMMdd-HHmmss}-seg{versionAddress.SegmentNumber}-{ticketText}";
        var candidate = baseName;
        var suffix = 2;
        while (repo.HasBranch(candidate)) {
            candidate = $"{baseName}-{suffix.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            suffix++;
        }

        return candidate;
    }
}
