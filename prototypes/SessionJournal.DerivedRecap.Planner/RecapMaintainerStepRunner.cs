using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

internal abstract record RecapMaintainerStepResult {
    private RecapMaintainerStepResult() {
    }

    public sealed record Succeeded(DerivedRecapBlock Candidate)
        : RecapMaintainerStepResult;

    public sealed record MaintainerFailed(string Detail)
        : RecapMaintainerStepResult;

    public sealed record ResultInvalid(string Detail)
        : RecapMaintainerStepResult;
}

/// <summary>
/// Executes and validates one Maintainer step without Store or phase logic.
/// </summary>
internal static class RecapMaintainerStepRunner {
    public static async ValueTask<RecapMaintainerStepResult> RunAsync(
        IRecapBlockMaintainer maintainer,
        MaintainRecapBlockPlan plan,
        ContextHeaderSnapshot priorContext,
        DerivedRecapBlock? currentBlock,
        SessionHistoryPlanningWindow window,
        EventAddress endpoint,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(maintainer);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(priorContext);
        ArgumentNullException.ThrowIfNull(window);

        if (!string.Equals(
                maintainer.Id,
                plan.MaintainerId,
                StringComparison.Ordinal
            )
            || maintainer.Target != plan.Target
            || !string.Equals(
                maintainer.CapabilityFingerprint,
                plan.MaintainerCapabilityFingerprint,
                StringComparison.Ordinal
            )) {
            return new RecapMaintainerStepResult.ResultInvalid(
                "Resolved Maintainer identity does not match the frozen block plan."
            );
        }

        RecapMaintenanceSuccess result;
        try {
            result = await maintainer.MaintainAsync(
                    new RecapMaintenanceEpochInput(
                        priorContext,
                        window.Units
                            .Select(static unit => unit.Message)
                            .ToArray(),
                        EventAddressTextCodec.Format(
                            window.StartExclusive
                        )
                        + ".."
                        + EventAddressTextCodec.Format(
                            window.ObservedRawHead
                        )
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (Exception exception) {
            return new RecapMaintainerStepResult.MaintainerFailed(
                exception.Message
            );
        }

        string? content = result switch {
            RecapMaintenanceSuccess.Updated updated =>
                updated.Content,
            RecapMaintenanceSuccess.KeepUnchanged
                when currentBlock is not null => currentBlock.Content,
            RecapMaintenanceSuccess.KeepUnchanged => null,
            null => null,
            _ => null
        };
        if (result is RecapMaintenanceSuccess.KeepUnchanged
            && currentBlock is null) {
            return new RecapMaintainerStepResult.ResultInvalid(
                "KeepUnchanged requires an existing recap block."
            );
        }

        string? invalidResult = ValidateContent(plan, content);
        if (invalidResult is not null) {
            return new RecapMaintainerStepResult.ResultInvalid(
                invalidResult
            );
        }
        return new RecapMaintainerStepResult.Succeeded(
            DerivedRecapCodec.CreateBlock(
                plan,
                endpoint,
                content!
            )
        );
    }

    internal static ContextHeaderSnapshot GetPriorContext(
        RecapPriorContext prior
    ) => prior switch {
        EmptyRecapPriorContext => ContextHeaderSnapshot.Empty,
        InlineRecapPriorContext inline => inline.Snapshot,
        _ => throw new InvalidDataException(
            "Unsupported Recap prior context."
        )
    };

    private static string? ValidateContent(
        MaintainRecapBlockPlan plan,
        string? content
    ) {
        if (content is null) {
            return "Maintainer returned null.";
        }
        if (string.IsNullOrEmpty(content)) {
            return "Maintainer result content cannot be empty.";
        }
        try {
            if (new UTF8Encoding(false, true).GetByteCount(
                    content
                ) > plan.MaxContentUtf8Bytes) {
                return $"Maintainer result exceeds "
                    + $"{plan.MaxContentUtf8Bytes} UTF-8 bytes.";
            }
        }
        catch (EncoderFallbackException) {
            return "Maintainer result content is not valid UTF-8.";
        }
        return null;
    }
}
