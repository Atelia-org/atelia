using System.Text.Json;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.MemoPod;

namespace Atelia.Galatea.Server;

internal enum GalateaMemoRecallDiagnosticOutcome {
    NotScheduled,
    NoMatch,
    AllFiltered,
    Selected,
    Failed,
}

internal enum GalateaMemoRecallNotScheduledReason {
    MaintenanceMode,
    ProviderDisabled,
    UnsupportedTrigger,
    Recovery,
}

internal enum GalateaMemoRecallFailureStage {
    ContextConstruction,
    QueryRendering,
    ClientResolution,
    SelectorExecution,
    PlannerEvaluation,
    ProviderResultValidation,
}

internal enum GalateaMemoRecallFailureKind {
    ProviderFailure,
    InvalidModelOutput,
    LocalLimitExceeded,
    StorageFailure,
    StateInvalid,
    ContextUnavailable,
    ContractViolation,
    Unexpected,
}

internal sealed class GalateaMemoRecallStageException(
    GalateaMemoRecallFailureStage stage,
    GalateaMemoRecallFailureKind failureKind,
    Exception innerException
) : Exception(
    "Memo recall failed during a classified planning stage.",
    innerException
) {
    internal GalateaMemoRecallFailureStage Stage { get; } = stage;
    internal GalateaMemoRecallFailureKind FailureKind { get; } = failureKind;
}

internal sealed record GalateaMemoRecallDiagnostic(
    GalateaMemoRecallDiagnosticOutcome Outcome,
    GalateaMemoRecallNotScheduledReason? NotScheduledReason = null,
    GalateaMemoRecallFailureStage? FailureStage = null,
    GalateaMemoRecallFailureKind? FailureKind = null,
    int? NominatedCount = null,
    int? EvaluatedCount = null,
    int? SelectedCount = null,
    int? MissingTitleSkipCount = null,
    int? OriginVisibleSkipCount = null,
    int? PriorRecallSkipCount = null,
    int? ObservationBudgetSkipCount = null,
    int? UnexaminedCount = null
) {
    internal static GalateaMemoRecallDiagnostic NotScheduled(
        GalateaMemoRecallNotScheduledReason reason
    ) => new(
        GalateaMemoRecallDiagnosticOutcome.NotScheduled,
        NotScheduledReason: reason
    );

    internal static GalateaMemoRecallDiagnostic Completed(
        GalateaMemoRecallPlanningResult planning
    ) {
        ArgumentNullException.ThrowIfNull(planning);
        return new(
            planning.Outcome switch {
                GalateaMemoRecallPlanningOutcome.NoMatch =>
                    GalateaMemoRecallDiagnosticOutcome.NoMatch,
                GalateaMemoRecallPlanningOutcome.AllFiltered =>
                    GalateaMemoRecallDiagnosticOutcome.AllFiltered,
                GalateaMemoRecallPlanningOutcome.Selected =>
                    GalateaMemoRecallDiagnosticOutcome.Selected,
                _ => throw new InvalidOperationException(
                    "Unknown Memo recall planning outcome."
                ),
            },
            NominatedCount: planning.NominatedCount,
            EvaluatedCount: planning.EvaluatedCount,
            SelectedCount: planning.SelectedCount,
            MissingTitleSkipCount: planning.MissingTitleSkipCount,
            OriginVisibleSkipCount: planning.OriginVisibleSkipCount,
            PriorRecallSkipCount: planning.PriorRecallSkipCount,
            ObservationBudgetSkipCount:
                planning.ObservationBudgetSkipCount,
            UnexaminedCount: planning.UnexaminedCount
        );
    }

    internal static GalateaMemoRecallDiagnostic Failed(
        GalateaMemoRecallFailureStage stage,
        GalateaMemoRecallFailureKind failureKind
    ) => new(
        GalateaMemoRecallDiagnosticOutcome.Failed,
        FailureStage: stage,
        FailureKind: failureKind
    );
}

internal static class GalateaMemoRecallDiagnosticRenderer {
    internal const string EventName = "memo-recall-planning";
    internal const int SchemaVersion = 1;
    private static readonly JsonNamingPolicy CodePolicy =
        JsonNamingPolicy.KebabCaseLower;

    internal static string Render(GalateaMemoRecallDiagnostic diagnostic) {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return JsonSerializer.Serialize(new {
            @event = EventName,
            schemaVersion = SchemaVersion,
            outcome = Code(diagnostic.Outcome),
            notScheduledReason = Code(diagnostic.NotScheduledReason),
            failureStage = Code(diagnostic.FailureStage),
            failureKind = Code(diagnostic.FailureKind),
            nominatedCount = diagnostic.NominatedCount,
            evaluatedCount = diagnostic.EvaluatedCount,
            selectedCount = diagnostic.SelectedCount,
            missingTitleSkipCount = diagnostic.MissingTitleSkipCount,
            originVisibleSkipCount = diagnostic.OriginVisibleSkipCount,
            priorRecallSkipCount = diagnostic.PriorRecallSkipCount,
            observationBudgetSkipCount =
                diagnostic.ObservationBudgetSkipCount,
            unexaminedCount = diagnostic.UnexaminedCount,
        });
    }

    private static string Code<T>(T value) where T : struct, Enum =>
        CodePolicy.ConvertName(value.ToString());

    private static string? Code<T>(T? value) where T : struct, Enum =>
        value is { } present ? Code(present) : null;
}

internal static class GalateaMemoRecallFailureClassifier {
    internal static GalateaMemoRecallFailureKind Classify(
        Exception exception
    ) {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch {
            MemoRecallException recall => recall.FailureKind switch {
                MemoRecallFailureKind.ProviderFailure =>
                    GalateaMemoRecallFailureKind.ProviderFailure,
                MemoRecallFailureKind.InvalidModelOutput =>
                    GalateaMemoRecallFailureKind.InvalidModelOutput,
                MemoRecallFailureKind.LocalLimitExceeded =>
                    GalateaMemoRecallFailureKind.LocalLimitExceeded,
                _ => GalateaMemoRecallFailureKind.Unexpected,
            },
            CharacterNoteDefaultPodAccessException access =>
                access.Kind switch {
                    CharacterNoteDefaultPodFailureKind.NotFound
                        or CharacterNoteDefaultPodFailureKind.IoFailure =>
                        GalateaMemoRecallFailureKind.StorageFailure,
                    CharacterNoteDefaultPodFailureKind.UnsafePath
                        or CharacterNoteDefaultPodFailureKind.InvalidDocument =>
                        GalateaMemoRecallFailureKind.StateInvalid,
                    _ => GalateaMemoRecallFailureKind.Unexpected,
                },
            CharacterMemoryStoreQuarantinedException =>
                GalateaMemoRecallFailureKind.StateInvalid,
            GalateaTurnException =>
                GalateaMemoRecallFailureKind.ContextUnavailable,
            InvalidDataException => GalateaMemoRecallFailureKind.StateInvalid,
            IOException => GalateaMemoRecallFailureKind.StorageFailure,
            ArgumentException or InvalidOperationException =>
                GalateaMemoRecallFailureKind.ContractViolation,
            _ => GalateaMemoRecallFailureKind.Unexpected,
        };
    }
}
