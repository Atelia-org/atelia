using Atelia.Completion;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Manager;

namespace Atelia.SessionJournal.Cli;

internal static partial class RecapGridCommands {
    private static int ControlPromote(CliOptions options) {
        options.EnsureOnly(
            "input", "branch", "confirm-ref", "admission", "recipe",
            "through-row", "max-recipe-row-steps",
            "max-new-calls", "max-elapsed-ms"
        );
        if (!string.Equals(
                options.RequireSingle("max-new-calls"),
                "0",
                StringComparison.Ordinal)) {
            throw new ArgumentException(
                "Promotion revalidation requires --max-new-calls 0."
            );
        }
        using SessionJournalEngine engine = OpenBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        RecapGridControlAdmission admission = ReadAdmission(options);
        if ((admission.Permissions & RecapGridControlPermission.Promote)
            != RecapGridControlPermission.Promote) {
            throw new ArgumentException(
                "The admission does not authorize promotion."
            );
        }
        RecapGridBuildRequest request = ReadBuildRequest(options);
        if (request.Selection is not RecapGridBuildSelection
                .ExplicitCandidate) {
            throw new ArgumentException(
                "Promotion requires an explicit --recipe."
            );
        }
        RecapGridManagerOpenResult opened = RecapGridManagerFactory.Open(
            engine.ReadView,
            RecapGridHistoryLoadEstimator
        );
        if (opened is not RecapGridManagerOpenResult.Opened manager) {
            return Print(
                "control.promote", "manager-open-failed",
                DescribeManagerOpen(opened), 2
            );
        }
        using (manager.Handle) {
            RecapGridBuildProgressResult progress = manager.Handle.Manager
                .InspectBuildProgress(request);
            if (progress is not RecapGridBuildProgressResult.Complete {
                    FulfillmentPresent: true,
                    Proof: { } proof
                }) {
                return Print(
                    "control.promote",
                    "revalidation-not-promotable",
                    progress,
                    2
                );
            }
            RecapGridControlOpenResult controlOpened =
                RecapGridControlFactory.Open(
                    engine.ReadView.Path,
                    engine.BranchRefId,
                    admission
                );
            if (controlOpened is not RecapGridControlOpenResult.Opened control) {
                return PrintControlOpenFailure(
                    "control.promote",
                    controlOpened
                );
            }
            using (control.Handle) {
                RecapGridControlActivateResult activated = control.Handle
                    .Coordinator.CompareExchangeActiveRecipe(
                        proof.ControlHead,
                        proof.TimelineHead,
                        proof.RecipeDigest,
                        RecapGridControlActivationPurpose.Promotion
                    );
                return PrintControlActivate(
                    "control.promote",
                    activated
                );
            }
        }
    }

    private static async ValueTask<int> BuildAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) {
        options.EnsureOnly(
            "input", "branch", "confirm-ref", "live", "recipe", "through-row",
            "max-recipe-row-steps", "max-new-calls",
            "max-elapsed-ms", "routes", "connections", "call-log-dir"
        );
        using SessionJournalEngine engine = OpenBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        RecapGridBuildRequest request = ReadBuildRequest(options);
        RecapGridRouteManifest manifest = RecapGridRouteManifest
            .DecodeCanonical(ReadBoundedFile(
                options.RequireSingle("routes"),
                RecapGridRouteManifestLimits.MaximumCanonicalUtf8Bytes
            ));
        CompletionConnectionsFileConfig connections =
            RecapGridCompletionConnectionsManifest.Decode(ReadBoundedFile(
                options.RequireSingle("connections"),
                RecapGridCompletionConnectionsLimits.MaximumInputUtf8Bytes
            ));
        string? callLogDirectory = options.GetOptionalSingle("call-log-dir");
        ICompletionClientFactory buildClientFactory = callLogDirectory is null
            ? completionClientFactory
            : new RecapGridLoggingCompletionClientFactory(
                completionClientFactory,
                callLogDirectory
            );
        RecapGridManagerOpenResult opened = RecapGridManagerFactory.Open(
            engine.ReadView,
            RecapGridHistoryLoadEstimator
        );
        if (opened is not RecapGridManagerOpenResult.Opened manager) {
            return Print(
                "build", "open-failed", DescribeManagerOpen(opened), 2
            );
        }
        using (manager.Handle) {
            await using RecapGridRuntimeHost host = RecapGridRuntimeHost.Create(
                manifest,
                connections,
                buildClientFactory
            );
            RecapGridBuildResult result = await manager.Handle.Manager
                .BuildAsync(
                    request,
                    host.Executor
                ).ConfigureAwait(false);
            return PrintBuildResult(
                "build",
                result,
                host.Telemetry.ReadSnapshot()
            );
        }
    }

    private static int Progress(CliOptions options) {
        options.EnsureOnly(
            "input", "branch", "live", "recipe", "through-row",
            "max-recipe-row-steps", "max-new-calls",
            "max-elapsed-ms"
        );
        using SessionJournalEngine engine = OpenBranch(options);
        RecapGridManagerOpenResult opened = RecapGridManagerFactory.Open(
            engine.ReadView,
            RecapGridHistoryLoadEstimator
        );
        if (opened is not RecapGridManagerOpenResult.Opened manager) {
            return Print(
                "progress", "open-failed", DescribeManagerOpen(opened), 2
            );
        }
        using (manager.Handle) {
            RecapGridBuildProgressResult result = manager.Handle.Manager
                .InspectBuildProgress(ReadBuildRequest(options));
            return Print(
                "progress",
                result is RecapGridBuildProgressResult.Complete
                    ? "complete"
                    : result is RecapGridBuildProgressResult.Frontier
                        ? "frontier"
                        : result is RecapGridBuildProgressResult.NoRows
                            ? "no-rows"
                            : ProgressStatus(result),
                result,
                result is RecapGridBuildProgressResult.Complete
                    or RecapGridBuildProgressResult.Frontier
                    or RecapGridBuildProgressResult.NoRows
                    ? 0 : 2
            );
        }
    }

    private static int Materialize(CliOptions options) {
        options.EnsureOnly(
            "input", "branch", "boundary", "nth-previous", "include-content"
        );
        bool includeContent = options.HasSingleFlag("include-content");
        using SessionJournalEngine engine = OpenBranch(options);
        RecapGridContextOpenResult opened = RecapGridContextFactory.Open(
            engine.ReadView,
            new O200kBaseHistoryUnitLoadEstimator()
        );
        if (opened is not RecapGridContextOpenResult.Opened context) {
            return Print("materialize", "open-failed", opened, 2);
        }
        using (context.Handle) {
            EventAddress boundary = EventAddressTextCodec.Parse(
                options.RequireSingle("boundary")
            );
            int nth = options.GetOptionalSingle("nth-previous") is { } raw
                ? ParseBoundedInt(
                    raw,
                    0,
                    RecapGridGetterLimits.MaximumNthPrevious,
                    "--nth-previous"
                )
                : 0;
            RecapGridContextResolveResult resolved = context.Handle.Resolve(
                boundary,
                nth
            );
            if (resolved is RecapGridContextResolveResult.RawHistoryAuthorized) {
                return Print("materialize", "raw-history-authorized");
            }
            if (resolved is RecapGridContextResolveResult.ReserveBootstrapRawOnly
                    bootstrap) {
                return Print(
                    "materialize",
                    "reserve-bootstrap-raw-only",
                    bootstrap.Evidence
                );
            }
            if (resolved is not RecapGridContextResolveResult.Selected selected) {
                return Print("materialize", ResolveStatus(resolved), resolved, 2);
            }
            RecapGridContextMaterializeResult result = context.Handle.Materialize(
                selected.Selection
            );
            if (result is not RecapGridContextMaterializeResult.Available
                    available) {
                return Print(
                    "materialize",
                    MaterializeStatus(result),
                    result,
                    2
                );
            }
            return Print(
                "materialize",
                "available",
                new {
                    selection = new {
                        selected.Selection.TimelineHead,
                        selected.Selection.ControlHead,
                        selected.Selection.StoreIdentity,
                        recipe = selected.Selection.Recipe.Digest.Value,
                        rowId = selected.Selection.SelectedRowId.Value,
                        descriptorDigest = selected.Selection
                            .SelectedDescriptorDigest.Value,
                        viewDigest = selected.Selection.SelectedViewDigest.Value
                    },
                    candidate = new {
                        available.Candidate.SetAdmissionAnchor,
                        available.Candidate.AnchorSetups,
                        contributions = available.Candidate.Contributions.Select(
                            contribution => new {
                                contribution.Target,
                                exactText = includeContent
                                    ? contribution.ExactText
                                    : null,
                                utf8Bytes = System.Text.Encoding.UTF8.GetByteCount(
                                    contribution.ExactText
                                ),
                                contribution.ContentCodecId,
                                contribution.ContentSha256,
                                contribution.AbsorbedThrough
                            }
                        )
                    },
                    available.Provenance
                }
            );
        }
    }

    private static RecapGridBuildRequest ReadBuildRequest(CliOptions options) {
        bool live = options.HasSingleFlag("live");
        string? recipe = options.GetOptionalSingle("recipe");
        if (live == (recipe is not null)) {
            throw new ArgumentException(
                "Specify exactly one of --live or --recipe."
            );
        }
        RecapGridBuildSelection selection = live
            ? new RecapGridBuildSelection.LiveActive()
            : new RecapGridBuildSelection.ExplicitCandidate(
                new GridBuildRecipeDigest(recipe!)
            );
        HistoryRowId? through = options.GetOptionalSingle("through-row")
            is { } row
            ? new HistoryRowId(row)
            : null;
        return new RecapGridBuildRequest(
            selection,
            through,
            new RecapGridBuildBudget(
                ReadBoundedOption(options, "max-recipe-row-steps", 1_000_000),
                ReadBoundedOption(options, "max-new-calls", 1_000_000),
                TimeSpan.FromMilliseconds(ReadBoundedOption(
                    options,
                    "max-elapsed-ms",
                    checked((int)TimeSpan.FromDays(1).TotalMilliseconds)
                ))
            )
        );
    }

    private static int ReadBoundedOption(
        CliOptions options,
        string key,
        int maximum
    ) => ParseBoundedInt(
        options.RequireSingle(key),
        0,
        maximum,
        $"--{key}"
    );

    private static object DescribeManagerOpen(RecapGridManagerOpenResult result)
        => result;

    private static int PrintBuildResult(
        string command,
        RecapGridBuildResult result,
        RecapCompletionTelemetrySnapshot? evidence = null
    ) => Print(
        command,
        result switch {
            RecapGridBuildResult.Fulfilled => "fulfilled",
            RecapGridBuildResult.FulfilledThrough => "fulfilled-through",
            RecapGridBuildResult.NoRows => "no-rows",
            RecapGridBuildResult.NoActiveRecipe => "no-active-recipe",
            RecapGridBuildResult.RecipeAbsent => "recipe-absent",
            RecapGridBuildResult.ThroughRowNotSelected
                => "through-row-not-selected",
            RecapGridBuildResult.BudgetExceeded => "budget-exceeded",
            RecapGridBuildResult.Cancelled => "cancelled",
            RecapGridBuildResult.Incomplete => "incomplete",
            RecapGridBuildResult.ExecutorRejected => "executor-rejected",
            RecapGridBuildResult.ExecutorFailed => "executor-failed",
            RecapGridBuildResult.Unavailable => "unavailable",
            RecapGridBuildResult.StaleTimelineHead => "stale-timeline-head",
            RecapGridBuildResult.StaleControlAuthority
                => "stale-control-authority",
            RecapGridBuildResult.SettlementRequired => "settlement-required",
            RecapGridBuildResult.Disposed => "disposed",
            RecapGridBuildResult.Invalid => "invalid",
            _ => "invalid-outcome"
        },
        evidence is null ? result : new { result, evidence },
        result is RecapGridBuildResult.Fulfilled
            or RecapGridBuildResult.FulfilledThrough
            or RecapGridBuildResult.NoRows
            ? 0 : 2
    );

    private static string ProgressStatus(RecapGridBuildProgressResult result)
        => result switch {
            RecapGridBuildProgressResult.Blocked => "blocked",
            RecapGridBuildProgressResult.NoActiveRecipe => "no-active-recipe",
            RecapGridBuildProgressResult.RecipeAbsent => "recipe-absent",
            RecapGridBuildProgressResult.ThroughRowNotSelected
                => "through-row-not-selected",
            RecapGridBuildProgressResult.BudgetExceeded => "budget-exceeded",
            RecapGridBuildProgressResult.Cancelled => "cancelled",
            RecapGridBuildProgressResult.Unavailable => "unavailable",
            RecapGridBuildProgressResult.StaleTimelineHead
                => "stale-timeline-head",
            RecapGridBuildProgressResult.StaleControlAuthority
                => "stale-control-authority",
            RecapGridBuildProgressResult.Disposed => "disposed",
            RecapGridBuildProgressResult.Invalid => "invalid",
            _ => "invalid-outcome"
        };

    private static string ResolveStatus(RecapGridContextResolveResult result)
        => result switch {
            RecapGridContextResolveResult.OrdinalUnavailable
                => "ordinal-unavailable",
            RecapGridContextResolveResult.LimitExceeded => "limit-exceeded",
            RecapGridContextResolveResult.Unfulfilled => "unfulfilled",
            RecapGridContextResolveResult.ReserveBootstrapRawOnly
                => "reserve-bootstrap-raw-only",
            RecapGridContextResolveResult.Stale => "stale",
            RecapGridContextResolveResult.NotOnSelectedPath
                => "not-on-selected-path",
            RecapGridContextResolveResult.Busy => "busy",
            RecapGridContextResolveResult.Disposed => "disposed",
            RecapGridContextResolveResult.UnsupportedSchema
                => "unsupported-schema",
            RecapGridContextResolveResult.Invalid => "invalid",
            _ => "invalid-outcome"
        };

    private static string MaterializeStatus(
        RecapGridContextMaterializeResult result
    ) => result switch {
        RecapGridContextMaterializeResult.Stale => "stale",
        RecapGridContextMaterializeResult.Busy => "busy",
        RecapGridContextMaterializeResult.Disposed => "disposed",
        RecapGridContextMaterializeResult.Invalid => "invalid",
        _ => "invalid-outcome"
    };

}
