using Atelia.EventJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

internal sealed class HistoryRecentReserveAuthorityToken {
    private int _active = 1;
    internal bool IsActive => Volatile.Read(ref _active) == 1;
    internal void Deactivate() => Interlocked.Exchange(ref _active, 0);
}

internal sealed class HistoryRecentReservePolicy {
    internal HistoryRecentReservePolicy(
        string canonicalRepositoryPath,
        RefId refId,
        long cadenceGeneration,
        string cadenceDomainDigest,
        PartitionPolicyRevision expectedPolicy,
        HistoryLoadUnit minimumRecentHistoryLoad,
        HistoryRecentReserveAuthorityToken authorityToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRepositoryPath);
        CanonicalRepositoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(canonicalRepositoryPath));
        RefId = HistoryTimelineSyntax.RequireRefId(refId);
        if (cadenceGeneration < 0) {
            throw new ArgumentOutOfRangeException(nameof(cadenceGeneration));
        }
        CadenceGeneration = cadenceGeneration;
        CadenceDomainDigest = HistoryTimelineSyntax.RequireSha256(
            cadenceDomainDigest,
            nameof(cadenceDomainDigest));
        ExpectedPolicy = expectedPolicy
            ?? throw new ArgumentNullException(nameof(expectedPolicy));
        if (minimumRecentHistoryLoad.Value < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(minimumRecentHistoryLoad));
        }
        MinimumRecentHistoryLoad = minimumRecentHistoryLoad;
        AuthorityToken = authorityToken
            ?? throw new ArgumentNullException(nameof(authorityToken));
    }

    internal string CanonicalRepositoryPath { get; }
    internal RefId RefId { get; }
    internal long CadenceGeneration { get; }
    internal string CadenceDomainDigest { get; }
    internal PartitionPolicyRevision ExpectedPolicy { get; }
    internal HistoryLoadUnit MinimumRecentHistoryLoad { get; }
    internal HistoryRecentReserveAuthorityToken AuthorityToken { get; }
    internal bool IsRequired => MinimumRecentHistoryLoad.Value != 0;

    internal bool IsExactFor(
        TimelineHeadRef head,
        PartitionPolicyRevision policy
    ) => !IsRequired
        ? AuthorityToken.IsActive
            && RefId == head.RefId
            && policy.TimelineId == head.TimelineId
        : AuthorityToken.IsActive
        && RefId == head.RefId
        && ExpectedPolicy == policy
        && ExpectedPolicy.TimelineId == head.TimelineId
        && string.Equals(
            ExpectedPolicy.PolicyDigest,
            head.ActivePartitionPolicyDigest,
            StringComparison.Ordinal);
}

public sealed record HistoryRecentReserveShortfall(
    HistoryLoadUnit CandidateLoad,
    HistoryLoadUnit Retained,
    HistoryLoadUnit Required
);

internal sealed class HistoryRecentReserveProof {
    private readonly HistoryRowProposal? _testProposal;
    private readonly IHistoryTimelineRawFence? _testRawFence;

    private HistoryRecentReserveProof(
        HistoryRecentReservePolicy policy,
        TimelineHeadRef expectedHead,
        EventAddress capturedRawHead,
        HistorySegmentDescriptor descriptor,
        HistoryLoadUnit retained
    ) {
        Policy = policy;
        ExpectedHead = expectedHead;
        CapturedRawHead = capturedRawHead;
        RowId = descriptor.RowId;
        DescriptorDigest = descriptor.DescriptorDigest;
        Retained = retained;
    }

    private HistoryRecentReserveProof(
        HistoryRowProposal testProposal,
        IHistoryTimelineRawFence testRawFence
    ) {
        _testProposal = testProposal;
        _testRawFence = testRawFence;
        Policy = null!;
        ExpectedHead = testProposal.ExpectedHead;
        CapturedRawHead = testProposal.CapturedSelectedRawHead;
        RowId = testProposal.Descriptor.RowId;
        DescriptorDigest = testProposal.Descriptor.DescriptorDigest;
        Retained = new HistoryLoadUnit(0);
    }

    internal HistoryRecentReservePolicy Policy { get; }
    internal TimelineHeadRef ExpectedHead { get; }
    internal EventAddress CapturedRawHead { get; }
    internal HistoryRowId RowId { get; }
    internal HistorySegmentDescriptorDigest DescriptorDigest { get; }
    internal HistoryLoadUnit Retained { get; }

    internal static HistoryRecentReserveProof Create(
        HistoryRecentReservePolicy policy,
        TimelineHeadRef expectedHead,
        EventAddress capturedRawHead,
        HistorySegmentDescriptor descriptor,
        HistoryLoadUnit retained
    ) {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(expectedHead);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.RefId != expectedHead.RefId
            || descriptor.TimelineId != expectedHead.TimelineId
            || descriptor.PreviousRowId != expectedHead.HeadRowId
            || !policy.IsExactFor(expectedHead, policy.ExpectedPolicy)
            || policy.IsRequired && !string.Equals(
                policy.ExpectedPolicy.PolicyDigest,
                descriptor.PartitionPolicyDigestAtCreation,
                StringComparison.Ordinal)
            || policy.IsRequired && !string.Equals(
                policy.ExpectedPolicy.HistoryLoadEstimatorId,
                descriptor.HistoryLoadEstimatorId,
                StringComparison.Ordinal)
            || retained.Value
                < policy.MinimumRecentHistoryLoad.Value) {
            throw new InvalidDataException(
                "Recent-reserve proof does not bind the exact row scope.");
        }
        return new HistoryRecentReserveProof(
            policy,
            expectedHead,
            capturedRawHead,
            descriptor,
            retained);
    }

    internal static HistoryRecentReserveProof CreateForTest(
        HistoryRowProposal proposal,
        IHistoryTimelineRawFence rawFence
    ) => new(proposal, rawFence);

    internal bool IsBoundToAuthority(
        HistoryRecentReserveAuthorityToken authorityToken
    ) => _testProposal is null
        && ReferenceEquals(Policy.AuthorityToken, authorityToken);

    internal bool IsExactFor(
        HistoryRowProposal proposal,
        IHistoryTimelineRawFence rawFence
    ) => _testProposal is not null
        ? ReferenceEquals(proposal, _testProposal)
            && ReferenceEquals(rawFence, _testRawFence)
        : proposal.ExpectedHead == ExpectedHead
        && Policy.AuthorityToken.IsActive
        && string.Equals(
            rawFence.CanonicalRepositoryPath,
            Policy.CanonicalRepositoryPath,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal)
        && proposal.CapturedSelectedRawHead == CapturedRawHead
        && rawFence.RefId == Policy.RefId
        && rawFence.CapturedHead == CapturedRawHead
        && proposal.Descriptor.RowId == RowId
        && proposal.Descriptor.DescriptorDigest == DescriptorDigest
        && proposal.Descriptor.RefId == Policy.RefId
        && (!Policy.IsRequired || string.Equals(
            proposal.Descriptor.PartitionPolicyDigestAtCreation,
            Policy.ExpectedPolicy.PolicyDigest,
            StringComparison.Ordinal))
        && (!Policy.IsRequired || string.Equals(
            proposal.Descriptor.HistoryLoadEstimatorId,
            Policy.ExpectedPolicy.HistoryLoadEstimatorId,
            StringComparison.Ordinal))
        && Retained.Value >= Policy.MinimumRecentHistoryLoad.Value;
}
