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
        RecapPlannerConfig config =
            RecapCliComposition.CreateConfig();
        Assert.Collection(
            config.Catalog,
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
                    32_768,
                    autobiography.MaxContentUtf8Bytes
                );
            }
        );
        Assert.Equal(32, config.RawGrowthTrigger);
        Assert.Equal(512, config.RawGrowthHardLimit);
        Assert.Equal(4, config.MaxRouteEndpointsPerBlock);
        Assert.Equal(8, config.MaxMaintainerCallsPerBuild);
        Assert.Equal(64, config.MaxRawEventsPerStep);
        Assert.Equal(512, config.MaxRawEventsPerBuild);
    }

    [Fact]
    public async Task PartialFailureReopensSuffixPublishesThenRestoresCorrupt()
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
        var resumeFactory = new ScriptedCompletionClientFactory(
            "SECRET-RECAP-RESULT"
        );
        Assert.Equal(0, Run(
            ExecuteArgs(
                fixture,
                "resume",
                resumeReport,
                "resume-calls",
                anchor
            ),
            resumeFactory
        ));
        Assert.Equal("Published", String(
            ReadJson(resumeReport),
            "resultStatus"
        ));
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
                    await store.InspectPublishedForRestoreAsync(
                        publishedAnchor,
                        engine.ReadCurrentLineageHeaders()
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
        string failureMessage = "scripted failure"
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
