using Atelia.Galatea.Prompts;
using Atelia.Galatea.RecapGrid;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;

namespace Atelia.Galatea.Server;

internal sealed class GalateaRecapGridTargetExpectation {
    private GalateaRecapGridTargetExpectation(
        BuildTargetDigest targetDigest
    ) {
        if (targetDigest.Value is null) {
            throw new ArgumentException(
                "Target digest must not be default.",
                nameof(targetDigest)
            );
        }
        TargetDigest = targetDigest;
    }

    internal BuildTargetDigest TargetDigest { get; }

    internal static GalateaRecapGridTargetExpectation ForCharacterName(
        GalateaCharacterName characterName
    ) {
        ArgumentNullException.ThrowIfNull(characterName);
        if (!GalateaRecapGridAssets.TryCreateRegistrationBundle(
                GalateaRecapGridAssets.RollingRewriteZhCnV6,
                new GalateaRecapGridAssetParameters(characterName),
                out RecapGridControlRegistrationBundle? bundle)
            || bundle is null) {
            throw new InvalidDataException(
                "The current Galatea RecapGrid asset is unavailable."
            );
        }
        return ForTarget(BuildTarget.Create(bundle.Definitions.Select(
            static definition => new BuildTargetColumn(
                definition.LogicalColumnId,
                definition.Digest
            )
        )));
    }

    internal static GalateaRecapGridTargetExpectation ForTarget(
        BuildTarget target
    ) {
        ArgumentNullException.ThrowIfNull(target);
        return new GalateaRecapGridTargetExpectation(target.Digest);
    }
}

internal abstract record GalateaRecapGridTargetAlignment {
    private GalateaRecapGridTargetAlignment() { }

    internal sealed record Aligned(
        BuildTargetDigest TargetDigest
    ) : GalateaRecapGridTargetAlignment;

    internal sealed record Unprovisioned(
        string Component
    ) : GalateaRecapGridTargetAlignment;

    internal sealed record NoActive : GalateaRecapGridTargetAlignment;

    internal sealed record Mismatch(
        BuildTargetDigest Expected,
        BuildTargetDigest Actual
    ) : GalateaRecapGridTargetAlignment;

    internal sealed record Busy(
        string Component
    ) : GalateaRecapGridTargetAlignment;

    internal sealed record UnsupportedSchema(
        string Component,
        int SchemaVersion
    ) : GalateaRecapGridTargetAlignment;

    internal sealed record Disposed : GalateaRecapGridTargetAlignment;

    internal sealed record Invalid(
        string Component,
        string Code,
        string? Detail = null
    ) : GalateaRecapGridTargetAlignment;
}

internal static class GalateaRecapGridTargetInspector {
    internal static GalateaRecapGridTargetAlignment Inspect(
        SessionJournalReadView selectedRef,
        GalateaRecapGridTargetExpectation expectation
    ) {
        ArgumentNullException.ThrowIfNull(selectedRef);
        ArgumentNullException.ThrowIfNull(expectation);
        RecapGridControlReaderOpenResult opened =
            RecapGridControlFactory.OpenReader(
                selectedRef.Path,
                selectedRef.BranchRefId
            );
        if (opened is not RecapGridControlReaderOpenResult.Opened available) {
            return opened switch {
                RecapGridControlReaderOpenResult.Absent => new
                    GalateaRecapGridTargetAlignment.Unprovisioned("control"),
                RecapGridControlReaderOpenResult.TimelineAbsent => new
                    GalateaRecapGridTargetAlignment.Unprovisioned("timeline"),
                RecapGridControlReaderOpenResult.Busy => new
                    GalateaRecapGridTargetAlignment.Busy("control"),
                RecapGridControlReaderOpenResult.TimelineUnsupportedSchema value
                    => new GalateaRecapGridTargetAlignment.UnsupportedSchema(
                        "timeline",
                        value.SchemaVersion
                    ),
                RecapGridControlReaderOpenResult.UnsupportedSchema value
                    => new GalateaRecapGridTargetAlignment.UnsupportedSchema(
                        "control",
                        value.SchemaVersion
                    ),
                RecapGridControlReaderOpenResult.Invalid value => new
                    GalateaRecapGridTargetAlignment.Invalid(
                        "control",
                        value.Code,
                        value.Detail
                    ),
                _ => new GalateaRecapGridTargetAlignment.Invalid(
                    "control",
                    "ControlReaderOpenOutcomeInvalid"
                )
            };
        }
        using RecapGridControlReaderHandle handle = available.Handle;
        RecapGridControlSnapshotResult snapshot = handle.Reader.ReadSnapshot();
        if (snapshot is not RecapGridControlSnapshotResult.Available current) {
            return snapshot switch {
                RecapGridControlSnapshotResult.Busy => new
                    GalateaRecapGridTargetAlignment.Busy("control-snapshot"),
                RecapGridControlSnapshotResult.UnsupportedSchema value => new
                    GalateaRecapGridTargetAlignment.UnsupportedSchema(
                        "control",
                        value.SchemaVersion
                    ),
                RecapGridControlSnapshotResult.Disposed => new
                    GalateaRecapGridTargetAlignment.Disposed(),
                RecapGridControlSnapshotResult.Invalid value => new
                    GalateaRecapGridTargetAlignment.Invalid(
                        "control",
                        value.Code,
                        value.Detail
                    ),
                _ => new GalateaRecapGridTargetAlignment.Invalid(
                    "control",
                    "ControlSnapshotOutcomeInvalid"
                )
            };
        }
        RegisteredGridRecipe? active = current.Snapshot.ActiveRecipe;
        if (active is null) {
            return new GalateaRecapGridTargetAlignment.NoActive();
        }
        BuildTargetDigest actual = active.Recipe.Target.Digest;
        return actual == expectation.TargetDigest
            ? new GalateaRecapGridTargetAlignment.Aligned(actual)
            : new GalateaRecapGridTargetAlignment.Mismatch(
                expectation.TargetDigest,
                actual
            );
    }

    internal static void RequireCurrent(
        SessionJournalReadView selectedRef,
        GalateaRecapGridTargetExpectation expectation
    ) {
        GalateaRecapGridTargetAlignment alignment = Inspect(
            selectedRef,
            expectation
        );
        switch (alignment) {
            case GalateaRecapGridTargetAlignment.Aligned:
            case GalateaRecapGridTargetAlignment.Unprovisioned:
            case GalateaRecapGridTargetAlignment.NoActive:
                return;
            case GalateaRecapGridTargetAlignment.Mismatch:
                throw new GalateaTurnException(
                    "当前角色名与active RecapGrid recipe不一致。",
                    "character-asset-mismatch"
                );
            case GalateaRecapGridTargetAlignment.Busy value:
                throw new GalateaTurnException(
                    $"RecapGrid target检查繁忙：{value.Component}",
                    "recap-grid-busy"
                );
            case GalateaRecapGridTargetAlignment.UnsupportedSchema value:
                throw new GalateaTurnException(
                    $"RecapGrid target schema不受支持：{value.Component}:{value.SchemaVersion}",
                    "recap-grid-unsupported-schema"
                );
            case GalateaRecapGridTargetAlignment.Disposed:
                throw new GalateaTurnException(
                    "RecapGrid target authority已关闭。",
                    "recap-grid-disposed"
                );
            case GalateaRecapGridTargetAlignment.Invalid value:
                throw new GalateaTurnException(
                    $"RecapGrid target无效：{value.Component}:{value.Code}",
                    "recap-grid-invalid"
                );
            default:
                throw new InvalidDataException(
                    "Unknown Galatea RecapGrid target alignment outcome."
                );
        }
    }
}
