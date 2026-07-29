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
                + "|executor-resume> "
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
                failpoint == "rolling-before-replace"
                    ? _ => crash()
                    : null,
            AfterAtomicFileReplace:
                path => {
                    if (failpoint == "rolling-after-replace") {
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
                var publisher = new DerivedRecapPublisher(store, engine);
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
                        maintain.CatchUpThrough[^1],
                        "new checkpoint"
                    )
                );
                break;
            case "building-create":
                SessionCurrentLineageSnapshot buildingLineage =
                    engine.ReadCurrentLineageHeaders();
                EventAddress buildingAnchor =
                    buildingLineage.CapturedHead;
                var buildingPlan = new MaintainRecapBlockPlan(
                    new RecapBlockId("roleplay.self"),
                    new ContextHeaderBlockPath(
                        ContextHeaderCarrier.System,
                        "roleplay.self"
                    ),
                    "roleplay.autobiographical",
                    new EmptyRecapMaintainSource(
                        engine.ReadHistoryPlanningWindow()
                            .StartExclusive
                    ),
                    [buildingAnchor],
                    EmptyRecapPriorContext.Instance
                );
                await store.CreateBuildingAsync(
                    DerivedRecapCodec.CreateManifest(
                        engine.BranchRefId,
                        buildingAnchor,
                        [buildingPlan]
                    )
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
        var catalog = new List<RecapBlockCatalogEntry>();
        var maintainers = new List<IRecapBlockMaintainer>();
        foreach (RecapBlockPlan plan
                 in building.Snapshot.Manifest.Blocks) {
            MaintainRecapBlockPlan maintain =
                plan as MaintainRecapBlockPlan
                ?? throw new InvalidDataException(
                    "Executor crash fixture supports Maintain plans only."
                );
            catalog.Add(new RecapBlockCatalogEntry(
                plan.RecapBlockId,
                plan.Target,
                maintain.MaintainerId,
                plan.MaxContentUtf8Bytes
            ));
            maintainers.Add(new DurableDeterministicMaintainer(
                maintain.MaintainerId,
                plan.Target,
                Path.Combine(
                    repositoryPath,
                    "recap-maintainer-calls.jsonl"
                )
            ));
        }
        var executor = new DerivedRecapPlannerExecutor(
            engine,
            store,
            new RecapPlannerConfig(
                catalog,
                rawGrowthTrigger: 0,
                rawGrowthHardLimit: 10_000,
                maxRouteEndpointsPerBlock: 16,
                maxMaintainerCallsPerBuild: 32,
                maxRawEventsPerStep: 10_000,
                maxRawEventsPerBuild: 50_000
            ),
            new NoBuildPolicy(),
            new RecapBlockMaintainerRegistry(maintainers)
        );
        return await executor.ResumeAsync(admission);
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

    private sealed class NoBuildPolicy : IRecapPlanningPolicy {
        public RecapPlanningPolicyDecision Decide(
            RecapPlanningPolicyContext context
        ) => new RecapPlanningPolicyDecision.NoBuild("resume only");
    }

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

        public ValueTask<RecapBlockMaintenanceResult> MaintainAsync(
            RecapBlockMaintenanceRequest request,
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
                request.OldBlock.Text,
                request.RecentHistory.SourceId ?? string.Empty
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
                new RecapBlockMaintenanceResult(
                    Id,
                    Target,
                    new ContextHeaderBlock(
                        request.OldBlock.Text
                        + $"|{Id}:{ordinal + 1}"
                    )
                )
            );
        }
    }

    private sealed record MaintainerCallLogEntry(
        string MaintainerId,
        int Ordinal,
        string OldContent,
        string SourceDescription
    );
}
