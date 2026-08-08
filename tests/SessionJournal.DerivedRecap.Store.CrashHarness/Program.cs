using System.Text;
using System.Text.Json;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace SessionJournal.DerivedRecap.Store.CrashHarness;

internal static class Program {
    public static async Task<int> Main(string[] args) {
        if (args.Length != 3) {
            Console.Error.WriteLine(
                "usage: <create|publish|reset|rolling|building-create"
                + "|building-quarantine|executor-resume"
                + "|executor-restore> "
                + "<failpoint> <repository>"
            );
            return 2;
        }

        string operation = args[0];
        string failpoint = args[1];
        string repositoryPath = Path.GetFullPath(args[2]);
        using SessionJournalEngine engine =
            SessionJournalEngine.Open(repositoryPath);
        Action crash = () => Environment.FailFast(
            $"Intentional DerivedRecap crash at '{failpoint}'."
        );
        int workReplaceCount = 0;
        string? restoreFinalPath = null;
        string? restoreEnvelopePath = null;
        var hooks = new RecapStoreTestHooks(
            AfterPublicationSealed:
                failpoint == "publication-sealed" ? crash : null,
            BeforePublishedPromotion:
                failpoint == "promotion-before" ? crash : null,
            AfterPublishedPromotion:
                failpoint == "promotion-after" ? crash : null,
            BeforeRootCommit:
                failpoint == "root-before-commit" ? crash : null,
            AfterRootCommit:
                failpoint == "root-after-commit" ? crash : null,
            AfterResetQuarantine:
                failpoint == "reset-after-quarantine" ? crash : null,
            AfterResetNewRootCommit:
                failpoint == "reset-after-new-root-commit"
                    ? crash
                    : null,
            BeforePublicationSealInstall:
                failpoint == "publication-before-seal" ? crash : null,
            BeforeAtomicFileReplace:
                path => {
                    if (failpoint == "rolling-before-replace") {
                        crash();
                    }
                    if (failpoint
                            == "restore-envelope-before-replace"
                        && PathEquals(path, restoreEnvelopePath)) {
                        crash();
                    }
                },
            AfterAtomicFileReplace:
                path => {
                    if (failpoint == "rolling-after-replace") {
                        crash();
                    }
                    if (failpoint == "restore-final-after-replace"
                        && PathEquals(path, restoreFinalPath)) {
                        crash();
                    }
                    if (failpoint
                            == "restore-envelope-after-replace"
                        && PathEquals(path, restoreEnvelopePath)) {
                        crash();
                    }
                    if (TryParseExecutorWorkFailpoint(
                            failpoint,
                            out int failAfter
                        )
                        && IsWorkFile(path)
                        && ++workReplaceCount == failAfter) {
                        crash();
                    }
                },
            BeforeBuildingQuarantineRename:
                failpoint == "quarantine-before-rename"
                    ? crash
                    : null,
            AfterBuildingQuarantineRename:
                failpoint == "quarantine-after-rename"
                    ? crash
                    : null,
            IoObserver: (point, path) => {
                if (failpoint == "building-manifest-installed"
                    && point == RecapIoPoint.FileInstalled
                    && string.Equals(
                        Path.GetFileName(path),
                        "manifest.json",
                        StringComparison.Ordinal
                    )
                    && path.Contains(
                        ".create.",
                        StringComparison.Ordinal
                    )) {
                    crash();
                }
                if (failpoint == "building-promoted"
                    && point == RecapIoPoint.DirectoryPromoted
                    && path.Contains(
                        $"{Path.DirectorySeparatorChar}building"
                        + $"{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal
                    )) {
                    crash();
                }
            }
        );
        DerivedRecapStore store = DerivedRecapStore.OpenForTest(
            repositoryPath,
            engine.BranchRefId,
            hooks
        );

        switch (operation) {
            case "create":
                await store.CreateAsync();
                break;
            case "publish":
                var publisher = new DerivedRecapPublisher(
                    store,
                    engine.ReadView
                );
                await publisher.PublishAsync(
                    engine.ReadCurrentLineageHeaders().CapturedHead
                );
                break;
            case "reset":
                await store.ResetAsync();
                break;
            case "rolling":
                SessionCurrentLineageSnapshot lineage =
                    engine.ReadCurrentLineageHeaders();
                BuildingReadResult.Available building =
                    await store.ReadBuildingAsync(lineage.CapturedHead)
                        is BuildingReadResult.Available available
                        ? available
                        : throw new InvalidDataException(
                            "Rolling crash fixture Building is unavailable."
                        );
                RecapBlockPlan plan =
                    building.Snapshot.Manifest.Blocks.Single();
                BuildingBlockInspection inspection =
                    await store.InspectBuildingBlockAsync(
                        building.Snapshot.Descriptor,
                        plan.RecapBlockId
                    );
                MaintainRecapBlockPlan maintain =
                    plan as MaintainRecapBlockPlan
                    ?? throw new InvalidDataException(
                        "Rolling crash fixture plan is not Maintain."
                    );
                await store.AdvanceRollingCheckpointAsync(
                    building.Snapshot.Descriptor,
                    plan.RecapBlockId,
                    inspection.Checkpoint.StateToken,
                    DerivedRecapCodec.CreateBlock(
                        plan,
                        maintain.CatchUpBoundaries[^1].Address,
                        "new checkpoint"
                    )
                );
                break;
            case "building-create":
                SessionCurrentLineageSnapshot buildingLineage =
                    engine.ReadCurrentLineageHeaders();
                EventAddress buildingAnchor =
                    buildingLineage.CapturedHead;
                EventAddress replayStart =
                    engine.ReadHistoryPlanningWindow()
                        .StartExclusive;
                var buildingPlan = new MaintainRecapBlockPlan(
                    new RecapBlockId("roleplay.self"),
                    new ContextHeaderBlockPath(
                        ContextHeaderCarrier.System,
                        "roleplay.self"
                    ),
                    "roleplay.autobiographical",
                    "sha256:0000000000000000000000000000000000000000000000000000000000000000",
                    new EmptyRecapMaintainSource(
                        replayStart,
                        engine.ResolveContextAnchorSetupReferences(
                            replayStart
                        )
                    ),
                    [
                        new RecapReplayBoundary(
                            buildingAnchor,
                            engine.ResolveContextAnchorSetupReferences(
                                buildingAnchor
                            )
                        )
                    ],
                    DerivedRecapCodec
                        .ComputePriorContextPayloadSha256(
                            EmptyRecapPriorContext.Instance
                        )
                );
                await store.CreateBuildingAsync(
                    DerivedRecapCodec.CreateManifest(
                        engine.BranchRefId,
                        buildingAnchor,
                        engine.ResolveContextAnchorSetupReferences(
                            buildingAnchor
                        ),
                        EmptyRecapPriorContext.Instance,
                        [buildingPlan]
                    )
                );
                break;
            case "building-quarantine":
                EventAddress quarantineAnchor =
                    engine.ReadCurrentLineageHeaders().CapturedHead;
                _ = await store.QuarantineBuildingAsync(
                    quarantineAnchor
                );
                break;
            case "executor-resume":
                DerivedRecapExecutionResult result =
                    await ResumeExecutorAsync(
                        engine,
                        store,
                        repositoryPath
                    );
                Console.Out.WriteLine(
                    $"executor-result:{result.GetType().Name}"
                );
                if (failpoint == "none") {
                    return 0;
                }
                break;
            case "executor-restore":
                SessionCurrentLineageSnapshot restoreLineage =
                    engine.ReadCurrentLineageHeaders();
                string publishedPath =
                    store.GetPublishedPathForTest(
                        restoreLineage.CapturedHead
                    );
                string[] finalPaths = Directory.GetFiles(
                    Path.Combine(publishedPath, "blocks"),
                    "*.json",
                    SearchOption.TopDirectoryOnly
                );
                if (finalPaths.Length != 1) {
                    throw new InvalidDataException(
                        "Executor restore crash fixture requires exactly "
                        + "one Published final block."
                    );
                }
                restoreFinalPath = Path.GetFullPath(finalPaths[0]);
                restoreEnvelopePath = Path.GetFullPath(
                    Path.Combine(publishedPath, "publication.json")
                );
                DerivedRecapRestoreResult restoreResult =
                    await RestoreExecutorAsync(
                        engine,
                        store,
                        repositoryPath,
                        restoreLineage
                    );
                Console.Out.WriteLine(
                    $"executor-result:{restoreResult.GetType().Name}"
                );
                if (failpoint == "none") {
                    return 0;
                }
                break;
            default:
                Console.Error.WriteLine(
                    $"unknown operation '{operation}'"
                );
                return 2;
        }

        Console.Error.WriteLine(
            $"failpoint '{failpoint}' was not reached"
        );
        return 3;
    }

    private static async ValueTask<DerivedRecapExecutionResult>
        ResumeExecutorAsync(
        SessionJournalEngine engine,
        DerivedRecapStore store,
        string repositoryPath
    ) {
        EventAddress admission =
            engine.ReadCurrentLineageHeaders().CapturedHead;
        BuildingReadResult.Available building =
            await store.ReadBuildingAsync(admission)
                is BuildingReadResult.Available available
                ? available
                : throw new InvalidDataException(
                    "Executor crash fixture Building is unavailable."
                );
        var maintainers = new List<IRecapBlockMaintainer>();
        var capabilities = new List<RecapProfilePlanningDescriptor>();
        foreach (RecapBlockPlan plan
                 in building.Snapshot.Manifest.Blocks) {
            MaintainRecapBlockPlan maintain =
                plan as MaintainRecapBlockPlan
                ?? throw new InvalidDataException(
                    "Executor crash fixture supports Maintain plans only."
                );
            var maintainer = new DurableDeterministicMaintainer(
                maintain.MaintainerId,
                plan.Target,
                Path.Combine(
                    repositoryPath,
                    "recap-maintainer-calls.jsonl"
                )
            );
            maintainers.Add(maintainer);
            capabilities.Add(new RecapProfilePlanningDescriptor(
                $"crash-{plan.RecapBlockId.Value}",
                plan.RecapBlockId,
                plan.Target,
                maintainer.Id,
                maintainer.CapabilityFingerprint
            ));
        }
        var prepared = AssertReady(
            await DerivedRecapOperationPreparer
                .PrepareExactBuildingAsync(
                    engine.ReadView,
                    store,
                    new RecapMaintainerCapabilitySnapshot(capabilities),
                    admission
                )
        );
        var executor = new DerivedRecapPreparedExecutor(
            engine.ReadView,
            store,
            prepared,
            new RecapBlockMaintainerRegistry(maintainers)
        );
        return await executor.ExecuteAsync();
    }

    private static PreparedRecapOperationAuthority AssertReady(
        DerivedRecapOperationPreparationResult result
    ) => result is DerivedRecapOperationPreparationResult.Ready ready
        ? ready.Authority
        : throw new InvalidDataException(
            $"Executor crash fixture preparation failed: {result}."
        );

    private static async ValueTask<DerivedRecapRestoreResult>
        RestoreExecutorAsync(
        SessionJournalEngine engine,
        DerivedRecapStore store,
        string repositoryPath,
        SessionCurrentLineageSnapshot lineage
    ) {
        DerivedRecapLineageView lineageView =
            DerivedRecapLineageView.Capture(store, engine.ReadView);
        PublishedRestoreInspectionResult.Available available =
            await lineageView
                .InspectPublishedForOfflineDiagnosticsAsync(
                    lineage.CapturedHead
                )
                is PublishedRestoreInspectionResult.Available exact
                    ? exact
                    : throw new InvalidDataException(
                        "Executor restore crash fixture exact Published "
                        + "set is unavailable for restore."
                    );
        MaintainRecapBlockPlan[] plans = available.Inspection
            .FrozenPlan.Blocks
            .Select(plan =>
                plan as MaintainRecapBlockPlan
                ?? throw new InvalidDataException(
                    "Executor restore crash fixture supports Maintain "
                    + "plans only."
                ))
            .ToArray();
        var maintainers = plans
            .Select(plan =>
                (IRecapBlockMaintainer)new DurableDeterministicMaintainer(
                    plan.MaintainerId,
                    plan.Target,
                    Path.Combine(
                        repositoryPath,
                        "recap-maintainer-calls.jsonl"
                    )
                ))
            .ToArray();
        var executor = new DerivedRecapRestoreExecutor(
            engine.ReadView,
            store,
            new RecapBlockMaintainerRegistry(maintainers)
        );
        return await executor.RestoreAsync(
            lineage.CapturedHead,
            lineage.CapturedHead
        );
    }

    private static bool TryParseExecutorWorkFailpoint(
        string failpoint,
        out int failAfter
    ) {
        const string prefix = "executor-work-after-";
        failAfter = 0;
        return failpoint.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(
                failpoint.AsSpan(prefix.Length),
                out failAfter
            )
            && failAfter > 0;
    }

    private static bool IsWorkFile(string path)
        => path.Contains(
            $"{Path.DirectorySeparatorChar}work"
            + $"{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal
        );

    private static bool PathEquals(string path, string? expected)
        => expected is not null
            && string.Equals(
                Path.GetFullPath(path),
                expected,
                StringComparison.Ordinal
            );

    private sealed class DurableDeterministicMaintainer
        : IRecapBlockMaintainer {
        private readonly string _logPath;

        public DurableDeterministicMaintainer(
            string id,
            ContextHeaderBlockPath target,
            string logPath
        ) {
            Id = id;
            Target = target;
            _logPath = logPath;
        }

        public string Id { get; }
        public ContextHeaderBlockPath Target { get; }
        public string CapabilityFingerprint { get; } =
            "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        public object RuntimeGroupAffinity => this;

        public ValueTask<RecapMaintenanceSuccess> MaintainAsync(
            RecapMaintenanceEpochInput request,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            int ordinal = File.Exists(_logPath)
                ? File.ReadLines(_logPath).Count(line =>
                    line.Contains(
                        $"\"MaintainerId\":\"{Id}\"",
                        StringComparison.Ordinal
                    ))
                : 0;
            var entry = new MaintainerCallLogEntry(
                Id,
                ordinal + 1,
                request.SourceId ?? string.Empty,
                request.PriorContext.SystemPromptFragment,
                request.PriorContext.ObservationMessage,
                request.PriorContext.ActionMessage
            );
            byte[] line = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(entry) + "\n"
            );
            using (var stream = new FileStream(
                       _logPath,
                       FileMode.Append,
                       FileAccess.Write,
                       FileShare.Read,
                       bufferSize: 4096,
                       FileOptions.WriteThrough
                   )) {
                stream.Write(line);
                stream.Flush(flushToDisk: true);
            }
            if (string.Equals(
                    Id,
                    "zeta-maintainer",
                    StringComparison.Ordinal
                )
                && ordinal == 0) {
                throw new InvalidOperationException(
                    "Intentional first zeta failure."
                );
            }
            return ValueTask.FromResult(
                (RecapMaintenanceSuccess)new
                    RecapMaintenanceSuccess.Updated(
                        $"{Id}:{ordinal + 1}"
                    )
            );
        }
    }

    private sealed record MaintainerCallLogEntry(
        string MaintainerId,
        int Ordinal,
        string SourceDescription,
        string PriorSystem,
        string PriorObservation,
        string PriorAction
    );
}
