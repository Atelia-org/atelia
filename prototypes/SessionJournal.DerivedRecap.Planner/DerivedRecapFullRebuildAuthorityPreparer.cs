using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

public sealed record DerivedRecapFullRebuildAuthorityPreparation(
    DerivedRecapRebuildSpoolDescriptor Spool,
    SessionSelectedLineageAuditAuthority RawAuthority
);

/// <summary>
/// Explicit operator/rebuild entry for creating or resuming complete raw
/// selected-lineage authority. It never runs planning policy or Maintainers,
/// resets recap truth, selects epoch boundaries, or writes a Building.
/// </summary>
public static class DerivedRecapFullRebuildAuthorityPreparer {
    public static async ValueTask<
        DerivedRecapRebuildSpoolDescriptor
    > BeginAsync(
        SessionJournalEngine engine,
        DerivedRecapRebuildSpoolStore spool,
        DerivedRecapRebuildSpoolLimits limits,
        CancellationToken cancellationToken = default
    ) {
        RequireSameBinding(engine, spool);
        ArgumentNullException.ThrowIfNull(limits);
        SessionSelectedLineageAuditSession audit =
            engine.BeginSelectedLineageAudit(cancellationToken);
        DerivedRecapRebuildSpoolDescriptor descriptor =
            await spool.CreateCampaignAsync(
                    audit.Capture,
                    limits,
                    cancellationToken
                )
                .ConfigureAwait(false);
        return descriptor;
    }

    public static async ValueTask<
        DerivedRecapFullRebuildAuthorityPreparation
    > ResumeAsync(
        SessionJournalEngine engine,
        DerivedRecapRebuildSpoolStore spool,
        string campaignId,
        CancellationToken cancellationToken = default
    ) {
        RequireSameBinding(engine, spool);
        await using DerivedRecapRebuildSpoolWriter writer =
            await spool.OpenWriterAsync(
                    campaignId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        SessionSelectedLineageAuditSession audit =
            engine.ResumeSelectedLineageAudit(
                writer.Checkpoint.Descriptor.Capture,
                writer.ReadCommittedPages(),
                cancellationToken
            );
        return await CaptureAndSealUnderWriterAsync(
                writer,
                audit,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public static async ValueTask<
        SessionSelectedLineageForwardCursor
    > OpenForwardCursorAsync(
        SessionJournalEngine engine,
        DerivedRecapRebuildSpoolStore spool,
        string campaignId,
        CancellationToken cancellationToken = default
    ) {
        RequireSameBinding(engine, spool);
        ISessionSelectedLineageAuditPageSnapshot snapshot =
            await spool.OpenSealedSnapshotAsync(
                    campaignId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        return engine.OpenSelectedLineageForwardCursor(
            snapshot,
            cancellationToken
        );
    }

    private static async ValueTask<
        DerivedRecapFullRebuildAuthorityPreparation
    > CaptureAndSealUnderWriterAsync(
        DerivedRecapRebuildSpoolWriter writer,
        SessionSelectedLineageAuditSession audit,
        CancellationToken cancellationToken
    ) {
        int pageEventCount = writer.Checkpoint.Descriptor.Limits
            .PageEventCount;
        while (!audit.IsCaptureComplete) {
            cancellationToken.ThrowIfCancellationRequested();
            SessionSelectedLineageAuditPage page =
                audit.ReadNextPage(
                    pageEventCount,
                    cancellationToken
                );
            await writer.AppendPageAsync(page, cancellationToken)
                .ConfigureAwait(false);
        }
        SessionSelectedLineageAuditAuthority authority =
            audit.Complete(cancellationToken);
        await writer.SealAsync(authority, cancellationToken)
            .ConfigureAwait(false);
        return new DerivedRecapFullRebuildAuthorityPreparation(
            writer.Checkpoint.Descriptor,
            authority
        );
    }

    private static void RequireSameBinding(
        SessionJournalEngine engine,
        DerivedRecapRebuildSpoolStore spool
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(spool);
        if (!engine.IsReadOnly) {
            throw new ArgumentException(
                "Explicit full-rebuild authority requires a read-only SessionJournalEngine.",
                nameof(engine)
            );
        }
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (engine.BranchRefId != spool.RefId
            || !string.Equals(
                Path.GetFullPath(engine.Path),
                Path.GetFullPath(spool.SessionRepositoryPath),
                comparison
            )) {
            throw new ArgumentException(
                "Full-rebuild engine and spool must bind the same repository and RefId.",
                nameof(spool)
            );
        }
    }
}
