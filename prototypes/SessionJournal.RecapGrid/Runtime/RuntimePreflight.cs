using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Manager;

namespace Atelia.SessionJournal.RecapGrid.Runtime;

internal sealed record PreparedRecapWork(
    FrozenRecapCellWork Work,
    RecapCompletionRoute Route,
    CompletionRequest Request,
    RecapCellArtifact? SameColumnPrior,
    string StoreInstanceId,
    int StoreSchemaVersion,
    RowResultId? PreviousRowResultId
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
        if (batch.HistorySegment.Descriptor.RowId
                != batch.Spec.HistoryRowId
            || batch.HistorySegment.Descriptor.TimelineId
                != batch.Spec.TimelineId
            || batch.Recipe.Digest != batch.Spec.RecipeDigest
            || batch.HistorySegment.Descriptor.PreviousRowId != batch.Spec.PreviousHistoryRowId) {
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
        if (_inputProjector is null && visibleHistory.Any(static message =>
            message is SessionInputObservationMessage { Content.IsStructured: true })) {
            return new RuntimePreflightResult.Rejected(
                "InputProjectorUnavailable",
                "Structured recap history requires a host input projector."
            );
        }
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
                || assignment.Slot
                    != work.Slot
                || batch.Spec.DefinitionAt(work.Ordinal) != work.Definition.Digest
                || work.Slot.RecipeDigest != batch.Spec.RecipeDigest
                || work.Slot.HistoryRowId != batch.Spec.HistoryRowId
                || work.Definition.LogicalColumnId != work.LogicalColumnId
                || work.Definition.FamilyDigest != work.Family.Digest) {
                return new RuntimePreflightResult.Rejected(
                    "WorkAuthorityMismatch",
                    "Missing work differs from its exact row assignment, definition, family, or history segment."
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
                [RuntimeRenderer.RenderWorkTail(work)]
            );
            priorByColumn.TryGetValue(
                work.LogicalColumnId,
                out RecapCellArtifact? sameColumnPrior
            );
            prepared[index] = new PreparedRecapWork(
                work,
                route,
                request,
                sameColumnPrior,
                batch.StoreIdentity.InstanceId.Value,
                batch.StoreIdentity.SchemaVersion,
                batch.Spec.PreviousRowResultId
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
        if (batch.Spec.PreviousHistoryRowId is null) {
            if (batch.Spec.PreviousRowResultId is not null
                || batch.PreviousView is not null || batch.PreviousCells.Count != 0) {
                return new RuntimePreflightResult.Rejected(
                    "FirstRowPriorInvalid", "A first-row batch must not carry previous view state.");
            }
            return null;
        }
        if (batch.PreviousView is null
            || batch.PreviousCells.Count != batch.PreviousView.OrderedCells.Count) {
            return new RuntimePreflightResult.Rejected(
                "PriorViewMissing", "A successor batch requires one exact previous view and its cells.");
        }
        RecapRowView previous = batch.PreviousView;
        if (previous.Id != batch.Spec.PreviousRowResultId
            || previous.HistoryRowId != batch.Spec.PreviousHistoryRowId
            || previous.RefId != batch.Spec.RefId
            || previous.TimelineId != batch.Spec.TimelineId
            || previous.RecipeDigest != batch.Spec.RecipeDigest
            || previous.TargetDigest != batch.Spec.TargetDigest) {
            return new RuntimePreflightResult.Rejected(
                "PriorSourceMismatch", "The previous row differs from the independently frozen source.");
        }
        for (int index = 0; index < batch.PreviousCells.Count; index++) {
            RecapCellArtifact cell = batch.PreviousCells[index];
            RecapRowViewCell member = previous.OrderedCells[index];
            if (cell is null || cell.LogicalColumnId != member.LogicalColumnId
                || cell.DefinitionDigest != member.DefinitionDigest
                || cell.Id != member.CellId
                || cell.Slot.HistoryRowId != previous.HistoryRowId
                || !prior.TryAdd(cell.LogicalColumnId, cell)) {
                return new RuntimePreflightResult.Rejected(
                    "PriorViewMismatch", "Previous cells do not exactly materialize the previous view.");
            }
        }
        priorMessage = RuntimeRenderer.RenderPrior(batch.PreviousCells);
        return null;
    }

    private sealed record PreparedFamily(
        byte[] FamilyCanonical,
        CompletionPromptPrefix Prefix
    );
}
