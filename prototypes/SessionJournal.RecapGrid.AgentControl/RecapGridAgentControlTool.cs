using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.Completion.Tools.Declaration;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Manager;

namespace Atelia.SessionJournal.RecapGrid.AgentControl;

internal sealed class RecapGridAgentControlTool {
    internal const string ToolName = "recap_grid.control";
    internal const string ToolDescription =
        "Inspect or mutate the admitted RecapGrid Control state. No authority tokens are accepted from the model.";
    private const int MaximumArgumentsUtf8Bytes = 2 * 1024 * 1024;
    private const int MaximumCanonicalBase64Characters = 1024 * 1024;
    private readonly SessionJournalReadView _selectedRef;
    private readonly RecapGridControlAdmission _admission;
    private readonly IHistoryUnitLoadEstimator[] _estimators;
    private readonly AgentControlDependencySource _dependencies;
    private readonly RecapGridAgentControlLifetime _lifetime;
    private readonly AgentControlDependencyTestHooks? _testHooks;

    internal RecapGridAgentControlTool(
        SessionJournalReadView selectedRef,
        RecapGridControlAdmission admission,
        IHistoryUnitLoadEstimator[] estimators,
        AgentControlDependencySource dependencies,
        RecapGridAgentControlLifetime lifetime,
        AgentControlDependencyTestHooks? testHooks
    ) {
        _selectedRef = selectedRef;
        _admission = admission;
        _estimators = estimators;
        _dependencies = dependencies;
        _lifetime = lifetime;
        _testHooks = testHooks;
    }

    internal static ToolDefinition CanonicalDefinition { get; } =
        ReflectedToolDefinitionBuilder.BuildDefinition<
            AgentControlMethodInput>(ToolName, ToolDescription);

    [Tool(ToolName, ToolDescription)]
    internal async ValueTask<ToolExecuteResult> ExecuteToolAsync(
        AgentControlMethodInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        using RecapGridAgentControlLifetime.Operation? operation =
            _lifetime.TryEnter();
        if (operation is null) {
            return Failed("disposed", "Agent Control is disposed.");
        }
        if (context.OperationId is null) {
            return Failed(
                "operation-id-required",
                "Durable Agent Control requires a SessionJournal operation id."
            );
        }
        AgentControlArguments arguments;
        try {
            arguments = ParseExact(context.RawToolCall.RawArgumentsJson);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or JsonException
            or FormatException) {
            return Failed(
                "arguments-invalid",
                RecapGridAgentControlFactory.Bound(exception.Message)
            );
        }
        if (cancellationToken.IsCancellationRequested) {
            throw new ToolExecutionCancelledBeforeMutationException(
                cancellationToken
            );
        }

        AgentControlDependencyResult dependency = _dependencies.Get();
        ThrowIfCancelledBeforeMutation(cancellationToken);
        if (dependency is AgentControlDependencyResult.Failure unavailable) {
            return Failed(unavailable.Code, unavailable.Detail);
        }
        var opened = (AgentControlDependencyResult.Opened)dependency;
        AuthorityCapture? capture = CaptureAuthority(opened);
        ThrowIfCancelledBeforeMutation(cancellationToken);
        if (capture?.Failure is { } failure) {
            return failure;
        }
        AuthorityCapture authority = capture!;
        if (arguments.Action == "inspect") {
            return Success(
                "available",
                authority.ControlHead,
                authority.TimelineHead,
                resultIdentity: null,
                headAdvancedSinceApply: false,
                instanceReplaced: false
            );
        }

        try {
            SessionToolRuntimeIdentity runtimeIdentity =
                RecapGridAgentControlFactoryIdentity.Require(
                    context.Session);
            RecapGridControlOperation durableOperation =
                RecapGridControlOperation.Create(
                context.OperationId,
                context.ExecutionSequence,
                RecapGridAgentControlFactory.RuntimeIdentityDigest(
                    runtimeIdentity
                )
            );
            return arguments.Action switch {
                "register-family" => ApplyRegistration(
                    opened,
                    authority,
                    durableOperation,
                    FamilyBundle(arguments.CanonicalValueBase64!),
                    cancellationToken
                ),
                "register-definition" => ApplyRegistration(
                    opened,
                    authority,
                    durableOperation,
                    DefinitionBundle(arguments.CanonicalValueBase64!),
                    cancellationToken
                ),
                "register-recipe" => ApplyRegistration(
                    opened,
                    authority,
                    durableOperation,
                    RecipeBundle(
                        opened,
                        authority,
                        arguments.CanonicalValueBase64!
                    ),
                    cancellationToken
                ),
                "provision-built-in" => ApplyBuiltIn(
                    opened,
                    authority,
                    durableOperation,
                    arguments.BuiltInAssetId!,
                    cancellationToken
                ),
                "promote" => await PromoteAsync(
                    opened,
                    authority,
                    durableOperation,
                    new GridBuildRecipeDigest(arguments.RecipeDigest!),
                    cancellationToken
                ).ConfigureAwait(false),
                _ => Failed(
                    "action-unsupported",
                    "Unsupported Agent Control action."
                )
            };
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or FormatException) {
            return Failed(
                "command-invalid",
                RecapGridAgentControlFactory.Bound(exception.Message)
            );
        }
    }

    private ToolExecuteResult ApplyBuiltIn(
        AgentControlDependencyResult.Opened dependencies,
        AuthorityCapture authority,
        RecapGridControlOperation operation,
        string assetId,
        CancellationToken cancellationToken
    ) {
        if (!RecapGridAgentControlBuiltIns.TryCreateRegistrationBundle(
                assetId,
                out RecapGridControlRegistrationBundle? bundle)
            || bundle is null) {
            return Failed(
                "built-in-asset-absent",
                "The code-owned built-in asset id is unknown."
            );
        }
        return ApplyRegistration(
            dependencies,
            authority,
            operation,
            bundle,
            cancellationToken
        );
    }

    private ToolExecuteResult ApplyRegistration(
        AgentControlDependencyResult.Opened dependencies,
        AuthorityCapture authority,
        RecapGridControlOperation operation,
        RecapGridControlRegistrationBundle bundle,
        CancellationToken cancellationToken
    ) {
        ThrowIfCancelledBeforeMutation(cancellationToken);
        RecapGridControlOperationResult result = _testHooks?
                .ControlOperationResultOverride?.Invoke()
            ?? dependencies.Control.Coordinator.ApplyRegistrationBundle(
                authority.ControlHead,
                authority.TimelineHead,
                operation,
                bundle
            );
        return MapOperation(
            result,
            authority.TimelineHead
        );
    }

    private async ValueTask<ToolExecuteResult> PromoteAsync(
        AgentControlDependencyResult.Opened dependencies,
        AuthorityCapture authority,
        RecapGridControlOperation operation,
        GridBuildRecipeDigest recipeDigest,
        CancellationToken cancellationToken
    ) {
        RecapGridManagerOpenResult opened = RecapGridManagerFactory.Open(
            _selectedRef,
            _estimators
        );
        if (opened is not RecapGridManagerOpenResult.Opened managerOpened) {
            return opened switch {
                RecapGridManagerOpenResult.Absent value => Failed(
                    "promotion-prerequisite-absent",
                    value.Dependency.ToString()
                ),
                RecapGridManagerOpenResult.Busy value => Failed(
                    "busy",
                    value.Dependency.ToString()
                ),
                RecapGridManagerOpenResult.UnsupportedSchema value => Failed(
                    "unsupported-schema",
                    $"{value.Dependency}:{value.SchemaVersion}"
                ),
                RecapGridManagerOpenResult.PlatformUnsupported value => Failed(
                    "platform-unsupported",
                    value.Dependency.ToString()
                ),
                RecapGridManagerOpenResult.Invalid value => Failed(
                    value.Code,
                    value.Detail
                ),
                _ => Failed(
                    "manager-open-invalid",
                    "Manager returned an unknown open outcome."
                )
            };
        }
        using (managerOpened.Handle) {
            var request = new RecapGridBuildRequest(
                new RecapGridBuildSelection.ExplicitCandidate(recipeDigest),
                authority.TimelineHead.HeadRowId,
                new RecapGridBuildBudget(
                    HistoryTimelineStoreLimits.MaximumRowCount,
                    1_000_000,
                    maximumNewCalls: 0,
                    TimeSpan.FromMinutes(5)
                )
            );
            RecapGridBuildResult build = _testHooks?.BuildResultOverride
                    is { } buildOverride
                ? await buildOverride(cancellationToken).ConfigureAwait(false)
                : await managerOpened.Handle.Manager.BuildAsync(
                        request,
                        NoDispatchExecutor.Instance,
                        cancellationToken
                    ).ConfigureAwait(false);
            if (build is not RecapGridBuildResult.Fulfilled fulfilled) {
                return build switch {
                    RecapGridBuildResult.FulfilledThrough
                        => Failed(
                            "fulfilled-through-not-promotable",
                            "A partial fulfillment cannot be promoted."
                        ),
                    RecapGridBuildResult.NoRows
                        => Failed(
                            "no-rows",
                            "The selected Timeline has no promotable head row."
                        ),
                    RecapGridBuildResult.NoActiveRecipe
                        => Failed(
                            "no-active-recipe",
                            "No active recipe is available."
                        ),
                    RecapGridBuildResult.RecipeAbsent
                        => Failed(
                            "recipe-absent",
                            "The candidate recipe is absent."
                        ),
                    RecapGridBuildResult.ThroughRowNotSelected
                        => Failed(
                            "through-row-not-selected",
                            "The requested Timeline head is not selected."
                        ),
                    RecapGridBuildResult.BudgetExceeded value
                        => Failed(
                            "budget-exceeded",
                            value.Kind.ToString()
                        ),
                    RecapGridBuildResult.Cancelled
                        => throw new ToolExecutionUnsettledException(
                            "manager-cancelled-after-start",
                            "Manager cancellation may follow durable Grid writes; resume with the same operation id."
                        ),
                    RecapGridBuildResult.Incomplete
                        => Failed(
                            "candidate-incomplete",
                            "The candidate recipe remains incomplete."
                        ),
                    RecapGridBuildResult.ExecutorRejected value
                        => Failed(
                            "executor-rejected",
                            $"{value.Code}:{value.Detail}"
                        ),
                    RecapGridBuildResult.ExecutorFailed value
                        => Failed(
                            "executor-failed",
                            $"{value.Code}:{value.Detail}"
                        ),
                    RecapGridBuildResult.Unavailable value
                        => Failed(value.Code, value.Detail),
                    RecapGridBuildResult.StaleTimelineHead
                        => Failed("stale-timeline-head", "Timeline changed."),
                    RecapGridBuildResult.StaleControlAuthority
                        => Failed("stale-control-head", "Control changed."),
                    RecapGridBuildResult.SettlementRequired
                        => throw new ToolExecutionUnsettledException(
                            "manager-settlement-required",
                            "Grid settlement must be reconciled before the same operation can resume."
                        ),
                    RecapGridBuildResult.Disposed
                        => Failed("disposed", "Manager is disposed."),
                    RecapGridBuildResult.Invalid value
                        => Failed(value.Code, value.Detail),
                    _ => Failed(
                        "manager-outcome-invalid",
                        "Manager returned an unknown build outcome."
                    )
                };
            }
            if (fulfilled.Proof.ControlHead != authority.ControlHead
                || fulfilled.Proof.TimelineHead != authority.TimelineHead
                || fulfilled.Proof.RecipeDigest != recipeDigest) {
                return Failed(
                    "promotion-proof-stale",
                    "The fresh promotion proof differs from the captured authority."
                );
            }
            ThrowIfCancelledBeforeMutation(cancellationToken);
            RecapGridControlOperationResult promoted = _testHooks?
                    .ControlOperationResultOverride?.Invoke()
                ?? dependencies.Control.Coordinator.CompareExchangeAgentPromotion(
                    authority.ControlHead,
                    authority.TimelineHead,
                    recipeDigest,
                    operation
                );
            return MapOperation(
                promoted,
                authority.TimelineHead
            );
        }
    }

    private AuthorityCapture CaptureAuthority(
        AgentControlDependencyResult.Opened dependencies
    ) {
        HistoryTimelineSnapshotResult timeline =
            dependencies.Timeline.Reader.ReadSnapshot();
        if (timeline is not HistoryTimelineSnapshotResult.Available
                timelineAvailable) {
            return new AuthorityCapture(default!, default!, timeline switch {
                HistoryTimelineSnapshotResult.Busy
                    => Failed("busy", "timeline"),
                HistoryTimelineSnapshotResult.UnsupportedSchema value
                    => Failed(
                        "unsupported-schema",
                        $"timeline:{value.SchemaVersion}"
                    ),
                HistoryTimelineSnapshotResult.Invalid value
                    => Failed(value.Code, value.Detail),
                _ => Failed(
                    "timeline-snapshot-invalid",
                    "Timeline returned an unknown snapshot outcome."
                )
            });
        }
        RecapGridControlSnapshotResult control =
            dependencies.Control.Reader.ReadSnapshot();
        if (control is not RecapGridControlSnapshotResult.Available
                controlAvailable) {
            return new AuthorityCapture(default!, default!, control switch {
                RecapGridControlSnapshotResult.Busy
                    => Failed("busy", "control"),
                RecapGridControlSnapshotResult.UnsupportedSchema value
                    => Failed(
                        "unsupported-schema",
                        $"control:{value.SchemaVersion}"
                    ),
                RecapGridControlSnapshotResult.Disposed
                    => Failed("disposed", "control"),
                RecapGridControlSnapshotResult.Invalid value
                    => Failed(value.Code, value.Detail),
                _ => Failed(
                    "control-snapshot-invalid",
                    "Control returned an unknown snapshot outcome."
                )
            });
        }
        if (controlAvailable.Snapshot.Head.RefId
                != timelineAvailable.Head.RefId
            || controlAvailable.Snapshot.Head.TimelineId
                != timelineAvailable.Head.TimelineId) {
            return new AuthorityCapture(
                default!,
                default!,
                Failed(
                    "authority-scope-mismatch",
                    "Control and Timeline belong to different scopes."
                )
            );
        }
        return new AuthorityCapture(
            controlAvailable.Snapshot.Head,
            timelineAvailable.Head,
            null
        );
    }

    private RecapGridControlRegistrationBundle RecipeBundle(
        AgentControlDependencyResult.Opened dependencies,
        AuthorityCapture authority,
        string canonicalBase64
    ) {
        GridBuildRecipe recipe = GridBuildRecipe.DecodeCanonical(
            DecodeBase64Exact(canonicalBase64)
        );
        HistoryTimelineAncestorWitness? witness = null;
        if (recipe.BootstrapThroughRowId is { } rowId) {
            HistoryTimelineReaderRowResult selected =
                dependencies.Timeline.Reader.ReadSelectedRow(
                    authority.TimelineHead,
                    rowId
                );
            witness = selected switch {
                HistoryTimelineReaderRowResult.Selected value
                    => value.Row.Witness,
                HistoryTimelineReaderRowResult.NotOnSelectedPath
                    => throw new InvalidDataException(
                        "Recipe bootstrap is not selected."
                    ),
                HistoryTimelineReaderRowResult.StaleTimelineHead
                    => throw new InvalidDataException(
                        "Timeline changed while resolving recipe bootstrap."
                    ),
                HistoryTimelineReaderRowResult.Busy
                    => throw new InvalidDataException(
                        "Timeline is busy."
                    ),
                HistoryTimelineReaderRowResult.Invalid value
                    => throw new InvalidDataException(value.Detail),
                _ => throw new InvalidDataException(
                    "Timeline returned an unknown row outcome."
                )
            };
        }
        return new RecapGridControlRegistrationBundle(
            [],
            [],
            [new RecapGridControlRecipeRegistration(recipe, witness)]
        );
    }

    private static RecapGridControlRegistrationBundle FamilyBundle(
        string canonicalBase64
    ) => new(
        [FamilyDefinition.DecodeCanonical(
            DecodeBase64Exact(canonicalBase64))],
        [],
        []
    );

    private static RecapGridControlRegistrationBundle DefinitionBundle(
        string canonicalBase64
    ) => new(
        [],
        [MaintainerDefinitionRevision.DecodeCanonical(
            DecodeBase64Exact(canonicalBase64))],
        []
    );

    private static byte[] DecodeBase64Exact(string value) {
        if (value.Length > MaximumCanonicalBase64Characters) {
            throw new InvalidDataException(
                "Canonical value exceeds the Agent Control byte bound."
            );
        }
        byte[] bytes = Convert.FromBase64String(value);
        if (!string.Equals(
                Convert.ToBase64String(bytes),
                value,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Canonical value base64 is not exact."
            );
        }
        return bytes;
    }

    private static AgentControlArguments ParseExact(string raw) {
        ArgumentNullException.ThrowIfNull(raw);
        byte[] bytes;
        try {
            bytes = new UTF8Encoding(false, true).GetBytes(raw);
        }
        catch (EncoderFallbackException exception) {
            throw new InvalidDataException(
                "Tool arguments are not strict UTF-8 text.",
                exception
            );
        }
        if (bytes.Length > MaximumArgumentsUtf8Bytes
            || bytes.AsSpan().StartsWith(
                new byte[] { 0xef, 0xbb, 0xbf })) {
            throw new InvalidDataException(
                "Tool arguments exceed the byte cap or contain a BOM."
            );
        }
        using JsonDocument document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            }
        );
        if (document.RootElement.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException("Tool arguments must be an object.");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? action = null;
        string? canonical = null;
        string? asset = null;
        string? recipe = null;
        foreach (JsonProperty property
                 in document.RootElement.EnumerateObject()) {
            if (!seen.Add(property.Name)) {
                throw new InvalidDataException(
                    "Tool argument properties must be unique."
                );
            }
            string value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()!
                : throw new InvalidDataException(
                    "Tool argument values must be strings."
                );
            switch (property.Name) {
                case "action": action = value; break;
                case "canonicalValueBase64": canonical = value; break;
                case "builtInAssetId": asset = value; break;
                case "recipeDigest": recipe = value; break;
                default:
                    throw new InvalidDataException(
                        "Tool arguments contain an unknown property."
                    );
            }
        }
        if (action is null) {
            throw new InvalidDataException("Tool action is required.");
        }
        bool valid = action switch {
            "inspect" => canonical is null && asset is null && recipe is null,
            "register-family" or "register-definition" or "register-recipe"
                => canonical is not null && asset is null && recipe is null,
            "provision-built-in"
                => canonical is null && asset is not null && recipe is null,
            "promote"
                => canonical is null && asset is null && recipe is not null,
            _ => false
        };
        if (!valid) {
            throw new InvalidDataException(
                "Tool action and payload fields do not form an exact V1 command."
            );
        }
        return new AgentControlArguments(action, canonical, asset, recipe);
    }

    private static ToolExecuteResult MapOperation(
        RecapGridControlOperationResult result,
        TimelineHeadRef timelineHead
    ) => result switch {
        RecapGridControlOperationResult.Applied value => Success(
            "applied", value.Head, timelineHead, value.ResultIdentity,
            false, false),
        RecapGridControlOperationResult.Replayed value => Success(
            value.InstanceReplaced
                ? "replayed-instance-replaced"
                : value.HeadAdvancedSinceApply
                    ? "replayed-head-advanced"
                    : "replayed",
            value.CurrentHead,
            timelineHead,
            value.ResultIdentity,
            value.HeadAdvancedSinceApply,
            value.InstanceReplaced
        ),
        RecapGridControlOperationResult.Conflict
            => Failed("operation-conflict", "Operation id was reused."),
        RecapGridControlOperationResult.Unauthorized value
            => Failed("unauthorized", value.Rule),
        RecapGridControlOperationResult.RecipeAbsent
            => Failed("recipe-absent", "Recipe is not registered."),
        RecapGridControlOperationResult.StaleControlHead
            => Failed("stale-control-head", "Control changed."),
        RecapGridControlOperationResult.StaleTimelineHead
            => Failed("stale-timeline-head", "Timeline changed."),
        RecapGridControlOperationResult.NotOnSelectedPath
            => Failed("not-on-selected-path", "Bootstrap row is not selected."),
        RecapGridControlOperationResult.Busy
            => Failed("busy", "Control is busy."),
        RecapGridControlOperationResult.TimelineUnsupportedSchema value
            => Failed("unsupported-schema", $"timeline:{value.SchemaVersion}"),
        RecapGridControlOperationResult.Disposed
            => Failed("disposed", "Control is disposed."),
        RecapGridControlOperationResult.LimitExceeded value
            => Failed("limit-exceeded", value.Limit),
        RecapGridControlOperationResult.CommitIndeterminate value
            => throw new ToolExecutionUnsettledException(
                "commit-indeterminate",
                $"operation={value.OperationKey};inspect-required"
            ),
        RecapGridControlOperationResult.Invalid value
            => Failed(value.Code, value.Detail),
        _ => Failed(
            "control-outcome-invalid",
            "Control returned an unknown operation outcome."
        )
    };

    private static ToolExecuteResult Success(
        string status,
        ControlHeadRef control,
        TimelineHeadRef timeline,
        string? resultIdentity,
        bool headAdvancedSinceApply,
        bool instanceReplaced
    ) => Result(
        ToolExecutionStatus.Success,
        new AgentControlResultDto(
            1,
            status,
            null,
            control.InstanceId.Value,
            control.Generation,
            control.StateDigest.Value,
            control.ActiveRecipeDigest?.Value,
            timeline.TimelineId.Value,
            timeline.Generation,
            timeline.HeadRowId?.Value,
            resultIdentity,
            headAdvancedSinceApply,
            instanceReplaced
        )
    );

    private static ToolExecuteResult Failed(string code, string detail)
        => Result(
            ToolExecutionStatus.Failed,
            new AgentControlResultDto(
                1,
                code,
                RecapGridAgentControlFactory.Bound(detail),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                false
            )
        );

    private static void ThrowIfCancelledBeforeMutation(
        CancellationToken cancellationToken
    ) {
        if (cancellationToken.IsCancellationRequested) {
            throw new ToolExecutionCancelledBeforeMutationException(
                cancellationToken
            );
        }
    }

    private static ToolExecuteResult Result(
        ToolExecutionStatus status,
        AgentControlResultDto value
    ) => ToolExecuteResult.FromText(
        status,
        Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
            value,
            AgentControlJson.Options
        ))
    );

    private sealed record AuthorityCapture(
        ControlHeadRef ControlHead,
        TimelineHeadRef TimelineHead,
        ToolExecuteResult? Failure
    );

    private sealed record AgentControlArguments(
        string Action,
        string? CanonicalValueBase64,
        string? BuiltInAssetId,
        string? RecipeDigest
    );

    private sealed record AgentControlResultDto(
        int SchemaVersion,
        string Status,
        string? Detail,
        string? ControlInstanceId,
        long? ControlGeneration,
        string? ControlStateDigest,
        string? ActiveRecipeDigest,
        string? TimelineId,
        long? TimelineGeneration,
        string? TimelineHeadRowId,
        string? ResultIdentity,
        bool HeadAdvancedSinceApply,
        bool InstanceReplaced
    );

    private sealed class NoDispatchExecutor : IRecapCellBatchExecutor {
        internal static NoDispatchExecutor Instance { get; } = new();

        public ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<RecapCellBatchExecutionResult>(
            new RecapCellBatchExecutionResult.RejectedBeforeDispatch(
                "AgentControlDispatchForbidden",
                "Promotion proof may not start new recap calls."
            )
        );
    }
}

internal enum AgentControlAction {
    [JsonStringEnumMemberName("inspect")]
    Inspect,
    [JsonStringEnumMemberName("register-family")]
    RegisterFamily,
    [JsonStringEnumMemberName("register-definition")]
    RegisterDefinition,
    [JsonStringEnumMemberName("register-recipe")]
    RegisterRecipe,
    [JsonStringEnumMemberName("provision-built-in")]
    ProvisionBuiltIn,
    [JsonStringEnumMemberName("promote")]
    Promote
}

[Description("Strict RecapGrid Agent Control V1 command.")]
internal sealed record class AgentControlMethodInput(
    [property: Description("Exact control action.")]
    [property: JsonPropertyName("action")]
    AgentControlAction Action,

    [property: Description("Exact canonical value encoded as canonical base64.")]
    [property: JsonPropertyName("canonicalValueBase64")]
    [property: StringLength(1024 * 1024)]
    string? CanonicalValueBase64,

    [property: Description("Exact code-owned built-in asset id.")]
    [property: JsonPropertyName("builtInAssetId")]
    [property: StringLength(128)]
    string? BuiltInAssetId,

    [property: Description("Exact lowercase SHA-256 recipe digest.")]
    [property: JsonPropertyName("recipeDigest")]
    [property: StringLength(64, MinimumLength = 64)]
    string? RecipeDigest
);

internal static class RecapGridAgentControlFactoryIdentity {
    internal static SessionToolRuntimeIdentity Require(ToolSession session) {
        // A ToolSession intentionally does not carry SessionJournal runtime
        // identity. Recompute from the exact visible definition and the frozen
        // capability digest placed into its immutable items by the factory.
        if (session.Items is null
            || !session.Items.TryGetValue(
                RuntimeIdentityItemKey,
                out object? value)
            || value is not SessionToolRuntimeIdentity identity) {
            throw new InvalidOperationException(
                "Agent Control ToolSession lacks its frozen runtime identity."
            );
        }
        return identity;
    }

    internal const string RuntimeIdentityItemKey =
        "atelia.recap-grid.agent-control.runtime-identity";
}
