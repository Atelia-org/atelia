using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli.Tests;

internal sealed class RealLegacyExportFactAttribute : FactAttribute {
    public const string SourceEnvironment =
        "ATELIA_REAL_LEGACY_UPGRADE_EXPORT";

    public RealLegacyExportFactAttribute() {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    SourceEnvironment
                )
            )) {
            Skip = $"{SourceEnvironment} is required for the "
                + "external real-data release gate.";
        }
    }
}

public sealed class DerivedRecapRealDataAcceptanceTests {
    private const string SourceExportEnvironment =
        RealLegacyExportFactAttribute.SourceEnvironment;
    private const string ReportEnvironment =
        "ATELIA_DERIVED_RECAP_ACCEPTANCE_REPORT";

    [RealLegacyExportFact]
    public async Task ImportedRealExportSurvivesFullRecapAndRecoveryFlow() {
        string? configuredSource = Environment.GetEnvironmentVariable(
            SourceExportEnvironment
        );
        Assert.False(string.IsNullOrWhiteSpace(configuredSource));

        string sourcePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(configuredSource!)
        );
        Assert.True(
            File.Exists(sourcePath),
            $"{SourceExportEnvironment} must identify an existing "
            + "legacy-upgrade export JSON file."
        );
        TreeFingerprint sourceBefore =
            FingerprintSourceFile(sourcePath);
        Assert.Equal(
            new TreeFingerprint(
                FileCount: 1,
                TotalBytes: 1_281_881,
                Sha256: "b71822a27003e8d9f9b9c0ff956ca7c268267aba72221be89df154ed7d4751f3"
            ),
            sourceBefore
        );
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "atelia-derived-recap-real-acceptance",
            Guid.NewGuid().ToString("N")
        );
        string copyPath = Path.Combine(tempRoot, "session-copy");
        EnsureDisjoint(sourcePath, copyPath);

        try {
            Directory.CreateDirectory(tempRoot);
            Assert.Equal(0, Program.MainCore([
                "import-legacy-json",
                "--input", sourcePath,
                "--output", copyPath
            ], ThrowingCompletionClientFactory.Instance));
            AssertRealCompletedTurnProjection(sourcePath, copyPath);
            Assert.Equal(0, Program.MainCore([
                "recap", "planner-config", "init",
                "--input", copyPath
            ], ThrowingCompletionClientFactory.Instance));
            RawSnapshot initialRaw = ReadRawSnapshot(copyPath);
            string legacyV1Sentinel = Path.Combine(
                copyPath,
                "derived",
                "memory",
                "v1",
                "acceptance-invalid-sentinel.bin"
            );
            Directory.CreateDirectory(
                Path.GetDirectoryName(legacyV1Sentinel)!
            );
            File.WriteAllBytes(
                legacyV1Sentinel,
                "not-a-derived-memory-record"u8.ToArray()
            );
            TreeFingerprint legacyV1Before = FingerprintTree(
                Path.Combine(
                    copyPath,
                    "derived",
                    "memory",
                    "v1"
                ),
                allowMissing: true
            );
            string branchName =
                SJ.SessionJournalDefaults.MainBranchName;
            string connectionsPath = WriteConnections(tempRoot);

            string createReport =
                Path.Combine(tempRoot, "recap-create.json");
            Assert.Equal(0, Program.MainCore([
                "recap", "create",
                "--input", copyPath,
                "--branch", branchName,
                "--report-json", createReport
            ], ThrowingCompletionClientFactory.Instance));

            string runReport =
                Path.Combine(tempRoot, "recap-run.json");
            var runFactory = new ScriptedCompletionClientFactory(
                "acceptance recap",
                failAtCall: 4
            );
            Assert.Equal(2, Program.MainCore(
                RecapExecutionArgs(
                    "run",
                    copyPath,
                    branchName,
                    connectionsPath,
                    Path.Combine(tempRoot, "run-calls"),
                    runReport
                ),
                runFactory
            ));
            Assert.True(
                string.Equals(
                    "BlockFailed",
                    ReadString(runReport, "resultStatus"),
                    StringComparison.Ordinal
                ),
                File.ReadAllText(runReport)
            );
            Assert.Equal(4, runFactory.CallCount);
            string admission = ReadString(runReport, "anchor");
            EventAddress admissionAddress =
                SJ.EventAddressTextCodec.Parse(admission);
            LoadSelectionReport loadSelection =
                ReadLoadSelection(runReport);
            RecapCadenceConfig cadence =
                RecapCliComposition.DefaultComposition
                    .PlanningInputs.Cadence;
            Assert.Equal(
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                loadSelection.HistoryUnitLoadEstimatorId
            );
            Assert.True(
                loadSelection.GrowthHistoryLoad
                    >= cadence.BuildThresholdHistoryLoad.Value
            );
            Assert.True(
                loadSelection.SelectedAbsorbedHistoryLoad
                    >= cadence.RecapBuildIntervalHistoryLoad.Value
            );
            Assert.True(
                loadSelection.SelectedRecentHistoryLoad
                    >= cadence.MinimumRecentHistoryLoad.Value
            );

            RefId branchRefId;
            int maintainedBlockCount;
            int routeEndpointCount;
            using (var engine = SJ.SessionJournalEngine.OpenReadOnly(
                       copyPath,
                       branchName
                   )) {
                branchRefId = engine.BranchRefId;
                DerivedRecapStore store = DerivedRecapStore.Open(
                    copyPath,
                    branchRefId
                );
                BuildingPlanReadResult.Available building = Assert.IsType<
                    BuildingPlanReadResult.Available
                >(
                    await store.ReadBuildingPlanAsync(admissionAddress)
                );
                MaintainRecapBlockPlan[] maintained = [
                    .. building.Snapshot.Manifest.Blocks
                        .OfType<MaintainRecapBlockPlan>()
                ];
                maintainedBlockCount = maintained.Length;
                routeEndpointCount = maintained.Sum(
                    static block => block.CatchUpBoundaries.Count
                );
                Assert.Equal(2, maintainedBlockCount);
                Assert.Equal(4, routeEndpointCount);
            }

            string resumeReport =
                Path.Combine(tempRoot, "recap-resume.json");
            var resumeFactory = new ScriptedCompletionClientFactory(
                "acceptance recap"
            );
            Assert.Equal(0, Program.MainCore(
                RecapExecutionArgs(
                    "resume",
                    copyPath,
                    branchName,
                    connectionsPath,
                    Path.Combine(tempRoot, "resume-calls"),
                    resumeReport,
                    admission
                ),
                resumeFactory
            ));
            Assert.Equal(1, resumeFactory.CallCount);
            string failedSuffixRequestSha256 =
                CanonicalRequestSha256(runFactory.Requests[^1]);
            Assert.Equal(
                failedSuffixRequestSha256,
                CanonicalRequestSha256(
                    Assert.Single(resumeFactory.Requests)
                )
            );
            Assert.Equal(
                "Published",
                ReadString(resumeReport, "resultStatus")
            );

            PublishedRecapDescriptor selected =
                await SelectStrictLatestAsync(
                    copyPath,
                    branchName
                );
            Assert.Equal(
                admissionAddress,
                selected.SetAdmissionAnchor
            );
            string exactPublishedPath = Path.Combine(
                copyPath,
                "derived",
                "recap",
                "v4",
                "refs",
                branchRefId.ToHexString(),
                "published",
                EventAddressFileNameCodec.Format(
                    selected.SetAdmissionAnchor
                )
            );
            string[] blockFiles = Directory.EnumerateFiles(
                    Path.Combine(exactPublishedPath, "blocks"),
                    "*.json",
                    SearchOption.TopDirectoryOnly
                )
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(2, blockFiles.Length);
            string damagedBlock = blockFiles[0];
            string healthyBlock = blockFiles[1];
            string damagedBlockId =
                Path.GetFileNameWithoutExtension(damagedBlock);
            string damagedBlockShaBefore = HashFile(damagedBlock);
            string healthyBlockShaBefore = HashFile(healthyBlock);
            await File.AppendAllTextAsync(
                damagedBlock,
                "\nacceptance-corruption"
            );
            Assert.NotEqual(
                damagedBlockShaBefore,
                HashFile(damagedBlock)
            );

            using (var engine = SJ.SessionJournalEngine.OpenReadOnly(
                       copyPath,
                       branchName
                   )) {
                DerivedRecapStore store = DerivedRecapStore.Open(
                    copyPath,
                    engine.BranchRefId
                );
                DerivedRecapSelection.Selected damagedSelection =
                    Assert.IsType<DerivedRecapSelection.Selected>(
                        await DerivedRecapLineageView
                            .Capture(store, engine.ReadView)
                            .SelectNthPreviousAsync(0)
                    );
                Assert.Equal(selected, damagedSelection.Descriptor);
            }

            string preRestoreMaterializationReport = Path.Combine(
                tempRoot,
                "materialize-before-restore.json"
            );
            Assert.Equal(2, Program.MainCore([
                "recap", "materialize-inspect",
                "--input", copyPath,
                "--branch", branchName,
                "--nth-previous", "0",
                "--report-json", preRestoreMaterializationReport
            ], ThrowingCompletionClientFactory.Instance));
            using (JsonDocument report = JsonDocument.Parse(
                       File.ReadAllText(
                           preRestoreMaterializationReport
                       )
                   )) {
                Assert.Equal(
                    "Invalid",
                    report.RootElement.GetProperty("status").GetString()
                );
                JsonElement defect = Assert.Single(
                    report.RootElement
                        .GetProperty("defects")
                        .EnumerateArray()
                );
                Assert.Equal(
                    "MaterializationInvalid",
                    defect.GetProperty("code").GetString()
                );
            }

            string restoreReport =
                Path.Combine(tempRoot, "recap-restore.json");
            var restoreFactory = new ScriptedCompletionClientFactory(
                "acceptance restored recap"
            );
            Assert.Equal(0, Program.MainCore(
                RecapExecutionArgs(
                    "restore",
                    copyPath,
                    branchName,
                    connectionsPath,
                    Path.Combine(tempRoot, "restore-calls"),
                    restoreReport,
                    admission,
                    SJ.EventAddressTextCodec.Format(initialRaw.Head)
                ),
                restoreFactory
            ));
            Assert.Equal(
                "Restored",
                ReadString(restoreReport, "resultStatus")
            );
            Assert.Equal(0, restoreFactory.CallCount);
            bool damagedBlockRestored = string.Equals(
                damagedBlockShaBefore,
                HashFile(damagedBlock),
                StringComparison.Ordinal
            );
            Assert.True(damagedBlockRestored);
            Assert.Equal(
                healthyBlockShaBefore,
                HashFile(healthyBlock)
            );
            bool otherBlockUnchanged = string.Equals(
                healthyBlockShaBefore,
                HashFile(healthyBlock),
                StringComparison.Ordinal
            );
            Assert.True(otherBlockUnchanged);
            string postRestoreMaterializationReport = Path.Combine(
                tempRoot,
                "materialize-after-restore.json"
            );
            Assert.Equal(0, Program.MainCore([
                "recap", "materialize-inspect",
                "--input", copyPath,
                "--branch", branchName,
                "--nth-previous", "0",
                "--report-json", postRestoreMaterializationReport
            ], ThrowingCompletionClientFactory.Instance));
            using (JsonDocument report = JsonDocument.Parse(
                       File.ReadAllText(
                           postRestoreMaterializationReport
                       )
                   )) {
                Assert.Equal(
                    "Selected",
                    report.RootElement.GetProperty("status").GetString()
                );
                Assert.Equal(
                    2,
                    report.RootElement
                        .GetProperty("contributions")
                        .GetArrayLength()
                );
                Assert.Empty(
                    report.RootElement
                        .GetProperty("defects")
                        .EnumerateArray()
                );
            }
            RawSnapshot afterRestoreRaw =
                ReadRawSnapshot(copyPath);
            AssertRawUnchanged(initialRaw, afterRestoreRaw);
            Assert.Equal(
                legacyV1Before,
                FingerprintTree(
                    Path.Combine(
                        copyPath,
                        "derived",
                        "memory",
                        "v1"
                    ),
                    allowMissing: true
                )
            );

            string onlineReport =
                Path.Combine(tempRoot, "online.json");
            var onlineFactory = new ScriptedCompletionClientFactory(
                "acceptance agent answer"
            );
            Assert.Equal(0, Program.MainCore([
                "run-online-turn",
                "--input", copyPath,
                "--branch", branchName,
                "--connections", connectionsPath,
                "--output", onlineReport,
                "--call-log-dir",
                Path.Combine(tempRoot, "online-calls"),
                "--message", "acceptance online observation"
            ], onlineFactory));
            Assert.Equal(1, onlineFactory.CallCount);
            Assert.Equal(
                "atelia.session-journal.online-turn-run.v6",
                ReadString(onlineReport, "schema")
            );
            AssertInitialPrefixPreserved(
                initialRaw,
                ReadRawSnapshot(copyPath)
            );

            PreparedSnapshot prepared =
                await LeavePreparedAsync(
                    copyPath,
                    branchName,
                    branchRefId
                );
            string recapV4Root = Path.Combine(
                copyPath,
                "derived",
                "recap",
                "v4"
            );
            Assert.True(Directory.Exists(recapV4Root));
            Directory.Delete(recapV4Root, recursive: true);
            var preparedFactory =
                new ScriptedCompletionClientFactory(
                    "acceptance prepared recovery"
                );
            string preparedReport =
                Path.Combine(tempRoot, "prepared-reopen.json");
            Assert.Equal(0, Program.MainCore([
                "run-online-turn",
                "--input", copyPath,
                "--branch", branchName,
                "--connections", connectionsPath,
                "--output", preparedReport,
                "--call-log-dir",
                Path.Combine(tempRoot, "prepared-calls")
            ], preparedFactory));
            Assert.Equal(1, preparedFactory.CallCount);
            CompletionRequest recovered =
                Assert.Single(preparedFactory.Requests);
            Assert.Equal(
                prepared.CanonicalRequestSha256,
                Sha256(
                    SJ.SessionRequestCanonicalizer.Canonicalize(
                        recovered
                    )
                )
            );
            Assert.False(Directory.Exists(recapV4Root));

            RawSnapshot finalRaw = ReadRawSnapshot(copyPath);
            AssertInitialPrefixPreserved(initialRaw, finalRaw);
            SJ.SessionEventKind[] appendedKinds = [
                .. finalRaw.Kinds.Skip(initialRaw.Kinds.Count)
            ];
            Assert.Equal(
                new[] {
                    SJ.SessionEventKind.RuntimeConfigSetup,
                    SJ.SessionEventKind.ObservationAccepted,
                    SJ.SessionEventKind.CompletionRequestPrepared,
                    SJ.SessionEventKind.CompletionAttemptStarted,
                    SJ.SessionEventKind.AgentActionProduced,
                    SJ.SessionEventKind.ObservationAccepted,
                    SJ.SessionEventKind.CompletionRequestPrepared,
                    SJ.SessionEventKind.CompletionAttemptStarted,
                    SJ.SessionEventKind.AgentActionProduced
                },
                appendedKinds
            );
            EventAddress desiredRuntimeAddress =
                finalRaw.Addresses[initialRaw.Addresses.Count];
            using (var engine = SJ.SessionJournalEngine.OpenReadOnly(
                       copyPath
                   )) {
                using JsonDocument runtimeDocument = JsonDocument.Parse(
                    engine.ReadPayloadBytes(desiredRuntimeAddress)
                );
                JsonElement body = runtimeDocument.RootElement
                    .GetProperty("body");
                Assert.Equal("model-a", body.GetProperty("modelId")
                    .GetString());
                Assert.Equal(
                    "surface-a",
                    body.GetProperty("completionSurfaceId").GetString()
                );
                Assert.Equal(
                    SJ.SessionJournalDefaults.Schema,
                    body.GetProperty("schema").GetString()
                );
                Assert.Equal(
                    0,
                    body.GetProperty("derivedContext")
                        .GetProperty("nthPrevious")
                        .GetInt32()
                );
            }
            Assert.Equal(
                legacyV1Before,
                FingerprintTree(
                    Path.Combine(
                        copyPath,
                        "derived",
                        "memory",
                        "v1"
                    ),
                    allowMissing: true
                )
            );
            TreeFingerprint sourceAfter =
                FingerprintSourceFile(sourcePath);
            Assert.Equal(sourceBefore, sourceAfter);

            string? acceptanceReport =
                Environment.GetEnvironmentVariable(
                    ReportEnvironment
                );
            if (!string.IsNullOrWhiteSpace(acceptanceReport)) {
                string reportPath = Path.GetFullPath(
                    acceptanceReport
                );
                EnsureDisjoint(sourcePath, reportPath);
                EnsureDisjoint(copyPath, reportPath);
                WriteJsonAtomically(
                    reportPath,
                    new AcceptanceReport(
                        "atelia.session-journal."
                        + "derived-recap-real-acceptance.v3",
                        "LegacyUpgradeExport",
                        "ImportLegacyJson",
                        new SourceReport(
                            sourceBefore.FileCount,
                            sourceBefore.TotalBytes,
                            sourceBefore.Sha256
                        ),
                        ConfigReport.From(
                            RecapCliComposition
                                .DefaultComposition
                        ),
                        loadSelection,
                        branchRefId.ToHexString(),
                        admission,
                        new FrozenPlanReport(
                            maintainedBlockCount,
                            routeEndpointCount
                        ),
                        new CallCountReport(
                            runFactory.CallCount,
                            resumeFactory.CallCount,
                            restoreFactory.CallCount,
                            onlineFactory.CallCount,
                            preparedFactory.CallCount,
                            failedSuffixRequestSha256,
                            ResumeMatchedFailedSuffix: true
                        ),
                        new CorruptionReport(
                            damagedBlockId,
                            "FinalPayloadChanged",
                            "Selected",
                            "Invalid",
                            "Restored",
                            "Selected",
                            damagedBlockRestored,
                            otherBlockUnchanged
                        ),
                        prepared.CanonicalRequestSha256,
                        new PrefixReport(
                            initialRaw.Addresses.Count,
                            finalRaw.Addresses.Count,
                            HashAddresses(initialRaw.Addresses),
                            HashAddresses(
                                finalRaw.Addresses.Take(
                                    initialRaw.Addresses.Count
                                )
                            ),
                            Preserved: true,
                            [
                                .. appendedKinds.Select(
                                    static kind => kind.ToString()
                                )
                            ]
                        ),
                        new RawFingerprintReport(
                            initialRaw.Files.FileCount,
                            initialRaw.Files.TotalBytes,
                            initialRaw.Files.Sha256,
                            HashAddresses(initialRaw.Addresses)
                        ),
                        new LegacyV1Report(
                            legacyV1Before.FileCount,
                            legacyV1Before.TotalBytes,
                            legacyV1Before.Sha256,
                            Unchanged: true
                        ),
                        RawUnchangedThroughRestore: true,
                        SourceUnchanged: true,
                        RecapV4AbsentAfterPreparedRecovery: true
                    )
                );
                AssertContentFreeReport(
                    File.ReadAllText(reportPath)
                );
            }
        }
        finally {
            TreeFingerprint sourceAfter =
                FingerprintSourceFile(sourcePath);
            Assert.Equal(sourceBefore, sourceAfter);
            try {
                if (Directory.Exists(tempRoot)) {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup for the isolated acceptance copy.
            }
        }
    }

    private static async Task<PublishedRecapDescriptor>
        SelectStrictLatestAsync(
        string path,
        string branchName
    ) {
        using var engine = SJ.SessionJournalEngine.OpenReadOnly(
            path,
            branchName
        );
        DerivedRecapStore store = DerivedRecapStore.Open(
            path,
            engine.BranchRefId
        );
        DerivedRecapSelection selection =
            await DerivedRecapLineageView
                .Capture(store, engine.ReadView)
                .SelectNthPreviousAsync(0);
        return Assert
            .IsType<DerivedRecapSelection.Selected>(selection)
            .Descriptor;
    }

    private static async Task<PreparedSnapshot> LeavePreparedAsync(
        string path,
        string branchName,
        RefId branchRefId
    ) {
        CompletionConnectionConfig connection = Connection();
        var client = new ScriptedCompletionClient("must not run");
        using (var engine = SJ.SessionJournalEngine.OpenForTest(
                   path,
                   branchName,
                   new SJ.SessionRuntime(client),
                   new SJ.SessionJournalTestHooks(
                       SJ.SessionJournalFailpoint
                           .AfterRequestPreparedCommitted
                   )
               )) {
            var source = new DerivedRecapContextCandidateSource(
                DerivedRecapStore.Open(path, branchRefId),
                engine.ReadView
            );
            engine.UseRuntime(new SJ.SessionRuntime(
                client,
                CompletionTarget:
                    CompletionTargetIdentityFactory.Create(
                        connection,
                        client
                    ),
                MaxTokens: connection.MaxTokens,
                ContextCandidateSource: source
            ));
            SJ.SessionJournalFailpointException failure =
                await Assert.ThrowsAsync<
                    SJ.SessionJournalFailpointException
                >(() => engine.SendAsync(
                    "acceptance prepared observation",
                    CancellationToken.None
                ));
            Assert.Equal(
                SJ.SessionJournalFailpoint
                    .AfterRequestPreparedCommitted,
                failure.Failpoint
            );
            Assert.Equal(0, client.CallCount);
        }

        EventAddress preparedAddress;
        using (EventJournal.EventJournal journal =
            EventJournal.EventJournal.OpenReadOnlyExisting(path)) {
            RefId branch = journal.OpenBranch(branchName).Unwrap();
            preparedAddress = journal.GetHead(branch)!.Value;
            byte[] canonical =
                SJ.SessionPreparedRequestReconstructor.Reconstruct(
                    journal,
                    preparedAddress
                ).CanonicalBytes;
            return new PreparedSnapshot(
                preparedAddress,
                Sha256(canonical)
            );
        }
    }

    private static RawSnapshot ReadRawSnapshot(string path) {
        using var engine = SJ.SessionJournalEngine.OpenReadOnly(path);
        SJ.SessionCurrentLineageSnapshot lineage =
            engine.ReadCurrentLineageHeaders();
        EventAddress[] addresses = [
            .. lineage.HeadToRoot
                .Reverse()
                .Select(static node => node.Address)
        ];
        SJ.SessionEventKind[] kinds;
        using (EventJournal.EventJournal journal =
            EventJournal.EventJournal.OpenReadOnlyExisting(path)) {
            kinds = [
                .. addresses.Select(address =>
                    (SJ.SessionEventKind)journal
                        .ReadEventHeaderPreview(address)
                        .Unwrap()
                        .OpaqueEventKind
                )
            ];
        }
        TreeFingerprint rawFiles = FingerprintSelectedRoots(
            path,
            ["events", "refs"]
        );
        return new RawSnapshot(
            lineage.CapturedHead,
            addresses,
            kinds,
            rawFiles
        );
    }

    private static void AssertInitialPrefixPreserved(
        RawSnapshot initial,
        RawSnapshot current
    ) {
        Assert.True(
            current.Addresses.Count >= initial.Addresses.Count
        );
        Assert.Equal(
            initial.Addresses,
            current.Addresses
                .Take(initial.Addresses.Count)
                .ToArray()
        );
        Assert.Equal(
            initial.Kinds,
            current.Kinds
                .Take(initial.Kinds.Count)
                .ToArray()
        );
    }

    private static void AssertRawUnchanged(
        RawSnapshot expected,
        RawSnapshot actual
    ) {
        Assert.Equal(expected.Head, actual.Head);
        Assert.Equal(expected.Addresses, actual.Addresses);
        Assert.Equal(expected.Kinds, actual.Kinds);
        Assert.Equal(expected.Files, actual.Files);
    }

    private static string[] RecapExecutionArgs(
        string operation,
        string input,
        string branch,
        string connections,
        string calls,
        string report,
        string? anchor = null,
        string? expectedRawHead = null
    ) => [
        "recap", operation,
        "--input", input,
        "--branch", branch,
        "--connections", connections,
        "--call-log-dir", calls,
        "--report-json", report,
        .. anchor is null
            ? []
            : new[] { "--anchor", anchor },
        .. expectedRawHead is null
            ? []
            : new[] {
                "--expected-raw-head",
                expectedRawHead
            }
    ];

    private static string WriteConnections(string tempRoot) {
        string path = Path.Combine(tempRoot, "connections.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new CompletionConnectionsFileConfig(
                    [Connection()],
                    "scripted"
                )
            )
        );
        return path;
    }

    private static CompletionConnectionConfig Connection() => new(
        "scripted",
        "scripted",
        "model-a",
        "surface-a",
        "http://localhost/"
    );

    private static string ReadString(
        string reportPath,
        string property
    ) {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(reportPath)
        );
        return document.RootElement.GetProperty(property).GetString()
            ?? throw new InvalidDataException(
                $"Report property '{property}' is null."
            );
    }

    private static LoadSelectionReport ReadLoadSelection(
        string reportPath
    ) {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(reportPath)
        );
        JsonElement planning =
            document.RootElement.GetProperty("planning");
        Assert.Equal(
            "ExactSchedule",
            planning.GetProperty("measurementKind").GetString()
        );
        return new LoadSelectionReport(
            planning.GetProperty("historyUnitLoadEstimatorId")
                .GetString()
                ?? throw new InvalidDataException(
                    "Planning estimator ID is null."
                ),
            planning.GetProperty("growthHistoryLoad").GetInt64(),
            planning.GetProperty("selectedAbsorbedHistoryLoad")
                .GetInt64(),
            planning.GetProperty("selectedRecentHistoryLoad")
                .GetInt64()
        );
    }

    private static void RejectReparsePoint(string path) {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint)
            != 0) {
            throw new InvalidDataException(
                $"Acceptance source contains a symlink or reparse "
                + $"point: {path}"
            );
        }
    }

    private static void EnsureDisjoint(
        string first,
        string second
    ) {
        string left = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(first)
        );
        string right = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(second)
        );
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(left, right, comparison)
            || IsAncestor(left, right, comparison)
            || IsAncestor(right, left, comparison)) {
            throw new ArgumentException(
                $"Acceptance paths must be disjoint: '{left}' and "
                + $"'{right}'."
            );
        }
    }

    private static bool IsAncestor(
        string ancestor,
        string descendant,
        StringComparison comparison
    ) => descendant.StartsWith(
        ancestor + Path.DirectorySeparatorChar,
        comparison
    );

    private static TreeFingerprint FingerprintSelectedRoots(
        string root,
        IReadOnlyList<string> relativeRoots
    ) {
        string[] files = [
            .. relativeRoots.SelectMany(relative => {
                string selected = Path.Combine(root, relative);
                return Directory.Exists(selected)
                    ? Directory.EnumerateFiles(
                        selected,
                        "*",
                        SearchOption.AllDirectories
                    )
                    : [];
            })
        ];
        return FingerprintFiles(root, files);
    }

    private static TreeFingerprint FingerprintTree(
        string root,
        bool allowMissing = false
    ) {
        if (!Directory.Exists(root)) {
            if (allowMissing) {
                return new TreeFingerprint(
                    0,
                    0,
                    Sha256([])
                );
            }
            throw new DirectoryNotFoundException(root);
        }
        return FingerprintFiles(
            root,
            Directory.EnumerateFiles(
                root,
                "*",
                SearchOption.AllDirectories
            )
        );
    }

    private static TreeFingerprint FingerprintSourceFile(
        string path
    ) {
        RejectReparsePoint(path);
        var info = new FileInfo(path);
        return new TreeFingerprint(
            1,
            info.Length,
            HashFile(path)
        );
    }

    private static TreeFingerprint FingerprintFiles(
        string root,
        IEnumerable<string> files
    ) {
        using IncrementalHash aggregate =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalBytes = 0;
        int count = 0;
        foreach (string file in files.Order(
                     StringComparer.Ordinal
                 )) {
            RejectReparsePoint(file);
            byte[] pathBytes = Encoding.UTF8.GetBytes(
                Path.GetRelativePath(root, file)
                    .Replace(Path.DirectorySeparatorChar, '/')
            );
            aggregate.AppendData(pathBytes);
            aggregate.AppendData([0]);
            byte[] bytes = File.ReadAllBytes(file);
            totalBytes += bytes.LongLength;
            aggregate.AppendData(
                BitConverter.GetBytes(bytes.LongLength)
            );
            aggregate.AppendData(SHA256.HashData(bytes));
            count++;
        }
        return new TreeFingerprint(
            count,
            totalBytes,
            Convert.ToHexStringLower(
                aggregate.GetHashAndReset()
            )
        );
    }

    private static string HashFile(string path)
        => Sha256(File.ReadAllBytes(path));

    private static string HashAddresses(
        IEnumerable<EventAddress> addresses
    ) => Sha256(Encoding.UTF8.GetBytes(string.Join(
        "\n",
        addresses.Select(SJ.EventAddressTextCodec.Format)
    )));

    private static string CanonicalRequestSha256(
        CompletionRequest request
    ) => Sha256(
        SJ.SessionRequestCanonicalizer.Canonicalize(request)
    );

    private static string Sha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void WriteJsonAtomically(
        string path,
        AcceptanceReport report
    ) {
        string fullPath = Path.GetFullPath(path);
        string directory =
            Path.GetDirectoryName(fullPath) ?? ".";
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}."
            + $"{Guid.NewGuid():N}.tmp"
        );
        try {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions {
                        WriteIndented = true,
                        PropertyNamingPolicy =
                            JsonNamingPolicy.CamelCase
                    }
                )
            );
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally {
            if (File.Exists(temporaryPath)) {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void AssertContentFreeReport(string report) {
        foreach (string forbidden in new[] {
                     "\"content\"",
                     "\"prompt\"",
                     "\"response\"",
                     "\"secret\"",
                     "acceptance recap",
                     "acceptance agent answer",
                     "acceptance prepared recovery"
                 }) {
            Assert.DoesNotContain(
                forbidden,
                report,
                StringComparison.OrdinalIgnoreCase
            );
        }
    }

    private sealed record TreeFingerprint(
        int FileCount,
        long TotalBytes,
        string Sha256
    );

    private sealed record RawSnapshot(
        EventAddress Head,
        IReadOnlyList<EventAddress> Addresses,
        IReadOnlyList<SJ.SessionEventKind> Kinds,
        TreeFingerprint Files
    );

    private sealed record PreparedSnapshot(
        EventAddress Address,
        string CanonicalRequestSha256
    );

    private sealed record AcceptanceReport(
        string Schema,
        string SourceKind,
        string Preparation,
        SourceReport Source,
        ConfigReport Config,
        LoadSelectionReport LoadSelection,
        string BranchRefId,
        string AdmissionAnchor,
        FrozenPlanReport FrozenPlan,
        CallCountReport Calls,
        CorruptionReport Corruption,
        string PreparedCanonicalRequestSha256,
        PrefixReport FinalPrefix,
        RawFingerprintReport InitialRaw,
        LegacyV1Report LegacyV1,
        bool RawUnchangedThroughRestore,
        bool SourceUnchanged,
        bool RecapV4AbsentAfterPreparedRecovery
    );

    private sealed record SourceReport(
        int FileCount,
        long TotalBytes,
        string Sha256
    );

    private sealed record LoadSelectionReport(
        string HistoryUnitLoadEstimatorId,
        long GrowthHistoryLoad,
        long SelectedAbsorbedHistoryLoad,
        long SelectedRecentHistoryLoad
    );

    private sealed record ConfigReport(
        string HistoryUnitLoadEstimatorId,
        long MinimumRecentHistoryLoad,
        long RecapBuildIntervalHistoryLoad,
        int MaxRawGrowthEventCount,
        int MaxRouteEndpointsPerBlock,
        int MaxMaintainerCallsPerBuild,
        int MaxRawEventsPerStep,
        int MaxRawEventsPerBuild,
        IReadOnlyList<string> BlockIds
    ) {
        public static ConfigReport From(
            ResolvedRecapPlannerComposition composition
        ) => new(
                composition.PlanningInputs.Cadence
                    .HistoryUnitLoadEstimatorId,
                composition.PlanningInputs.Cadence
                    .MinimumRecentHistoryLoad.Value,
                composition.PlanningInputs.Cadence
                    .RecapBuildIntervalHistoryLoad.Value,
                composition.PlanningLimits.MaxRawGrowthEventCount,
                composition.PlanningLimits.MaxRouteEndpointsPerBlock,
                composition.PlanningLimits.MaxMaintainerCallsPerBuild,
                composition.PlanningLimits.MaxRawEventsPerStep,
                composition.PlanningLimits.MaxRawEventsPerBuild,
                [
                    .. composition.PlanningInputs.OrderedCatalog
                        .Select(
                        static item =>
                            item.RecapBlockId.Value
                    )
                ]
            );
    }

    private sealed record CallCountReport(
        int FailedRun,
        int Resume,
        int Restore,
        int OnlineTurn,
        int PreparedRecovery,
        string FailedSuffixRequestSha256,
        bool ResumeMatchedFailedSuffix
    );

    private sealed record FrozenPlanReport(
        int MaintainedBlockCount,
        int RouteEndpointCount
    );

    private sealed record CorruptionReport(
        string BlockId,
        string Mutation,
        string SelectionResult,
        string PreRestoreMaterializationResult,
        string RestoreResult,
        string PostRestoreMaterializationResult,
        bool DamagedBlockRestored,
        bool OtherBlockUnchanged
    );

    private sealed record PrefixReport(
        int InitialAddressCount,
        int FinalAddressCount,
        string InitialAddressSha256,
        string FinalPrefixSha256,
        bool Preserved,
        IReadOnlyList<string> AppendedEventKinds
    );

    private sealed record RawFingerprintReport(
        int FileCount,
        long TotalBytes,
        string FullTreeSha256,
        string ChronologicalAddressSha256
    );

    private sealed record LegacyV1Report(
        int FileCount,
        long TotalBytes,
        string Sha256,
        bool Unchanged
    );

    private static void AssertRealCompletedTurnProjection(
        string sourcePath,
        string importedRepositoryPath
    ) {
        LegacyChatSessionExport export =
            LegacyChatSessionExportReader.Read(sourcePath);
        var expected = new List<ExpectedLegacyTurn>();
        string? pendingObservation = null;
        foreach (LegacyChatSessionEvent replayEvent in export.Events) {
            IReadOnlyList<LegacyChatSessionMessage> messages =
                replayEvent.Kind switch {
                    LegacyChatSessionEventKinds.InitialState =>
                        replayEvent.Messages ?? [],
                    LegacyChatSessionEventKinds.ModelTurn =>
                        replayEvent.AppendedMessages ?? [],
                    _ => []
                };
            foreach (LegacyChatSessionMessage message in messages) {
                if (string.Equals(
                        message.Kind,
                        "observation",
                        StringComparison.Ordinal
                    )) {
                    Assert.Null(pendingObservation);
                    pendingObservation = message.Content ?? string.Empty;
                }
                else if (string.Equals(
                             message.Kind,
                             "action",
                             StringComparison.Ordinal
                         )) {
                    Assert.NotNull(pendingObservation);
                    ActionMessage action = new(
                        ActionMessageSerialization.FromSerializedBlocks(
                            message.Action?.Blocks ?? []
                        )
                    );
                    expected.Add(new ExpectedLegacyTurn(
                        pendingObservation!,
                        action
                    ));
                    pendingObservation = null;
                }
            }
        }
        Assert.Null(pendingObservation);

        using var engine = SJ.SessionJournalEngine.OpenReadOnly(
            importedRepositoryPath
        );
        SJ.SessionCompletedTurnsSnapshot snapshot =
            engine.ReadRecentCompletedTurns(int.MaxValue);
        Assert.Equal(71, expected.Count);
        Assert.Equal(expected.Count, snapshot.Turns.Count);
        for (int index = 0; index < expected.Count; index++) {
            ExpectedLegacyTurn source =
                expected[expected.Count - 1 - index];
            SJ.SessionCompletedTurnProjection projected =
                snapshot.Turns[index];
            Assert.Equal(
                source.ObservationContent,
                projected.ObservationContent
            );
            Assert.Equal(
                source.TerminalAction.Blocks,
                projected.TerminalAction.Message.Blocks
            );
        }
    }

    private sealed record ExpectedLegacyTurn(
        string ObservationContent,
        ActionMessage TerminalAction
    );

    private sealed class ThrowingCompletionClientFactory
        : ICompletionClientFactory {
        public static ThrowingCompletionClientFactory Instance { get; } =
            new();

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => throw new InvalidOperationException(
            $"Completion client '{connection.Id}' must not be created."
        );
    }

    private sealed class ScriptedCompletionClientFactory(
        string responseText,
        int? failAtCall = null
    ) : ICompletionClientFactory {
        private readonly ScriptedCompletionClient _client =
            new(responseText, failAtCall);

        public int CallCount => _client.CallCount;
        public IReadOnlyList<CompletionRequest> Requests =>
            _client.Requests;

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => _client;
    }

    private sealed class ScriptedCompletionClient(
        string responseText,
        int? failAtCall = null
    ) : ICompletionClient {
        private readonly ConcurrentQueue<CompletionRequest> _requests =
            new();
        private int _callCount;

        public string Name => "scripted";
        public string ApiSpecId => "test-api-v1";
        public int CallCount => Volatile.Read(ref _callCount);
        public IReadOnlyList<CompletionRequest> Requests =>
            _requests.ToArray();

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Enqueue(request);
            int call = Interlocked.Increment(ref _callCount);
            if (call == failAtCall) {
                throw new HttpRequestException(
                    "scripted acceptance interruption"
                );
            }
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text(responseText)
                ]),
                CompletionDescriptor.From(this, request)
            ));
        }
    }
}
