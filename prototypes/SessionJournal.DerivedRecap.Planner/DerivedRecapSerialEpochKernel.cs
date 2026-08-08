using System.Text;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

internal sealed record SerialEpochFailure(
    RecapBlockId RecapBlockId,
    string Code,
    string Detail
);

internal abstract record SerialEpochBlockOutcome(
    RecapBlockId RecapBlockId
) {
    public sealed record ReusedHealthy(RecapBlockId RecapBlockId)
        : SerialEpochBlockOutcome(RecapBlockId);

    public sealed record FinalInstalled(
        RecapBlockId RecapBlockId,
        bool KeptUnchanged
    ) : SerialEpochBlockOutcome(RecapBlockId);

    public sealed record Failed(
        RecapBlockId RecapBlockId,
        SerialEpochFailure Failure
    ) : SerialEpochBlockOutcome(RecapBlockId);
}

internal sealed record SerialEpochKernelResult(
    RecapMaintenanceEpochInput RuntimeInput,
    IReadOnlyList<SerialEpochBlockOutcome> Outcomes,
    SerialEpochFailure? PrimaryFailure
) {
    public bool Succeeded => PrimaryFailure is null;
}

/// <summary>
/// Stage-neutral serial execution kernel for one complete shared epoch.
/// Building and Published Restore retain their own Store authority and pass
/// only an authority-capturing final-install delegate into this kernel.
/// </summary>
internal static class DerivedRecapSerialEpochKernel {
    internal static async ValueTask<SerialEpochKernelResult> ExecuteAsync(
        RecapEpochStoreSnapshot snapshot,
        IRecapBlockMaintainerRegistry registry,
        int maxMaintainerCallsPerEpoch,
        Func<
            RecapEpochBlockInspection,
            DerivedRecapFinalBlock,
            CancellationToken,
            ValueTask<WriteRecapEpochFinalResult>
        > installFinal,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(installFinal);
        if (maxMaintainerCallsPerEpoch <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxMaintainerCallsPerEpoch)
            );
        }
        DerivedRecapV8Codec.ValidateEpochSet(
            snapshot.Manifest,
            snapshot.EpochInput
        );
        if (snapshot.Blocks.Count != snapshot.Manifest.Blocks.Count) {
            throw new InvalidDataException(
                "Epoch inspection does not cover the complete manifest roster."
            );
        }

        RecapMaintenanceEpochInput runtimeInput = ProjectRuntimeInput(
            snapshot.EpochInput
        );
        var outcomes = new SerialEpochBlockOutcome?[snapshot.Blocks.Count];
        var pending = new List<PendingMaintainer>();
        for (int ordinal = 0; ordinal < snapshot.Blocks.Count; ordinal++) {
            RecapEpochBlockInspection inspection = snapshot.Blocks[ordinal];
            RecapEpochBlockDefinition definition =
                snapshot.Manifest.Blocks[ordinal];
            if (inspection.Definition != definition) {
                throw new InvalidDataException(
                    "Epoch inspection order differs from the manifest roster."
                );
            }
            if (inspection.Final is RecapEpochFinalHealth.Healthy) {
                outcomes[ordinal] =
                    new SerialEpochBlockOutcome.ReusedHealthy(
                        definition.RecapBlockId
                    );
                continue;
            }
            if (inspection.WriteAuthority is null) {
                pending.Add(PendingMaintainer.Unavailable(
                    ordinal,
                    inspection,
                    new SerialEpochFailure(
                        definition.RecapBlockId,
                        "FinalSlotUnavailable",
                        "Final slot cannot be read safely and has no write authority."
                    )
                ));
                continue;
            }
            if (!registry.TryResolve(
                    definition.MaintainerId,
                    definition.Target,
                    definition.MaintainerCapabilityFingerprint,
                    out IRecapBlockMaintainer? maintainer
                )) {
                pending.Add(PendingMaintainer.Unavailable(
                    ordinal,
                    inspection,
                    new SerialEpochFailure(
                        definition.RecapBlockId,
                        "MaintainerUnavailable",
                        "Frozen Maintainer binding is unavailable."
                    )
                ));
                continue;
            }
            if (!string.Equals(
                    maintainer.Id,
                    definition.MaintainerId,
                    StringComparison.Ordinal
                )
                || maintainer.Target != definition.Target
                || !string.Equals(
                    maintainer.CapabilityFingerprint,
                    definition.MaintainerCapabilityFingerprint,
                    StringComparison.Ordinal
                )) {
                pending.Add(PendingMaintainer.Unavailable(
                    ordinal,
                    inspection,
                    new SerialEpochFailure(
                        definition.RecapBlockId,
                        "MaintainerIdentityMismatch",
                        "Resolved Maintainer identity differs from frozen roster."
                    )
                ));
                continue;
            }
            pending.Add(new PendingMaintainer(
                ordinal,
                inspection,
                maintainer,
                null
            ));
        }

        int requiredCalls = pending.Count;
        if (requiredCalls > maxMaintainerCallsPerEpoch) {
            throw new InvalidDataException(
                $"Pending epoch roster requires {requiredCalls} calls; "
                + $"limit is {maxMaintainerCallsPerEpoch}."
            );
        }

        // Resolve and validate the complete pending roster before the first
        // remote call. A preflight defect means zero calls for this attempt.
        SerialEpochFailure? preflightFailure = pending
            .Where(static item => item.PreflightFailure is not null)
            .OrderBy(static item => item.Ordinal)
            .Select(static item => item.PreflightFailure)
            .FirstOrDefault();
        if (preflightFailure is not null) {
            foreach (PendingMaintainer item in pending) {
                SerialEpochFailure failure = item.PreflightFailure
                    ?? new SerialEpochFailure(
                        item.Inspection.Definition.RecapBlockId,
                        "PreflightAborted",
                        "No Maintainer was called because complete-roster preflight failed."
                    );
                outcomes[item.Ordinal] =
                    new SerialEpochBlockOutcome.Failed(
                        failure.RecapBlockId,
                        failure
                    );
            }
            return Finish(runtimeInput, outcomes, preflightFailure);
        }

        foreach (PendingMaintainer item in pending) {
            cancellationToken.ThrowIfCancellationRequested();
            RecapEpochBlockDefinition definition = item.Inspection.Definition;
            RecapMaintenanceSuccess result;
            try {
                result = await item.Maintainer!.MaintainAsync(
                        runtimeInput,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested) {
                throw;
            }
            catch (Exception exception)
                when (RecapNonFatalException.IsCatchable(exception)) {
                var failure = new SerialEpochFailure(
                    definition.RecapBlockId,
                    "MaintainerFailed",
                    exception.Message
                );
                outcomes[item.Ordinal] =
                    new SerialEpochBlockOutcome.Failed(
                        definition.RecapBlockId,
                        failure
                    );
                continue;
            }

            bool keptUnchanged = result
                is RecapMaintenanceSuccess.KeepUnchanged;
            string? content = result switch {
                RecapMaintenanceSuccess.Updated updated => updated.Content,
                RecapMaintenanceSuccess.KeepUnchanged =>
                    FindPriorContent(snapshot.EpochInput, definition),
                _ => null
            };
            string? invalid = ValidateContent(definition, content);
            if (invalid is not null) {
                var failure = new SerialEpochFailure(
                    definition.RecapBlockId,
                    "MaintainerResultInvalid",
                    invalid
                );
                outcomes[item.Ordinal] =
                    new SerialEpochBlockOutcome.Failed(
                        definition.RecapBlockId,
                        failure
                    );
                continue;
            }

            DerivedRecapFinalBlock candidate;
            try {
                candidate = DerivedRecapV8Codec.CreateFinalBlock(
                    snapshot.Manifest,
                    definition,
                    content!
                );
            }
            catch (Exception exception)
                when (exception is InvalidDataException
                      or ArgumentException
                      or EncoderFallbackException) {
                var failure = new SerialEpochFailure(
                    definition.RecapBlockId,
                    "MaintainerResultInvalid",
                    exception.Message
                );
                outcomes[item.Ordinal] =
                    new SerialEpochBlockOutcome.Failed(
                        definition.RecapBlockId,
                        failure
                    );
                continue;
            }

            WriteRecapEpochFinalResult write;
            try {
                write = await installFinal(
                        item.Inspection,
                        candidate,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested) {
                throw;
            }
            catch (Exception exception)
                when (RecapNonFatalException.IsCatchable(exception)) {
                var failure = new SerialEpochFailure(
                    definition.RecapBlockId,
                    "FinalWriteFailed",
                    exception.Message
                );
                outcomes[item.Ordinal] =
                    new SerialEpochBlockOutcome.Failed(
                        definition.RecapBlockId,
                        failure
                    );
                continue;
            }
            switch (write) {
                case WriteRecapEpochFinalResult.Installed:
                case WriteRecapEpochFinalResult.AlreadyHealthy:
                    outcomes[item.Ordinal] =
                        new SerialEpochBlockOutcome.FinalInstalled(
                            definition.RecapBlockId,
                            keptUnchanged
                        );
                    break;
                default:
                    var failure = new SerialEpochFailure(
                        definition.RecapBlockId,
                        "FinalWriteRejected",
                        DescribeWriteFailure(write)
                    );
                    outcomes[item.Ordinal] =
                        new SerialEpochBlockOutcome.Failed(
                            definition.RecapBlockId,
                            failure
                        );
                    break;
            }
        }
        SerialEpochFailure? primary = outcomes
            .OfType<SerialEpochBlockOutcome.Failed>()
            .Select(static outcome => outcome.Failure)
            .FirstOrDefault();
        return Finish(runtimeInput, outcomes, primary);
    }

    internal static RecapMaintenanceEpochInput ProjectRuntimeInput(
        DerivedRecapEpochInput input
    ) {
        DerivedRecapV8Codec.ValidateEpochInput(input);
        var pack = new ContextHeaderPack();
        if (input.Previous is RecapEpochPrevious.Prior prior) {
            foreach (PriorRecapBlockSnapshot block in prior.Pack.Blocks) {
                GetCarrier(pack, block.Target.Carrier).Add(
                    block.Target.BlockKey,
                    new ContextHeaderBlock(block.Content)
                );
            }
        }
        return new RecapMaintenanceEpochInput(
            pack.Render(),
            input.HistoryMessages,
            sourceId: input.PayloadSha256
        );
    }

    private static OrderedDictionary<string, ContextHeaderBlock> GetCarrier(
        ContextHeaderPack pack,
        ContextHeaderCarrier carrier
    ) => carrier switch {
        ContextHeaderCarrier.System => pack.System,
        ContextHeaderCarrier.Observation => pack.Observation,
        ContextHeaderCarrier.Action => pack.Action,
        _ => throw new InvalidDataException(
            "Prior recap block has an unsupported carrier."
        )
    };

    private static string? FindPriorContent(
        DerivedRecapEpochInput input,
        RecapEpochBlockDefinition definition
    ) {
        if (input.Previous is not RecapEpochPrevious.Prior prior) {
            return null;
        }
        PriorRecapBlockSnapshot? block = prior.Pack.Blocks
            .SingleOrDefault(candidate =>
                candidate.RecapBlockId == definition.RecapBlockId);
        return block is not null && block.Target == definition.Target
            ? block.Content
            : null;
    }

    private static string? ValidateContent(
        RecapEpochBlockDefinition definition,
        string? content
    ) {
        if (string.IsNullOrEmpty(content)) {
            return "Maintainer must return non-empty block content; bootstrap KeepUnchanged is invalid.";
        }
        try {
            int bytes = new UTF8Encoding(false, true).GetByteCount(content);
            if (bytes > definition.MaxContentUtf8Bytes) {
                return $"Maintainer result is {bytes} UTF-8 bytes; block limit is "
                    + $"{definition.MaxContentUtf8Bytes}.";
            }
        }
        catch (EncoderFallbackException) {
            return "Maintainer result content is not valid UTF-8.";
        }
        return null;
    }

    private static string DescribeWriteFailure(
        WriteRecapEpochFinalResult result
    ) => result switch {
        WriteRecapEpochFinalResult.HealthyConflict =>
            "A different healthy final already exists.",
        WriteRecapEpochFinalResult.Stale stale =>
            $"Final write authority is stale: {stale.CurrentStateToken}",
        WriteRecapEpochFinalResult.Invalid invalid => invalid.Detail,
        _ => $"Unexpected final write result '{result.GetType().Name}'."
    };

    private static SerialEpochKernelResult Finish(
        RecapMaintenanceEpochInput runtimeInput,
        SerialEpochBlockOutcome?[] outcomes,
        SerialEpochFailure? primary
    ) {
        if (outcomes.Any(static outcome => outcome is null)) {
            throw new InvalidDataException(
                "Serial epoch kernel did not produce one outcome per roster member."
            );
        }
        return new SerialEpochKernelResult(
            runtimeInput,
            Array.AsReadOnly(outcomes.Cast<SerialEpochBlockOutcome>().ToArray()),
            primary
        );
    }

    private sealed record PendingMaintainer(
        int Ordinal,
        RecapEpochBlockInspection Inspection,
        IRecapBlockMaintainer? Maintainer,
        SerialEpochFailure? PreflightFailure
    ) {
        internal static PendingMaintainer Unavailable(
            int ordinal,
            RecapEpochBlockInspection inspection,
            SerialEpochFailure failure
        ) => new(ordinal, inspection, null, failure);
    }
}
