namespace Atelia.StateJournal;

/// <summary>Repository commit 在 candidate 已写出后失败的阶段。</summary>
public enum RepositoryCommitFailurePhase {
    /// <summary>尚未记录或未来版本无法映射到当前枚举的阶段。</summary>
    Unknown = 0,

    /// <summary>将 candidate data 建立为 branch metadata 的 durable 前置条件时失败。</summary>
    DataDurability,

    /// <summary>验证 branch physical HEAD 是否仍等于调用方预期值时失败。</summary>
    VerifyExpectedHead,

    /// <summary>备份旧 primary ref 时失败。</summary>
    BackupPreviousRef,

    /// <summary>发布新的 primary ref 时失败。</summary>
    PublishPrimaryRef,

    /// <summary>确保 backup ref 存在时失败。</summary>
    EnsureBackupRef,

    /// <summary>追加 branch reflog 时失败。</summary>
    AppendReflog,
}

/// <summary>失败返回时，candidate 是否可能已被 branch primary ref 发布。</summary>
public enum RepositoryCommitPublicationState {
    /// <summary>无法判定 publication 状态；调用方必须按可能已发布处理。</summary>
    Unknown = 0,

    /// <summary>已确定 candidate 尚未进入 primary ref 的原子替换边界。</summary>
    NotPublished,

    /// <summary>已进入原子替换边界，但未观察到其完成；磁盘上可能是 parent 或 candidate。</summary>
    MayHavePublished,

    /// <summary>primary ref 的原子替换已返回成功；后续 metadata 步骤失败。</summary>
    Published,
}

/// <summary>
/// <see cref="Repository.Commit(DurableObject)"/> 在 candidate address 已产生后遇到的结构化失败。
/// </summary>
/// <remarks>
/// 该错误绝不表示可以透明重试。调用方必须 dispose 当前 Repository、重新 open，
/// 再把 physical HEAD 裁决为 expected parent、exact candidate 或 irreconcilable。
/// 即使 HEAD 是 exact candidate，应用仍应核验自己保存的 canonical envelope bytes 与 domain post-state。
/// </remarks>
public sealed record RepositoryCommitError : AteliaError {
    /// <summary>稳定错误码。</summary>
    public const string StableErrorCode = "SJ.Repository.CommitFailed";

    internal RepositoryCommitError(
        string branchName,
        CommitAddress? expectedHeadAddress,
        CommitAddress candidateAddress,
        RepositoryCommitFailurePhase failurePhase,
        RepositoryCommitPublicationState publicationState,
        Exception failure
    ) : base(
        StableErrorCode,
        BuildMessage(branchName, candidateAddress, failurePhase, publicationState, failure),
        RecoveryHint: "Dispose this Repository, reopen it, and compare the physical branch HEAD with ExpectedHeadAddress and CandidateAddress. Do not retry transparently.",
        Details: BuildDetails(branchName, expectedHeadAddress, candidateAddress, failurePhase, publicationState)
    ) {
        BranchName = branchName;
        ExpectedHeadAddress = expectedHeadAddress;
        CandidateAddress = candidateAddress;
        FailurePhase = failurePhase;
        PublicationState = publicationState;
    }

    /// <summary>Commit 试图推进的 branch。</summary>
    public string BranchName { get; }

    /// <summary>Commit 开始发布 metadata 时预期的 physical parent；null 表示 unborn。</summary>
    public CommitAddress? ExpectedHeadAddress { get; }

    /// <summary>已经由 data write 产生、需要在 reopen 后参与裁决的 candidate。</summary>
    public CommitAddress CandidateAddress { get; }

    /// <summary>失败阶段。</summary>
    public RepositoryCommitFailurePhase FailurePhase { get; }

    /// <summary>失败时对 primary ref publication 的保守判断。</summary>
    public RepositoryCommitPublicationState PublicationState { get; }

    /// <summary>当前 Repository 已 poison；继续操作前必须 dispose/open。</summary>
    public bool RequiresRepositoryReopen => true;

    /// <summary>Commit 不是幂等重试协议，不能在当前实例或 reopen 后透明重试。</summary>
    public bool CanRetryTransparently => false;

    /// <summary>candidate 不能被排除为已经发布。</summary>
    public bool MayHavePublished => PublicationState is not RepositoryCommitPublicationState.NotPublished;

    private static string BuildMessage(
        string branchName,
        CommitAddress candidateAddress,
        RepositoryCommitFailurePhase failurePhase,
        RepositoryCommitPublicationState publicationState,
        Exception failure
    ) {
        return $"Commit candidate {candidateAddress} for branch '{branchName}' failed during {failurePhase} "
            + $"(publication={publicationState}): {failure.Message}";
    }

    private static IReadOnlyDictionary<string, string> BuildDetails(
        string branchName,
        CommitAddress? expectedHeadAddress,
        CommitAddress candidateAddress,
        RepositoryCommitFailurePhase failurePhase,
        RepositoryCommitPublicationState publicationState
    ) {
        var mayHavePublished = publicationState is not RepositoryCommitPublicationState.NotPublished;
        return new Dictionary<string, string>(StringComparer.Ordinal) {
            [nameof(BranchName)] = branchName,
            [nameof(ExpectedHeadAddress)] = expectedHeadAddress?.ToString() ?? "null",
            [nameof(CandidateAddress)] = candidateAddress.ToString(),
            [nameof(FailurePhase)] = failurePhase.ToString(),
            [nameof(PublicationState)] = publicationState.ToString(),
            [nameof(RequiresRepositoryReopen)] = bool.TrueString,
            [nameof(CanRetryTransparently)] = bool.FalseString,
            [nameof(MayHavePublished)] = mayHavePublished.ToString(),
        };
    }
}
