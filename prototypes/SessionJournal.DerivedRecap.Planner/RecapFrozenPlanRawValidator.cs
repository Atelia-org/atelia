using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

internal sealed record RecapFrozenPlanRawDefect(string Detail);

/// <summary>
/// Validates only the raw-lineage semantics frozen into a recap plan.
/// Active catalog policy and Published/Building ordering belong to the
/// phase-specific caller.
/// </summary>
internal static class RecapFrozenPlanRawValidator {
    public static IReadOnlyList<RecapFrozenPlanRawDefect> ValidateBlock(
        DerivedRecapSetManifest manifest,
        IReadOnlyDictionary<
            RecapBlockId,
            DerivedRecapFrozenInput
        > frozenInputs,
        SessionCurrentLineageSnapshot lineage,
        RecapBlockPlan plan
    ) {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(frozenInputs);
        ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(plan);

        var defects = new List<RecapFrozenPlanRawDefect>();
        Dictionary<EventAddress, int> lineageIndex =
            lineage.HeadToRoot
                .Select((node, index) => (node.Address, index))
                .ToDictionary(
                    static pair => pair.Address,
                    static pair => pair.index
                );
        if (!lineageIndex.TryGetValue(
                manifest.SetAdmissionAnchor,
                out int admissionIndex
            )) {
            Add(
                defects,
                "SetAdmissionAnchor is outside current raw lineage."
            );
            return defects;
        }

        switch (plan) {
            case InheritRecapBlockPlan inherit:
                if (!TryValidateFrozenSource(
                        frozenInputs,
                        plan,
                        inherit.SourceSetAnchor,
                        lineageIndex,
                        admissionIndex,
                        defects,
                        out DerivedRecapFrozenInput? inheritedInput,
                        out _
                    )) {
                    return defects;
                }
                if (string.IsNullOrEmpty(inheritedInput.Content)) {
                    Add(
                        defects,
                        $"Inherit block '{plan.RecapBlockId}' source "
                        + "content is empty."
                    );
                    return defects;
                }
                try {
                    if (new UTF8Encoding(false, true).GetByteCount(
                            inheritedInput.Content
                        ) > plan.MaxContentUtf8Bytes) {
                        Add(
                            defects,
                            $"Inherit block '{plan.RecapBlockId}' "
                            + "source content exceeds its frozen limit."
                        );
                    }
                }
                catch (EncoderFallbackException) {
                    Add(
                        defects,
                        $"Inherit block '{plan.RecapBlockId}' source "
                        + "content is not valid UTF-8."
                    );
                }
                return defects;

            case MaintainRecapBlockPlan maintain:
                int startIndex;
                switch (maintain.Source) {
                    case EmptyRecapMaintainSource empty:
                        if (!lineageIndex.TryGetValue(
                                empty.ReplayStartExclusive,
                                out startIndex
                            )
                            || startIndex <= admissionIndex) {
                            Add(
                                defects,
                                $"Maintain block '{plan.RecapBlockId}' "
                                + "empty replay start is not a strict "
                                + "admission ancestor."
                            );
                            return defects;
                        }
                        break;
                    case ExistingRecapMaintainSource existing:
                        if (!TryValidateFrozenSource(
                                frozenInputs,
                                plan,
                                existing.SourceSetAnchor,
                                lineageIndex,
                                admissionIndex,
                                defects,
                                out _,
                                out startIndex
                            )) {
                            return defects;
                        }
                        break;
                    default:
                        Add(
                            defects,
                            $"Maintain block '{plan.RecapBlockId}' "
                            + "has an unsupported source."
                        );
                        return defects;
                }

                if (maintain.PriorContext
                        is InlineRecapPriorContext inline
                    && (!lineageIndex.TryGetValue(
                            inline.AdmissionAnchor,
                            out int priorIndex
                        )
                        || priorIndex < startIndex)) {
                    Add(
                        defects,
                        $"Maintain block '{plan.RecapBlockId}' inline "
                        + "prior context is not an ancestor of its "
                        + "replay start."
                    );
                }

                int previousIndex = startIndex;
                foreach (EventAddress endpoint
                         in maintain.CatchUpThrough) {
                    if (!lineageIndex.TryGetValue(
                            endpoint,
                            out int endpointIndex
                        )
                        || endpointIndex >= previousIndex) {
                        Add(
                            defects,
                            $"Maintain block '{plan.RecapBlockId}' route "
                            + "is not strictly increasing from its exact "
                            + "source cursor."
                        );
                        return defects;
                    }
                    previousIndex = endpointIndex;
                }
                if (maintain.CatchUpThrough.Count == 0
                    || maintain.CatchUpThrough[^1]
                        != manifest.SetAdmissionAnchor) {
                    Add(
                        defects,
                        $"Maintain block '{plan.RecapBlockId}' route "
                        + "does not end at SetAdmissionAnchor."
                    );
                }
                return defects;

            default:
                Add(
                    defects,
                    $"Block '{plan.RecapBlockId}' has an unsupported "
                    + "plan mode."
                );
                return defects;
        }
    }

    private static bool TryValidateFrozenSource(
        IReadOnlyDictionary<
            RecapBlockId,
            DerivedRecapFrozenInput
        > frozenInputs,
        RecapBlockPlan plan,
        EventAddress sourceSetAnchor,
        IReadOnlyDictionary<EventAddress, int> lineage,
        int admissionIndex,
        List<RecapFrozenPlanRawDefect> defects,
        out DerivedRecapFrozenInput input,
        out int cursorIndex
    ) {
        input = null!;
        cursorIndex = -1;
        if (!lineage.TryGetValue(
                sourceSetAnchor,
                out int sourceIndex
            )
            || sourceIndex <= admissionIndex) {
            Add(
                defects,
                $"Block '{plan.RecapBlockId}' source set is not a "
                + "strict admission ancestor."
            );
            return false;
        }
        if (!frozenInputs.TryGetValue(
                plan.RecapBlockId,
                out DerivedRecapFrozenInput? foundInput
            )
            || foundInput.Target != plan.Target
            || !lineage.TryGetValue(
                foundInput.AbsorbedThrough,
                out cursorIndex
            )
            || cursorIndex < sourceIndex) {
            Add(
                defects,
                $"Block '{plan.RecapBlockId}' frozen cursor is not "
                + "at or before its source set container."
            );
            return false;
        }
        input = foundInput;
        return true;
    }

    private static void Add(
        List<RecapFrozenPlanRawDefect> defects,
        string detail
    ) => defects.Add(new RecapFrozenPlanRawDefect(detail));
}
