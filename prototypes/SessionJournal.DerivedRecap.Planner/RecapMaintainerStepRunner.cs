using System.Text;
using Atelia.EventJournal;
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
        DerivedRecapBlock? currentBlock,
        SessionHistoryPlanningWindow window,
        EventAddress endpoint,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(maintainer);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(window);

        RecapBlockMaintenanceResult result;
        try {
            result = await maintainer.MaintainAsync(
                    new RecapBlockMaintenanceRequest(
                        new RecentHistorySlice(
                            GetPriorContext(plan.PriorContext),
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
                        new ContextHeaderBlock(
                            currentBlock?.Content ?? string.Empty
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

        string? invalidResult = ValidateResult(plan, result);
        if (invalidResult is not null) {
            return new RecapMaintainerStepResult.ResultInvalid(
                invalidResult
            );
        }
        return new RecapMaintainerStepResult.Succeeded(
            DerivedRecapCodec.CreateBlock(
                plan,
                endpoint,
                result.NewBlock.Text
            )
        );
    }

    private static ContextHeaderSnapshot GetPriorContext(
        RecapPriorContext prior
    ) => prior switch {
        EmptyRecapPriorContext => ContextHeaderSnapshot.Empty,
        InlineRecapPriorContext inline => inline.Snapshot,
        _ => throw new InvalidDataException(
            "Unsupported Recap prior context."
        )
    };

    private static string? ValidateResult(
        MaintainRecapBlockPlan plan,
        RecapBlockMaintenanceResult? result
    ) {
        if (result is null) {
            return "Maintainer returned null.";
        }
        if (!string.Equals(
                result.MaintainerId,
                plan.MaintainerId,
                StringComparison.Ordinal
            )
            || result.Target != plan.Target) {
            return "Maintainer result Id or Target does not match "
                + "the frozen block plan.";
        }
        if (result.NewBlock is null
            || string.IsNullOrEmpty(result.NewBlock.Text)) {
            return "Maintainer result content cannot be empty.";
        }
        if (result.Errors is { Count: > 0 }) {
            return "Maintainer returned errors: "
                + string.Join("; ", result.Errors);
        }
        try {
            if (new UTF8Encoding(false, true).GetByteCount(
                    result.NewBlock.Text
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
