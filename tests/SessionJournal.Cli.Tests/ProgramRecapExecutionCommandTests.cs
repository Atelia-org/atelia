using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramRecapExecutionCommandTests : IDisposable {
    // Test-only cadence: the compact 22-pair fixture measures 404 load,
    // so 180 + 200 preserves a fast build/no-build boundary without
    // weakening the independently asserted production 18k/21k values.
    private static RecapPlannerConfigDocument FastCadenceConfig =>
        RecapCliComposition.DefaultComposition.Snapshot.Document with {
            Cadence = new RecapCadenceConfigDocument(
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                MinimumRecentHistoryLoad: 180,
                RecapBuildIntervalHistoryLoad: 200
            )
        };

    private static string FastCadenceConfigSha256 =>
        RecapPlannerConfigSnapshot.FromDocument(FastCadenceConfig)
            .ConfigSha256;

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-execution-cli-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        try {
            if (Directory.Exists(_tempRoot)) {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch {
            // Best-effort cleanup for test-owned repositories.
        }
    }

    [Fact]
    public void ProductionCatalogAndLimitsAreExactAndOrdered() {
        ResolvedRecapPlannerComposition composition =
            RecapCliComposition.DefaultComposition;
        RecapPlanningInputs inputs = composition.PlanningInputs;
        RecapPlanningLimits limits = composition.PlanningLimits;
        Assert.Equal(
            RecapPlannerConfigCodec.SchemaV2,
            composition.Snapshot.Document.Schema
        );
        Assert.Equal(
            RecapPlanningPolicyIds.BoundedMaintainAllV1,
            composition.Snapshot.Document.PlanningPolicy
        );
        Assert.Equal(
            RecapPlannerConfigCodec.ComputeSha256(
                composition.Snapshot.CanonicalBytes.AsSpan()
            ),
            composition.Snapshot.ConfigSha256
        );
        Assert.Collection(
            inputs.OrderedCatalog,
            world => {
                Assert.Equal(
                    RolePlayRecapBlockPaths
                        .WorldUnderstandingBlockKey,
                    world.RecapBlockId.Value
                );
                Assert.Equal(
                    RolePlayRecapBlockPaths.WorldUnderstanding,
                    world.Target
                );
                Assert.Equal(
                    WorldUnderstandingRewriteProfiles.MaintainerId,
                    world.MaintainerId
                );
                Assert.Equal(
                    RecapMaintainerProfileCatalog.BuiltIn.Resolve(
                        RecapMaintainerProfileCatalog
                            .WorldUnderstandingRewrite
                    ).CapabilityFingerprint,
                    world.MaintainerCapabilityFingerprint
                );
                Assert.Equal(32_768, world.MaxContentUtf8Bytes);
            },
            autobiography => {
                Assert.Equal(
                    RolePlayRecapBlockPaths
                        .FirstPersonAutobiographyBlockKey,
                    autobiography.RecapBlockId.Value
                );
                Assert.Equal(
                    RolePlayRecapBlockPaths
                        .FirstPersonAutobiography,
                    autobiography.Target
                );
                Assert.Equal(
                    AutobiographicalRewriteProfiles.MaintainerId,
                    autobiography.MaintainerId
                );
                Assert.Equal(
                    RecapMaintainerProfileCatalog.BuiltIn.Resolve(
                        RecapMaintainerProfileCatalog
                            .AutobiographicalRewrite
                    ).CapabilityFingerprint,
                    autobiography.MaintainerCapabilityFingerprint
                );
                Assert.Equal(
                    32_768,
                    autobiography.MaxContentUtf8Bytes
                );
            }
        );
        Assert.Equal(
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            inputs.Cadence.HistoryUnitLoadEstimatorId
        );
        Assert.Equal(
            composition.Snapshot.Document.Cadence
                .MinimumRecentHistoryLoad,
            inputs.Cadence.MinimumRecentHistoryLoad.Value
        );
        Assert.Equal(
            composition.Snapshot.Document.Cadence
                .RecapBuildIntervalHistoryLoad,
            inputs.Cadence.RecapBuildIntervalHistoryLoad.Value
        );
        Assert.Equal(18_000, inputs.Cadence.MinimumRecentHistoryLoad.Value);
        Assert.Equal(
            21_000,
            inputs.Cadence.RecapBuildIntervalHistoryLoad.Value
        );
        Assert.Equal(512, limits.MaxRawGrowthEventCount);
        Assert.Equal(4, limits.MaxRouteEndpointsPerBlock);
        Assert.Equal(8, limits.MaxMaintainerCallsPerBuild);
        Assert.Equal(64, limits.MaxRawEventsPerStep);
        Assert.Equal(512, limits.MaxRawEventsPerBuild);
        Assert.Equal(
            inputs.OrderedCatalog,
            composition.ActiveProfiles.Select(
                static profile => profile.CatalogEntry
            )
        );
    }

    [Fact]
    public async Task PartialFailureRunFastPathIgnoresMissingConfigThenRestores()
    {
        Fixture fixture = await CreateFixtureAsync("partial", 77);
        Assert.Equal(157, fixture.EventCount);
        await CreateStoreAsync(fixture);
        RawSnapshot raw = ReadRawSnapshot(fixture.Path);
        string firstReport = Path.Combine(_tempRoot, "run.json");
        var firstFactory = new ScriptedCompletionClientFactory(
            "SECRET-RECAP-RESULT",
            failAtCall: 5,
            failureMessage: "SECRET-PROVIDER-FAILURE"
        );

        Assert.Equal(2, Run(
            ExecuteArgs(fixture, "run", firstReport, "run-calls"),
            firstFactory
        ));
        Assert.Equal(1, firstFactory.CreateCallCount);
        Assert.Equal(5, firstFactory.CallCount);
        using JsonDocument first = ReadJson(firstReport);
        Assert.Equal(
            "atelia.session-journal.derived-recap-execution.v5",
            String(first, "schema")
        );
        JsonElement reportedConfig =
            first.RootElement.GetProperty("config");
        Assert.Equal(
            RecapPlannerConfigCodec.SchemaV2,
            reportedConfig.GetProperty("schema").GetString()
        );
        Assert.Equal(
            FastCadenceConfigSha256,
            reportedConfig
                .GetProperty("configSha256")
                .GetString()
        );
        Assert.Equal(
            RecapPlannerConfigLoader.GetCanonicalPath(fixture.Path),
            reportedConfig.GetProperty("path").GetString()
        );
        Assert.Equal(
            "ExactSchedule",
            first.RootElement.GetProperty("planning")
                .GetProperty("measurementKind").GetString()
        );
        JsonElement planning =
            first.RootElement.GetProperty("planning");
        Assert.Equal(
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            planning.GetProperty("historyUnitLoadEstimatorId")
                .GetString()
        );
        Assert.True(
            planning.GetProperty("growthHistoryLoad")
                .GetInt64() > 0
        );
        Assert.True(
            planning.GetProperty("selectedAbsorbedHistoryLoad")
                .GetInt64() > 0
        );
        Assert.True(
            planning.GetProperty("selectedRecentHistoryLoad")
                .GetInt64() >= 0
        );
        Assert.Equal("BlockFailed", String(first, "resultStatus"));
        Assert.Equal(
            RolePlayRecapBlockPaths
                .FirstPersonAutobiographyBlockKey,
            String(first, "blockId")
        );
        string anchor = String(first, "anchor");
        AssertReportIsContentFree(
            File.ReadAllText(firstReport)
        );

        string resumeReport =
            Path.Combine(_tempRoot, "resume.json");
        Directory.Delete(
            Path.Combine(fixture.Path, "config"),
            recursive: true
        );
        var resumeFactory = new ScriptedCompletionClientFactory(
            "SECRET-RECAP-RESULT"
        );
        Assert.Equal(0, Run(
            ExecuteArgs(
                fixture,
                "run",
                resumeReport,
                "resume-calls"
            ),
            resumeFactory
        ));
        using (JsonDocument resume = ReadJson(resumeReport)) {
            Assert.Equal(
                "Published",
                String(resume, "resultStatus")
            );
            Assert.Equal(
                JsonValueKind.Null,
                resume.RootElement
                    .GetProperty("config")
                    .ValueKind
            );
            Assert.Equal(
                JsonValueKind.Null,
                resume.RootElement
                    .GetProperty("planning")
                    .ValueKind
            );
        }
        Assert.Equal(2, resumeFactory.CallCount);
        AssertReportIsContentFree(
            File.ReadAllText(resumeReport)
        );

        string fencedReport =
            Path.Combine(_tempRoot, "restore-fenced.json");
        var fencedFactory = new ScriptedCompletionClientFactory(
            "unused"
        );
        Assert.Equal(3, Run(
            ExecuteArgs(
                fixture,
                "restore",
                fencedReport,
                "restore-fenced-calls",
                anchor,
                SJ.EventAddressTextCodec.Format(
                    fixture.PreviousAddress
                )
            ),
            fencedFactory
        ));
        Assert.Equal(0, fencedFactory.CreateCallCount);
        Assert.Equal(
            "RawHeadChanged",
            String(ReadJson(fencedReport), "code")
        );
        Assert.False(Directory.Exists(Path.Combine(
            _tempRoot,
            "restore-fenced-calls"
        )));

        EventAddress publishedAnchor =
            SJ.EventAddressTextCodec.Parse(anchor);
        string publishedBlock =
            FindPublishedBlock(
                fixture,
                RolePlayRecapBlockPaths
                    .WorldUnderstandingBlockKey
            );
        await File.WriteAllTextAsync(
            publishedBlock,
            "SECRET-CORRUPTION"
        );
        string restoreReport =
            Path.Combine(_tempRoot, "restore.json");
        var restoreFactory = new ScriptedCompletionClientFactory(
            "SECRET-RESTORED-RESULT"
        );
        Assert.Equal(0, Run(
            ExecuteArgs(
                fixture,
                "restore",
                restoreReport,
                "restore-calls",
                anchor,
                SJ.EventAddressTextCodec.Format(fixture.Head)
            ),
            restoreFactory
        ));
        using (JsonDocument restore = ReadJson(restoreReport)) {
            Assert.Equal(
                "Restored",
                String(restore, "resultStatus")
            );
            Assert.Equal(anchor, String(restore, "anchor"));
            Assert.Equal(
                JsonValueKind.Null,
                restore.RootElement
                    .GetProperty("config")
                    .ValueKind
            );
        }
        AssertReportIsContentFree(
            File.ReadAllText(restoreReport)
        );
        using (var engine = SJ.SessionJournalEngine.OpenReadOnly(
                   fixture.Path,
                   fixture.BranchName
               )) {
            DerivedRecapStore store = DerivedRecapStore.Open(
                fixture.Path,
                engine.BranchRefId
            );
            PublishedRestoreInspectionResult.Available inspection =
                Assert.IsType<
                    PublishedRestoreInspectionResult.Available
                >(
                    await DerivedRecapLineageView
                        .Capture(store, engine)
                        .InspectPublishedForRestoreAsync(
                            publishedAnchor
                        )
                );
            Assert.All(
                inspection.Inspection.Blocks.Values,
                block => Assert.IsType<
                    FinalRecapBlockHealth.Healthy
                >(block.Final)
            );
        }
        Assert.Equal(raw, ReadRawSnapshot(fixture.Path));
    }

    [Fact]
    public async Task BoundedPreflightIsReportedBeforeClientOrBuilding() {
        Fixture fixture =
            await CreateFixtureAsync("raw-safety-rejected", 257);
        await CreateStoreAsync(fixture);
        string reportPath =
            Path.Combine(_tempRoot, "raw-safety-rejected.json");
        string calls =
            Path.Combine(_tempRoot, "raw-safety-rejected-calls");
        var factory =
            new ScriptedCompletionClientFactory("must not run");

        Assert.Equal(2, Run(
            ExecuteArgs(
                fixture,
                "run",
                reportPath,
                "raw-safety-rejected-calls"
            ),
            factory
        ));

        Assert.Equal(0, factory.CreateCallCount);
        Assert.False(Directory.Exists(calls));
        using JsonDocument report = ReadJson(reportPath);
        Assert.Equal(
            "atelia.session-journal.derived-recap-execution.v5",
            String(report, "schema")
        );
        Assert.Equal("BeyondPrefix", String(report, "resultStatus"));
        Assert.Contains(
            "BeyondPrefix",
            report.RootElement.GetProperty("defectCodes")
                .EnumerateArray()
                .Select(static item => item.GetString())
        );
        Assert.Equal(
            JsonValueKind.Null,
            report.RootElement.GetProperty("planning").ValueKind
        );
        JsonElement beyondPrefix =
            report.RootElement.GetProperty("beyondPrefix");
        Assert.Equal(
            513,
            beyondPrefix.GetProperty("headerCount").GetInt32()
        );
        Assert.False(string.IsNullOrWhiteSpace(
            beyondPrefix.GetProperty("requiredAnchor").GetString()
        ));
        Assert.False(string.IsNullOrWhiteSpace(
            beyondPrefix.GetProperty("capturedHead").GetString()
        ));
        Assert.False(string.IsNullOrWhiteSpace(
            beyondPrefix.GetProperty("nextAddress").GetString()
        ));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(
                fixture.Path,
                "derived",
                "recap",
                "v4",
                "refs",
                fixture.BranchRefId,
                "building"
            )
        ));
    }

    [Fact]
    public async Task RestoreBeyondPrefixUsesV5GoldenWithoutMutation() {
        Fixture fixture =
            await CreateFixtureAsync("restore-beyond-prefix", 77);
        await CreateStoreAsync(fixture);
        string publishReport =
            Path.Combine(_tempRoot, "restore-beyond-publish.json");
        var publishFactory = new ScriptedCompletionClientFactory(
            "publish recap"
        );
        Assert.Equal(0, Run(
            ExecuteArgs(
                fixture,
                "run",
                publishReport,
                "restore-beyond-publish-calls"
            ),
            publishFactory
        ));
        using JsonDocument published = ReadJson(publishReport);
        string anchor = String(published, "anchor");
        EventAddress currentHead;
        using (var engine = SJ.SessionJournalEngine.Open(fixture.Path)) {
            for (int index = 0; index < 257; index++) {
                engine.AppendObservation($"tail observation {index}");
                _ = engine.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"tail action {index}")
                    ]),
                    new CompletionDescriptor(
                        "import",
                        "v1",
                        "model-a"
                    )
                );
            }
            currentHead = engine.ReadCurrentHead()!.Value;
        }
        string beforeDerived = HashDerivedFiles(fixture.Path);
        string reportPath =
            Path.Combine(_tempRoot, "restore-beyond.json");
        string callLogName = "restore-beyond-calls";
        var restoreFactory = new ScriptedCompletionClientFactory(
            "must not run"
        );

        Assert.Equal(2, Run(
            ExecuteArgs(
                fixture,
                "restore",
                reportPath,
                callLogName,
                anchor,
                SJ.EventAddressTextCodec.Format(currentHead)
            ),
            restoreFactory
        ));

        Assert.Equal(0, restoreFactory.CreateCallCount);
        Assert.Equal(0, restoreFactory.CallCount);
        Assert.False(Directory.Exists(
            Path.Combine(_tempRoot, callLogName)
        ));
        Assert.Equal(beforeDerived, HashDerivedFiles(fixture.Path));
        using JsonDocument report = ReadJson(reportPath);
        Assert.Equal(
            "atelia.session-journal.derived-recap-execution.v5",
            String(report, "schema")
        );
        Assert.Equal("restore", String(report, "operation"));
        Assert.Equal("BeyondPrefix", String(report, "resultStatus"));
        Assert.Equal(anchor, String(report, "anchor"));
        JsonElement beyond =
            report.RootElement.GetProperty("beyondPrefix");
        Assert.Equal(
            anchor,
            beyond.GetProperty("requiredAnchor").GetString()
        );
        Assert.Equal(513, beyond.GetProperty("headerCount").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(
            beyond.GetProperty("capturedHead").GetString()
        ));
        Assert.False(string.IsNullOrWhiteSpace(
            beyond.GetProperty("nextAddress").GetString()
        ));
        AssertReportIsContentFree(File.ReadAllText(reportPath));
    }

    [Fact]
    public async Task TwoPairFixtureStaysBelowFastLoadThreshold() {
        Fixture fixture =
            await CreateFixtureAsync("below-fast-load-threshold", 2);
        await CreateStoreAsync(fixture);
        string reportPath =
            Path.Combine(_tempRoot, "below-fast-load-threshold.json");
        string calls =
            Path.Combine(_tempRoot, "below-fast-load-threshold-calls");
        var factory =
            new ScriptedCompletionClientFactory("must not run");

        Assert.Equal(0, Run(
            ExecuteArgs(
                fixture,
                "run",
                reportPath,
                "below-fast-load-threshold-calls"
            ),
            factory
        ));

        Assert.Equal(0, factory.CreateCallCount);
        Assert.False(Directory.Exists(calls));
        using JsonDocument report = ReadJson(reportPath);
        Assert.Equal("NoBuild", String(report, "resultStatus"));
        Assert.Equal(
            0,
            report.RootElement.GetProperty("callLogCount").GetInt32()
        );
        JsonElement planning =
            report.RootElement.GetProperty("planning");
        Assert.Equal(
            "ExactSchedule",
            planning.GetProperty("measurementKind").GetString()
        );
        Assert.True(
            planning.GetProperty("growthHistoryLoad").GetInt64()
                < 380
        );
        Assert.Equal(
            JsonValueKind.Null,
            planning.GetProperty("selectedAbsorbedHistoryLoad")
                .ValueKind
        );
        Assert.Equal(
            JsonValueKind.Null,
            planning.GetProperty("selectedRecentHistoryLoad")
                .ValueKind
        );
    }

    [Fact]
    public async Task OversizeUtf8FailsWithoutCheckpointOrFinal() {
        Fixture fixture = await CreateFixtureAsync("oversize", 78);
        await CreateStoreAsync(fixture);
        RawSnapshot raw = ReadRawSnapshot(fixture.Path);
        string reportPath = Path.Combine(_tempRoot, "oversize.json");
        var factory = new ScriptedCompletionClientFactory(
            new string('x', 32_769)
        );

        Assert.Equal(2, Run(
            ExecuteArgs(
                fixture,
                "run",
                reportPath,
                "oversize-calls"
            ),
            factory
        ));
        using JsonDocument report = ReadJson(reportPath);
        Assert.Equal("BlockFailed", String(report, "resultStatus"));
        EventAddress anchor = SJ.EventAddressTextCodec.Parse(
            String(report, "anchor")
        );
        using var engine = SJ.SessionJournalEngine.OpenReadOnly(
            fixture.Path,
            fixture.BranchName
        );
        DerivedRecapStore store = DerivedRecapStore.Open(
            fixture.Path,
            engine.BranchRefId
        );
        BuildingReadResult.Available building =
            Assert.IsType<BuildingReadResult.Available>(
                await store.ReadBuildingAsync(anchor)
            );
        BuildingBlockInspection block =
            await store.InspectBuildingBlockAsync(
                building.Snapshot.Descriptor,
                new RecapBlockId(
                    RolePlayRecapBlockPaths
                        .WorldUnderstandingBlockKey
                )
            );
        Assert.IsType<FinalRecapBlockHealth.Missing>(block.Final);
        Assert.IsType<RollingRecapCheckpointHealth.Missing>(
            block.Checkpoint
        );
        AssertReportIsContentFree(File.ReadAllText(reportPath));
        Assert.Equal(raw, ReadRawSnapshot(fixture.Path));
    }

    [Fact]
    public async Task StoreAbsentNeverCreatesClientLogOrExactRefRoot() {
        Fixture fixture = await CreateFixtureAsync("absent", 2);
        RawSnapshot raw = ReadRawSnapshot(fixture.Path);
        var factory = new ScriptedCompletionClientFactory("unused");
        string anchor =
            SJ.EventAddressTextCodec.Format(fixture.Head);
        foreach (string operation in new[] {
                     "run",
                     "resume",
                     "restore"
                 }) {
            string report = Path.Combine(
                _tempRoot,
                $"{operation}-absent.json"
            );
            string calls = $"{operation}-absent-calls";
            Assert.Equal(2, Run(
                ExecuteArgs(
                    fixture,
                    operation,
                    report,
                    calls,
                    operation == "run" ? null : anchor,
                    operation == "restore" ? anchor : null
                ),
                factory
            ));
            using JsonDocument result = ReadJson(report);
            Assert.Equal(
                "Unavailable",
                String(result, "resultStatus")
            );
            Assert.Equal(
                0,
                result.RootElement
                    .GetProperty("callLogCount")
                    .GetInt32()
            );
            Assert.False(Directory.Exists(
                Path.Combine(_tempRoot, calls)
            ));
        }
        Assert.Equal(0, factory.CreateCallCount);
        Assert.False(Directory.Exists(Path.Combine(
            fixture.Path,
            "derived",
            "recap",
            "v4",
            "refs",
            fixture.BranchRefId
        )));
        Assert.Equal(raw, ReadRawSnapshot(fixture.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrInvalidConfigBlocksBeforeClientOrBuilding(
        bool invalid
    ) {
        Fixture fixture = await CreateFixtureAsync(
            invalid ? "invalid-config" : "missing-config",
            77
        );
        await CreateStoreAsync(fixture);
        string configPath =
            RecapPlannerConfigLoader.GetCanonicalPath(fixture.Path);
        if (invalid) {
            File.WriteAllText(configPath, "{\"schema\":");
        }
        else {
            File.Delete(configPath);
        }
        RawSnapshot raw = ReadRawSnapshot(fixture.Path);
        string derivedBefore = HashDerivedFiles(fixture.Path);
        string reportPath = Path.Combine(
            _tempRoot,
            invalid ? "invalid-config.json" : "missing-config.json"
        );
        string calls = Path.Combine(
            _tempRoot,
            invalid ? "invalid-config-calls" : "missing-config-calls"
        );
        var factory = new ScriptedCompletionClientFactory("unused");

        Assert.Equal(2, Run(
            ExecuteArgs(
                fixture,
                "run",
                reportPath,
                Path.GetFileName(calls)
            ),
            factory
        ));

        using JsonDocument report = ReadJson(reportPath);
        Assert.Equal("Unavailable", String(report, "resultStatus"));
        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, factory.CallCount);
        Assert.False(Directory.Exists(calls));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(
                fixture.Path,
                "derived",
                "recap",
                "v4",
                "refs",
                fixture.BranchRefId,
                "building"
            )
        ));
        Assert.Equal(derivedBefore, HashDerivedFiles(fixture.Path));
        Assert.Equal(raw, ReadRawSnapshot(fixture.Path));
    }

    [Fact]
    public async Task CatalogShapeMismatchBlocksBeforeClientOrBuilding() {
        Fixture fixture =
            await CreateFixtureAsync("catalog-mismatch", 77);
        await CreateStoreAsync(fixture);
        var firstFactory =
            new ScriptedCompletionClientFactory("initial recap");
        Assert.Equal(0, Run(
            ExecuteArgs(
                fixture,
                "run",
                Path.Combine(_tempRoot, "catalog-first.json"),
                "catalog-first-calls"
            ),
            firstFactory
        ));

        RecapPlannerConfigDocument source = FastCadenceConfig;
        File.WriteAllBytes(
            RecapPlannerConfigLoader.GetCanonicalPath(fixture.Path),
            RecapPlannerConfigCodec.EncodeCanonical(
                source with {
                    Catalog = Array.AsReadOnly([
                        source.Catalog[1],
                        source.Catalog[0]
                    ])
                }
            )
        );
        RawSnapshot raw = ReadRawSnapshot(fixture.Path);
        string derivedBefore = HashDerivedFiles(fixture.Path);
        string reportPath =
            Path.Combine(_tempRoot, "catalog-mismatch.json");
        string calls =
            Path.Combine(_tempRoot, "catalog-mismatch-calls");
        var blockedFactory =
            new ScriptedCompletionClientFactory("must not run");

        Assert.Equal(2, Run(
            ExecuteArgs(
                fixture,
                "run",
                reportPath,
                "catalog-mismatch-calls"
            ),
            blockedFactory
        ));

        using JsonDocument report = ReadJson(reportPath);
        Assert.Contains(
            DerivedRecapExecutionDefectCodes
                .CatalogMigrationRequired,
            report.RootElement
                .GetProperty("defectCodes")
                .EnumerateArray()
                .Select(static item => item.GetString())
        );
        Assert.NotEqual(
            JsonValueKind.Null,
            report.RootElement.GetProperty("config").ValueKind
        );
        Assert.Equal(0, blockedFactory.CreateCallCount);
        Assert.Equal(0, blockedFactory.CallCount);
        Assert.False(Directory.Exists(calls));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(
                fixture.Path,
                "derived",
                "recap",
                "v4",
                "refs",
                fixture.BranchRefId,
                "building"
            )
        ));
        Assert.Equal(derivedBefore, HashDerivedFiles(fixture.Path));
        Assert.Equal(raw, ReadRawSnapshot(fixture.Path));
    }

    [Fact]
    public async Task ConfigChangeAfterReadinessAffectsOnlyNextOperation() {
        Fixture fixture =
            await CreateFixtureAsync("config-snapshot", 77);
        await CreateStoreAsync(fixture);
        string configPath =
            RecapPlannerConfigLoader.GetCanonicalPath(fixture.Path);
        string expectedHash = FastCadenceConfigSha256;
        var firstFactory = new ScriptedCompletionClientFactory(
            "snapshot recap",
            onCreate: () => File.WriteAllText(
                configPath,
                "{\"schema\":"
            )
        );
        string firstReport =
            Path.Combine(_tempRoot, "config-snapshot-first.json");

        Assert.Equal(0, Run(
            ExecuteArgs(
                fixture,
                "run",
                firstReport,
                "config-snapshot-first-calls"
            ),
            firstFactory
        ));

        using (JsonDocument report = ReadJson(firstReport)) {
            Assert.Equal("Published", String(report, "resultStatus"));
            Assert.Equal(
                expectedHash,
                report.RootElement
                    .GetProperty("config")
                    .GetProperty("configSha256")
                    .GetString()
            );
        }
        Assert.Equal(1, firstFactory.CreateCallCount);

        var nextFactory =
            new ScriptedCompletionClientFactory("must not run");
        string nextCalls =
            Path.Combine(_tempRoot, "config-snapshot-next-calls");
        Assert.Equal(2, Run(
            ExecuteArgs(
                fixture,
                "run",
                Path.Combine(
                    _tempRoot,
                    "config-snapshot-next.json"
                ),
                "config-snapshot-next-calls"
            ),
            nextFactory
        ));
        Assert.Equal(0, nextFactory.CreateCallCount);
        Assert.False(Directory.Exists(nextCalls));
    }

    [Fact]
    public async Task FrozenBuildingCapabilityBlocksBeforeClientOrLog() {
        Fixture fixture =
            await CreateFixtureAsync("missing-frozen-capability", 77);
        await CreateStoreAsync(fixture);
        using (var engine = SJ.SessionJournalEngine.Open(
                   fixture.Path,
                   fixture.BranchName
               )) {
            RecapBlockCatalogEntry entry =
                RecapCliComposition.DefaultComposition
                    .PlanningInputs.OrderedCatalog[0];
            var plan = new MaintainRecapBlockPlan(
                entry.RecapBlockId,
                entry.Target,
                entry.MaintainerId,
                "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
                new EmptyRecapMaintainSource(
                    engine.ReadHistoryPlanningWindow()
                        .StartExclusive
                ),
                [fixture.Head],
                EmptyRecapPriorContext.Instance,
                entry.MaxContentUtf8Bytes
            );
            DerivedRecapStore store = DerivedRecapStore.Open(
                fixture.Path,
                engine.BranchRefId
            );
            Assert.IsType<CreateBuildingResult.Created>(
                await new DerivedRecapBuildingInstaller(store, engine)
                    .InstallAsync(
                        DerivedRecapCodec.CreateManifest(
                            engine.BranchRefId,
                            fixture.Head,
                            [plan]
                        ),
                        fixture.Head
                    )
            );
        }
        RawSnapshot raw = ReadRawSnapshot(fixture.Path);
        string derived = HashDerivedFiles(fixture.Path);
        string reportPath =
            Path.Combine(_tempRoot, "missing-capability.json");
        string calls =
            Path.Combine(_tempRoot, "missing-capability-calls");
        var factory =
            new ScriptedCompletionClientFactory("must not run");

        Assert.Equal(2, Run(
            ExecuteArgs(
                fixture,
                "run",
                reportPath,
                "missing-capability-calls"
            ),
            factory
        ));

        using JsonDocument report = ReadJson(reportPath);
        Assert.Contains(
            DerivedRecapExecutionDefectCodes.MaintainerUnavailable,
            report.RootElement
                .GetProperty("defectCodes")
                .EnumerateArray()
                .Select(static item => item.GetString())
        );
        Assert.Equal(0, factory.CreateCallCount);
        Assert.False(Directory.Exists(calls));
        Assert.Equal(derived, HashDerivedFiles(fixture.Path));
        Assert.Equal(raw, ReadRawSnapshot(fixture.Path));
    }

    [Fact]
    public async Task DamagedLatestPassesCatalogGateBeforePlannerRetry() {
        Fixture fixture =
            await CreateFixtureAsync("damaged-latest", 77);
        await CreateStoreAsync(fixture);
        var publishFactory =
            new ScriptedCompletionClientFactory("initial recap");
        string publishReport =
            Path.Combine(_tempRoot, "damaged-latest-publish.json");
        Assert.Equal(0, Run(
            ExecuteArgs(
                fixture,
                "run",
                publishReport,
                "damaged-latest-publish-calls"
            ),
            publishFactory
        ));
        string anchor = String(ReadJson(publishReport), "anchor");
        string damagedBlock = FindPublishedBlock(
            fixture,
            RolePlayRecapBlockPaths.WorldUnderstandingBlockKey
        );
        await File.WriteAllTextAsync(
            damagedBlock,
            "damaged latest recap"
        );
        using (var readinessEngine =
               SJ.SessionJournalEngine.OpenReadOnly(
                   fixture.Path,
                   fixture.BranchName
               )) {
            RecapOperationReadinessResult readiness =
                await RecapOperationReadiness.PrepareAsync(
                    readinessEngine,
                    DerivedRecapStore.Open(
                        fixture.Path,
                        readinessEngine.BranchRefId
                    )
                );
            Assert.True(
                readiness is RecapOperationReadinessResult.Ready,
                readiness.ToString()
            );
        }
        var repairFactory =
            new ScriptedCompletionClientFactory("must not run");
        string repairReport =
            Path.Combine(_tempRoot, "damaged-latest-repair.json");

        int repairExitCode = Run(
            ExecuteArgs(
                fixture,
                "run",
                repairReport,
                "damaged-latest-repair-calls"
            ),
            repairFactory
        );
        Assert.Equal(3, repairExitCode);

        using JsonDocument report = ReadJson(repairReport);
        Assert.Equal("Retryable", String(report, "resultStatus"));
        Assert.Equal(
            DerivedRecapExecutionDefectCodes.SourceChanged,
            String(report, "code")
        );
        Assert.NotEqual(
            JsonValueKind.Null,
            report.RootElement.GetProperty("config").ValueKind
        );
        Assert.Equal(0, repairFactory.CreateCallCount);
        Assert.Equal(0, repairFactory.CallCount);
        Assert.False(Directory.Exists(Path.Combine(
            _tempRoot,
            "damaged-latest-repair-calls"
        )));
        using var engine = SJ.SessionJournalEngine.OpenReadOnly(
            fixture.Path,
            fixture.BranchName
        );
        DerivedRecapStore store = DerivedRecapStore.Open(
            fixture.Path,
            engine.BranchRefId
        );
        PublishedRestoreInspectionResult.Available inspection =
            Assert.IsType<PublishedRestoreInspectionResult.Available>(
                await DerivedRecapLineageView
                    .Capture(store, engine)
                    .InspectPublishedForRestoreAsync(
                        SJ.EventAddressTextCodec.Parse(anchor)
                    )
            );
        Assert.Contains(
            inspection.Inspection.Blocks.Values,
            block => block.Final is FinalRecapBlockHealth.Damaged
        );
    }

    [Fact]
    public async Task OutputShapePreflightStopsBeforeStoreOrClient() {
        Fixture fixture = await CreateFixtureAsync("path-shape", 77);
        await CreateStoreAsync(fixture);
        string derivedBefore = HashDerivedFiles(fixture.Path);
        var factory = new ScriptedCompletionClientFactory("unused");

        string reportDirectory =
            Path.Combine(_tempRoot, "execution-report-directory");
        Directory.CreateDirectory(reportDirectory);
        AssertRunRejected(
            fixture,
            reportDirectory,
            Path.Combine(_tempRoot, "valid-call-log-1"),
            factory,
            derivedBefore
        );

        string fileParent =
            Path.Combine(_tempRoot, "execution-report-parent-file");
        File.WriteAllText(fileParent, "not a directory");
        AssertRunRejected(
            fixture,
            Path.Combine(fileParent, "report.json"),
            Path.Combine(_tempRoot, "valid-call-log-2"),
            factory,
            derivedBefore
        );

        AssertRunRejected(
            fixture,
            _tempRoot,
            Path.Combine(_tempRoot, "valid-call-log-3"),
            factory,
            derivedBefore
        );

        string callLogFile =
            Path.Combine(_tempRoot, "call-log-file");
        File.WriteAllText(callLogFile, "not a directory");
        AssertRunRejected(
            fixture,
            Path.Combine(_tempRoot, "valid-report.json"),
            callLogFile,
            factory,
            derivedBefore
        );

        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, factory.CallCount);
    }

    private void AssertRunRejected(
        Fixture fixture,
        string reportPath,
        string callLogPath,
        ICompletionClientFactory factory,
        string expectedDerivedHash
    ) {
        Assert.Equal(1, Run(
            [
                "recap", "run",
                "--input", fixture.Path,
                "--branch", fixture.BranchName,
                "--connections", fixture.ConnectionsPath,
                "--call-log-dir", callLogPath,
                "--report-json", reportPath
            ],
            factory
        ));
        Assert.Equal(
            expectedDerivedHash,
            HashDerivedFiles(fixture.Path)
        );
    }

    private ValueTask<Fixture> CreateFixtureAsync(
        string name,
        int historyPairs
    ) {
        Directory.CreateDirectory(_tempRoot);
        string path = Path.Combine(_tempRoot, name);
        using var engine = SJ.SessionJournalEngine.Create(
            path,
            new SJ.SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        );
        for (int index = 0; index < historyPairs; index++) {
            engine.AppendObservation($"observation {index}");
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"action {index}")
                ]),
                new CompletionDescriptor(
                    "import",
                    "v1",
                    "model-a"
                )
            );
        }
        SJ.SessionCurrentLineageSnapshot lineage =
            engine.ReadCurrentLineageHeaders();
        string connectionsPath = Path.Combine(
            _tempRoot,
            $"{name}-connections.json"
        );
        File.WriteAllText(
            connectionsPath,
            JsonSerializer.Serialize(
                new CompletionConnectionsFileConfig(
                    [
                        new CompletionConnectionConfig(
                            "scripted",
                            "scripted",
                            "model-a",
                            "surface-a",
                            "http://localhost/"
                        )
                    ],
                    "scripted"
                )
            )
        );
        return ValueTask.FromResult(new Fixture(
            path,
            engine.BranchName,
            engine.BranchRefId.ToHexString(),
            lineage.CapturedHead,
            lineage.HeadToRoot[1].Address,
            lineage.HeadToRoot.Count,
            connectionsPath
        ));
    }

    private static async ValueTask CreateStoreAsync(
        Fixture fixture
    ) {
        using var engine = SJ.SessionJournalEngine.OpenReadOnly(
            fixture.Path,
            fixture.BranchName
        );
        await DerivedRecapStore.Open(
                fixture.Path,
                engine.BranchRefId
            )
            .CreateAsync();
        Assert.IsType<RecapPlannerConfigInitializeResult.Initialized>(
            RecapPlannerConfigInitializer.Initialize(
                fixture.Path,
                FastCadenceConfig
            )
        );
    }

    private string[] ExecuteArgs(
        Fixture fixture,
        string operation,
        string reportPath,
        string callLogName,
        string? anchor = null,
        string? expectedRawHead = null
    ) => [
        "recap", operation,
        "--input", fixture.Path,
        "--branch", fixture.BranchName,
        "--connections", fixture.ConnectionsPath,
        "--call-log-dir", Path.Combine(_tempRoot, callLogName),
        "--report-json", reportPath,
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

    private static int Run(
        string[] args,
        ICompletionClientFactory factory
    ) => Program.MainCore(args, factory);

    private static string FindPublishedBlock(
        Fixture fixture,
        string blockId
    ) => Assert.Single(
        Directory.EnumerateFiles(
            Path.Combine(
                fixture.Path,
                "derived",
                "recap",
                "v4",
                "refs",
                fixture.BranchRefId,
                "published"
            ),
            $"{blockId}.json",
            SearchOption.AllDirectories
        ),
        path => path.Contains(
            $"{Path.DirectorySeparatorChar}blocks"
            + Path.DirectorySeparatorChar,
            StringComparison.Ordinal
        )
    );

    private static void AssertReportIsContentFree(string report) {
        foreach (string forbidden in new[] {
                     "SECRET-",
                     "\"detail\"",
                     "\"content\"",
                     "\"prompt\"",
                     "\"response\"",
                     "\"providerReason\"",
                     "\"stateToken\""
                 }) {
            Assert.DoesNotContain(
                forbidden,
                report,
                StringComparison.OrdinalIgnoreCase
            );
        }
    }

    private static JsonDocument ReadJson(string path) =>
        JsonDocument.Parse(File.ReadAllText(path));

    private static string String(
        JsonDocument document,
        string property
    ) => document.RootElement.GetProperty(property).GetString()!;

    private static RawSnapshot ReadRawSnapshot(string path) {
        using EventJournal.EventJournal journal =
            EventJournal.EventJournal.OpenReadOnlyExisting(path);
        RefId main = journal.OpenBranch(
            SJ.SessionJournalDefaults.MainBranchName
        ).Unwrap();
        EventAddress head = journal.GetHead(main)!.Value;
        IReadOnlyList<EventAddress> chain =
            journal.ReadChronologicalChain(
                head,
                checkedRead: true
            ).Unwrap();
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        string derivedPart =
            $"{Path.DirectorySeparatorChar}derived"
            + Path.DirectorySeparatorChar;
        string configPart =
            $"{Path.DirectorySeparatorChar}config"
            + Path.DirectorySeparatorChar;
        foreach (string file in Directory
                     .EnumerateFiles(
                         path,
                         "*",
                         SearchOption.AllDirectories
                     )
                     .Where(file => !file.Contains(
                         derivedPart,
                         StringComparison.Ordinal
                     ))
                     .Where(file => !file.Contains(
                         configPart,
                         StringComparison.Ordinal
                     ))
                     .OrderBy(
                         static file => file,
                         StringComparer.Ordinal
                     )) {
            hash.AppendData(File.ReadAllBytes(file));
        }
        return new RawSnapshot(
            head,
            chain.Count,
            Convert.ToHexStringLower(hash.GetHashAndReset())
        );
    }

    private static string HashDerivedFiles(string path) {
        string derivedRoot = Path.Combine(path, "derived");
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        foreach (string file in Directory
                     .EnumerateFiles(
                         derivedRoot,
                         "*",
                         SearchOption.AllDirectories
                     )
                     .OrderBy(
                         static file => file,
                         StringComparer.Ordinal
                     )) {
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed record Fixture(
        string Path,
        string BranchName,
        string BranchRefId,
        EventAddress Head,
        EventAddress PreviousAddress,
        int EventCount,
        string ConnectionsPath
    );

    private sealed record RawSnapshot(
        EventAddress Head,
        int EventCount,
        string RawFilesSha256
    );

    private sealed class ScriptedCompletionClientFactory(
        string responseText,
        int? failAtCall = null,
        string failureMessage = "scripted failure",
        Action? onCreate = null
    ) : ICompletionClientFactory {
        private readonly ScriptedCompletionClient _client = new(
            responseText,
            failAtCall,
            failureMessage
        );

        public int CreateCallCount { get; private set; }
        public int CallCount => _client.CallCount;

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            CreateCallCount++;
            onCreate?.Invoke();
            return _client;
        }
    }

    private sealed class ScriptedCompletionClient(
        string responseText,
        int? failAtCall,
        string failureMessage
    ) : ICompletionClient {
        private int _callCount;

        public string Name => "scripted";
        public string ApiSpecId => "test-api-v1";
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            int call = Interlocked.Increment(ref _callCount);
            if (call == failAtCall) {
                throw new HttpRequestException(failureMessage);
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
