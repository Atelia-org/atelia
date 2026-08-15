using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Manager;

namespace Atelia.SessionJournal.RecapGrid.Runtime;

internal sealed record PreparedRecapWork(
    FrozenRecapCellWork Work,
    RecapCompletionRoute Route,
    CompletionRequest Request,
    RecapCellArtifact? SameColumnPrior
);

internal abstract record RuntimePreflightResult {
    private RuntimePreflightResult() { }

    internal sealed record Ready(IReadOnlyList<PreparedRecapWork> Work)
        : RuntimePreflightResult;

    internal sealed record Rejected(string Code, string Detail)
        : RuntimePreflightResult;
}

public sealed partial class RecapCompletionRuntime {
    private RuntimePreflightResult Preflight(FrozenRowBatch batch) {
        try {
            return PreflightCore(batch);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException) {
            return new RuntimePreflightResult.Rejected(
                "RuntimePreflightInvalid",
                RuntimeDiagnostics.BoundDetail(exception.Message)
            );
        }
        catch (Exception exception) when (!IsFatal(exception)) {
            return new RuntimePreflightResult.Rejected(
                "RuntimePreflightFailure",
                RuntimeDiagnostics.BoundDetail(exception.Message)
            );
        }
    }

    private RuntimePreflightResult PreflightCore(FrozenRowBatch batch) {
        if (batch.OrderedMissingWork.Count == 0) {
            return new RuntimePreflightResult.Rejected(
                "EmptyBatch",
                "A recap completion batch must contain missing work."
            );
        }
        if (batch.HistorySegment.Descriptor.DescriptorDigest
                != batch.Spec.HistorySegmentDigest
            || batch.HistorySegment.Descriptor.RowId
                != batch.Spec.HistoryRowId
            || batch.HistorySegment.Descriptor.TimelineId
                != batch.Spec.TimelineId
            || batch.Recipe.Digest != batch.Spec.RecipeDigest) {
            return new RuntimePreflightResult.Rejected(
                "BatchAuthorityMismatch",
                "The frozen history segment, recipe, and row spec do not share one exact authority."
            );
        }

        RuntimePreflightResult.Rejected? priorFailure = ValidatePrior(
            batch,
            out IReadOnlyDictionary<LogicalColumnId, RecapCellArtifact> priorByColumn,
            out IHistoryMessage priorMessage
        );
        if (priorFailure is not null) { return priorFailure; }

        IReadOnlyList<IHistoryMessage> visibleHistory =
            RuntimeRenderer.ProjectHistory(batch.HistorySegment.Window);
        var familyCache = new Dictionary<FamilyDefinitionDigest, PreparedFamily>();
        var prepared = new PreparedRecapWork[batch.OrderedMissingWork.Count];
        int previousOrdinal = -1;
        var seenColumns = new HashSet<LogicalColumnId>();

        for (int index = 0; index < prepared.Length; index++) {
            FrozenRecapCellWork work = batch.OrderedMissingWork[index];
            if (work.Ordinal <= previousOrdinal
                || !seenColumns.Add(work.LogicalColumnId)) {
                return new RuntimePreflightResult.Rejected(
                    "WorkOrderInvalid",
                    "Missing work must have strictly increasing target ordinals and unique columns."
                );
            }
            previousOrdinal = work.Ordinal;
            if (work.Ordinal < 0
                || work.Ordinal >= batch.Spec.OrderedAssignments.Count
                || batch.Spec.OrderedAssignments[work.Ordinal]
                    is not RowBuildAssignment.Evaluate assignment
                || assignment.LogicalColumnId != work.LogicalColumnId
                || assignment.EvaluationKey.Digest
                    != work.EvaluationKey.Digest
                || !assignment.EvaluationKey.ToCanonicalBytes().SequenceEqual(
                    work.EvaluationKey.ToCanonicalBytes()
                )
                || work.EvaluationKey.DefinitionDigest
                    != work.Definition.Digest
                || work.EvaluationKey.HistorySegmentDigest
                    != batch.Spec.HistorySegmentDigest
                || work.Definition.LogicalColumnId != work.LogicalColumnId
                || work.Definition.FamilyDigest != work.Family.Digest) {
                return new RuntimePreflightResult.Rejected(
                    "WorkAuthorityMismatch",
                    "Missing work differs from its exact row assignment, definition, family, or history segment."
                );
            }
            if (!PriorReferencesMatch(batch.Spec.PriorInput, work.EvaluationKey.PriorInput)) {
                return new RuntimePreflightResult.Rejected(
                    "WorkPriorMismatch",
                    "A work evaluation key carries a different prior-input reference."
                );
            }
            if (!string.Equals(
                    work.Definition.Capability.RuntimeProtocolId,
                    RecapRewriterProtocolV3.RuntimeProtocolId,
                    StringComparison.Ordinal)) {
                return new RuntimePreflightResult.Rejected(
                    "ProtocolUnavailable",
                    "The maintainer runtime protocol is unavailable in runtime V3."
                );
            }

            if (!familyCache.TryGetValue(work.Family.Digest, out PreparedFamily? family)) {
                RuntimePreflightResult familyResult = PrepareFamily(
                    work,
                    priorMessage,
                    visibleHistory,
                    out family
                );
                if (familyResult is RuntimePreflightResult.Rejected rejected) {
                    return rejected;
                }
                familyCache.Add(work.Family.Digest, family!);
            }
            else if (!family.FamilyCanonical.SequenceEqual(
                work.Family.ToCanonicalBytes()
            )) {
                return new RuntimePreflightResult.Rejected(
                    "FamilyDigestCollision",
                    "One family digest resolved to different canonical bytes."
                );
            }

            RuntimePreflightResult routeResult = ResolveWorkRoute(
                work,
                out RecapCompletionRoute? route
            );
            if (routeResult is RuntimePreflightResult.Rejected routeFailure) {
                return routeFailure;
            }
            var request = new CompletionRequest(
                route!.ModelId,
                family!.Prefix,
                [RuntimeRenderer.RenderWorkTail(work)],
                route.MaximumOutputTokens
            );
            priorByColumn.TryGetValue(
                work.LogicalColumnId,
                out RecapCellArtifact? sameColumnPrior
            );
            prepared[index] = new PreparedRecapWork(
                work,
                route,
                request,
                sameColumnPrior
            );
        }
        return new RuntimePreflightResult.Ready(prepared);
    }

    private RuntimePreflightResult PrepareFamily(
        FrozenRecapCellWork work,
        IHistoryMessage priorMessage,
        IReadOnlyList<IHistoryMessage> visibleHistory,
        out PreparedFamily? prepared
    ) {
        prepared = null;
        FamilyDefinition family = work.Family;
        if (!string.Equals(
                work.Definition.Capability.RuntimeProtocolId,
                RecapRewriterProtocolV3.RuntimeProtocolId,
                StringComparison.Ordinal)
            || !string.Equals(
                family.OutputProtocol.ProtocolId,
                RecapRewriterProtocolV3.OutputProtocolId,
                StringComparison.Ordinal)
            || !string.Equals(
                family.InputRenderingProtocol.ProtocolId,
                RecapRewriterProtocolV3.InputProtocolId,
                StringComparison.Ordinal)
            || !string.Equals(
                family.InputRenderingProtocol.PriorProjectionSchemaId,
                RecapRewriterProtocolV3.PriorProjectionSchemaId,
                StringComparison.Ordinal)
            || !string.Equals(
                family.InputRenderingProtocol.HistorySegmentRenderingSchemaId,
                RecapRewriterProtocolV3.HistorySegmentRenderingSchemaId,
                StringComparison.Ordinal)) {
            return new RuntimePreflightResult.Rejected(
                "ProtocolUnavailable",
                "The family or maintainer protocol is unavailable in runtime V3."
            );
        }
        RuntimePreflightResult.Rejected? protocolFailure =
            RuntimeProtocolValidator.Validate(family);
        if (protocolFailure is not null) { return protocolFailure; }

        CompletionOutputContract output = RuntimeProtocolValidator
            .CreateOutputContract(family);
        var shared = new IHistoryMessage[visibleHistory.Count + 1];
        shared[0] = priorMessage;
        for (int index = 0; index < visibleHistory.Count; index++) {
            shared[index + 1] = visibleHistory[index];
        }
        prepared = new PreparedFamily(
            family.ToCanonicalBytes(),
            new CompletionPromptPrefix(
                family.SystemPrompt,
                output,
                shared
            )
        );
        return new RuntimePreflightResult.Ready(Array.Empty<PreparedRecapWork>());
    }

    private RuntimePreflightResult ResolveWorkRoute(
        FrozenRecapCellWork work,
        out RecapCompletionRoute? route
    ) {
        var key = new RecapCompletionRouteKey(
            work.Family.Digest,
            work.Definition.Capability.RuntimeProtocolId,
            work.Definition.Capability.SemanticModelId
        );
        RecapCompletionRouteResolution resolution = ResolveRoute(key);
        if (resolution is RecapCompletionRouteResolution.Bound bound) {
            if (bound.Route.Key != key) {
                route = null;
                return new RuntimePreflightResult.Rejected(
                    "RouteKeyMismatch",
                    "The route resolver attempted a fallback to a different exact route key."
                );
            }
            route = bound.Route;
            return new RuntimePreflightResult.Ready(
                Array.Empty<PreparedRecapWork>()
            );
        }
        route = null;
        return resolution switch {
            RecapCompletionRouteResolution.Unavailable value
                => BoundResolverFailure(value.Code, value.Detail),
            RecapCompletionRouteResolution.Invalid value
                => BoundResolverFailure(value.Code, value.Detail),
            _ => new RuntimePreflightResult.Rejected(
                "RouteResolutionInvalid",
                "The route resolver returned an unsupported result."
            )
        };
    }

    private static RuntimePreflightResult.Rejected BoundResolverFailure(
        string code,
        string detail
    ) => RuntimeDiagnostics.TryValidateExternalCode(
        code,
        out string validatedCode
    )
        ? new RuntimePreflightResult.Rejected(
            validatedCode,
            RuntimeDiagnostics.BoundDetail(detail)
        )
        : new RuntimePreflightResult.Rejected(
            "RouteResolutionInvalid",
            "The route resolver returned an invalid diagnostic code."
        );

    private RuntimePreflightResult.Rejected? ValidatePrior(
        FrozenRowBatch batch,
        out IReadOnlyDictionary<LogicalColumnId, RecapCellArtifact> priorByColumn,
        out IHistoryMessage priorMessage
    ) {
        var prior = new Dictionary<LogicalColumnId, RecapCellArtifact>();
        priorByColumn = prior;
        priorMessage = RuntimeRenderer.RenderPrior(Array.Empty<RecapCellArtifact>());
        switch (batch.Spec.PriorInput) {
            case PriorInputReference.FirstRow:
                if (batch.PreviousView is not null
                    || batch.PreviousCells.Count != 0
                    || batch.PriorProjection is not null) {
                    return new RuntimePreflightResult.Rejected(
                        "FirstRowPriorInvalid",
                        "A first-row batch must not carry previous view state."
                    );
                }
                return null;

            case PriorInputReference.Projection expected:
                if (batch.PreviousView is null
                    || batch.PriorProjection is null
                    || batch.PreviousCells.Count
                        != batch.PreviousView.OrderedCells.Count) {
                    return new RuntimePreflightResult.Rejected(
                        "PriorProjectionMissing",
                        "A projected batch requires one exact previous view and its cells."
                    );
                }
                var projected = new PriorProjectedContent[
                    batch.PreviousCells.Count
                ];
                for (int index = 0; index < projected.Length; index++) {
                    RecapCellArtifact cell = batch.PreviousCells[index];
                    RecapRowViewCell member =
                        batch.PreviousView.OrderedCells[index];
                    if (cell.LogicalColumnId != member.LogicalColumnId
                        || cell.DefinitionDigest != member.DefinitionDigest
                        || cell.CellDigest != member.CellDigest
                        || !prior.TryAdd(cell.LogicalColumnId, cell)) {
                        return new RuntimePreflightResult.Rejected(
                            "PriorViewMismatch",
                            "Previous cells do not exactly materialize the previous view."
                        );
                    }
                    projected[index] = new PriorProjectedContent(
                        cell.LogicalColumnId,
                        cell.ContentDigest
                    );
                }
                PriorInputProjection rebuilt =
                    PriorInputProjection.Create(projected);
                if (expected.Digest != rebuilt.Digest
                    || batch.PriorProjection.Digest != rebuilt.Digest
                    || !batch.PriorProjection.ToCanonicalBytes()
                        .SequenceEqual(rebuilt.ToCanonicalBytes())) {
                    return new RuntimePreflightResult.Rejected(
                        "PriorProjectionMismatch",
                        "The prior projection differs from the ordered previous-cell loop."
                    );
                }
                priorMessage = RuntimeRenderer.RenderPrior(
                    batch.PreviousCells
                );
                return null;

            default:
                return new RuntimePreflightResult.Rejected(
                    "PriorInputUnsupported",
                    "The prior-input reference subtype is unsupported."
                );
        }
    }

    private static bool PriorReferencesMatch(
        PriorInputReference left,
        PriorInputReference right
    ) => (left, right) switch {
        (PriorInputReference.FirstRow, PriorInputReference.FirstRow) => true,
        (PriorInputReference.Projection first,
            PriorInputReference.Projection second)
            => first.Digest == second.Digest,
        _ => false
    };

    private sealed record PreparedFamily(
        byte[] FamilyCanonical,
        CompletionPromptPrefix Prefix
    );
}
