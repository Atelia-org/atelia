using Atelia.EventJournal;

namespace Atelia.SessionJournal.RecapGrid.Getter;

public sealed partial class RecapGridContextHandle {
    public ValueTask<SessionContextCandidateSelection> SelectAsync(
        SessionContextSelectionRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateShape();
        RecapGridContextResolveResult resolved = Resolve(
            request.CompletionBoundary,
            request.NthPrevious,
            cancellationToken
        );
        return ValueTask.FromResult(resolved switch {
            RecapGridContextResolveResult.RawHistoryAuthorized
                => new SessionContextCandidateSelection(
                    SessionContextCandidateSelectionStatus.EmptyLineage,
                    null
                ),
            RecapGridContextResolveResult.Selected selected
                => new SessionContextCandidateSelection(
                    SessionContextCandidateSelectionStatus.Selected,
                    new SessionContextCandidateDescriptor(
                        selected.Selection.HandleToken,
                        selected.Selection.SnapshotToken,
                        selected.Selection.SelectedRow.Descriptor.EndInclusive,
                        selected.Selection.SelectedRow.Descriptor.EndSetups
                    )
                ),
            RecapGridContextResolveResult.OrdinalUnavailable
                => new SessionContextCandidateSelection(
                    SessionContextCandidateSelectionStatus.OrdinalUnavailable,
                    null
                ),
            RecapGridContextResolveResult.LimitExceeded limit
                => new SessionContextCandidateSelection(
                    SessionContextCandidateSelectionStatus.StoreUnavailable,
                    null,
                    $"Getter limit exceeded: {limit.Limit}."
                ),
            RecapGridContextResolveResult.Invalid invalid
                => new SessionContextCandidateSelection(
                    SessionContextCandidateSelectionStatus.ExactPublishedSetInvalid,
                    null,
                    $"{invalid.Code}: {invalid.Detail}"
                ),
            RecapGridContextResolveResult.NotOnSelectedPath missing
                => new SessionContextCandidateSelection(
                    SessionContextCandidateSelectionStatus.ExactPublishedSetInvalid,
                    null,
                    $"Row '{missing.RowId}' is not on the selected path."
                ),
            _ => new SessionContextCandidateSelection(
                SessionContextCandidateSelectionStatus.StoreUnavailable,
                null,
                DescribeUnavailable(resolved)
            )
        });
    }

    public ValueTask<SessionContextCandidateMaterializationResult>
        MaterializeAsync(
        SessionContextCandidateDescriptor descriptor,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(descriptor);
        HandleParseResult parsed = TryParseHandle(
            descriptor.Handle,
            out EventAddress completionBoundary,
            out int nthPrevious
        );
        if (parsed == HandleParseResult.ForeignOwner) {
            return ValueTask.FromResult<
                SessionContextCandidateMaterializationResult>(
                new SessionContextCandidateMaterializationResult.Stale(
                    "The RecapGrid candidate belongs to another Getter handle."
                )
            );
        }
        if (parsed != HandleParseResult.Parsed) {
            return ValueTask.FromResult<
                SessionContextCandidateMaterializationResult>(
                new SessionContextCandidateMaterializationResult.Invalid(
                    "The RecapGrid candidate handle is invalid."
                )
            );
        }
        RecapGridContextResolveResult resolved = Resolve(
            completionBoundary,
            nthPrevious,
            cancellationToken
        );
        if (resolved is not RecapGridContextResolveResult.Selected selected) {
            return ValueTask.FromResult(MapNeutralMaterialization(resolved));
        }
        if (!string.Equals(
                selected.Selection.SnapshotToken,
                descriptor.SnapshotToken,
                StringComparison.Ordinal)
            || selected.Selection.SelectedRow.Descriptor.EndInclusive
                != descriptor.SetAdmissionAnchor
            || selected.Selection.SelectedRow.Descriptor.EndSetups
                != descriptor.AnchorSetups) {
            return ValueTask.FromResult<
                SessionContextCandidateMaterializationResult>(
                new SessionContextCandidateMaterializationResult.Stale(
                    "The RecapGrid selection token or raw anchor changed."
                )
            );
        }
        RecapGridContextMaterializeResult materialized = Materialize(
            selected.Selection,
            cancellationToken
        );
        return ValueTask.FromResult<
            SessionContextCandidateMaterializationResult>(materialized switch {
            RecapGridContextMaterializeResult.Available available
                => new SessionContextCandidateMaterializationResult
                    .Materialized(available.Candidate),
            RecapGridContextMaterializeResult.Stale stale
                => new SessionContextCandidateMaterializationResult.Stale(
                    stale.Detail
                ),
            RecapGridContextMaterializeResult.Busy busy
                => new SessionContextCandidateMaterializationResult.Busy(
                    $"{busy.Component} is busy."
                ),
            RecapGridContextMaterializeResult.Disposed disposed
                => new SessionContextCandidateMaterializationResult.Disposed(
                    $"{disposed.Component} has been disposed."
                ),
            RecapGridContextMaterializeResult.Invalid invalid
                => new SessionContextCandidateMaterializationResult.Invalid(
                    $"{invalid.Code}: {invalid.Detail}"
                ),
            _ => new SessionContextCandidateMaterializationResult.Invalid(
                "The Getter returned an unknown materialization outcome."
            )
        });
    }

    public ValueTask<SessionContextLifecycleResult> PrepareAsync(
        SessionJournalReadView readView,
        SessionContextLifecycleRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(readView);
        ArgumentNullException.ThrowIfNull(request);
        if (!ReferenceEquals(readView, _selectedRef)) {
            return ValueTask.FromResult(new SessionContextLifecycleResult(
                SessionContextLifecycleStatus.Unavailable,
                "The RecapGrid lifecycle received another raw authority."
            ));
        }
        RecapGridContextResolveResult resolved = Resolve(
            request.Selection.CompletionBoundary,
            request.Selection.NthPrevious,
            cancellationToken
        );
        return ValueTask.FromResult(resolved switch {
            RecapGridContextResolveResult.RawHistoryAuthorized
                => SessionContextLifecycleResult.RawHistoryAuthorized,
            RecapGridContextResolveResult.Selected
                or RecapGridContextResolveResult.OrdinalUnavailable
                or RecapGridContextResolveResult.Unfulfilled
                or RecapGridContextResolveResult.LimitExceeded
                => SessionContextLifecycleResult.Ready,
            _ => new SessionContextLifecycleResult(
                SessionContextLifecycleStatus.Unavailable,
                DescribeUnavailable(resolved)
            )
        });
    }

    private static SessionContextCandidateMaterializationResult
        MapNeutralMaterialization(RecapGridContextResolveResult result)
        => result switch {
            RecapGridContextResolveResult.Stale stale
                => new SessionContextCandidateMaterializationResult.Stale(
                    stale.Detail
                ),
            RecapGridContextResolveResult.Busy busy
                => new SessionContextCandidateMaterializationResult.Busy(
                    $"{busy.Component} is busy."
                ),
            RecapGridContextResolveResult.Disposed disposed
                => new SessionContextCandidateMaterializationResult.Disposed(
                    $"{disposed.Component} has been disposed."
                ),
            RecapGridContextResolveResult.UnsupportedSchema schema
                => new SessionContextCandidateMaterializationResult.Invalid(
                    $"{schema.Component} schema {schema.SchemaVersion} is unsupported."
                ),
            RecapGridContextResolveResult.Invalid invalid
                => new SessionContextCandidateMaterializationResult.Invalid(
                    $"{invalid.Code}: {invalid.Detail}"
                ),
            _ => new SessionContextCandidateMaterializationResult.Stale(
                DescribeUnavailable(result)
            )
        };

    private static string DescribeUnavailable(
        RecapGridContextResolveResult result
    ) => result switch {
        RecapGridContextResolveResult.Unfulfilled
            => "The active recipe is not fulfilled at the current Timeline head.",
        RecapGridContextResolveResult.Stale stale => stale.Detail,
        RecapGridContextResolveResult.Busy busy
            => $"{busy.Component} is busy.",
        RecapGridContextResolveResult.Disposed disposed
            => $"{disposed.Component} has been disposed.",
        RecapGridContextResolveResult.UnsupportedSchema schema
            => $"{schema.Component} schema {schema.SchemaVersion} is unsupported.",
        RecapGridContextResolveResult.LimitExceeded limit
            => $"Getter limit exceeded: {limit.Limit}.",
        _ => "The exact RecapGrid context is unavailable."
    };
}
