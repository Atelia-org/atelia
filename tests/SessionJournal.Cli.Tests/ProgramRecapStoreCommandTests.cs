using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramRecapStoreCommandTests : IDisposable {
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-store-cli-tests",
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
    public async Task CreateIsBranchExactDuplicateFailsAndRawIsUnchanged() {
        Fixture fixture = await CreateFixtureAsync();
        RawSnapshot raw = ReadRawSnapshot(fixture.Path);
        string reportPath = Path.Combine(_tempRoot, "create.json");

        Assert.Equal(0, Run([
            "recap", "create",
            "--input", fixture.Path,
            "--branch", fixture.BranchName,
            "--report-json", reportPath
        ]));

        using JsonDocument report = ReadJson(reportPath);
        Assert.Equal(
            "atelia.session-journal.derived-recap-store-operation.v1",
            String(report, "schema")
        );
        Assert.Equal("create", String(report, "operation"));
        Assert.Equal(fixture.BranchRefId, String(report, "branchRefId"));
        Assert.Equal("Created", String(report, "resultType"));
        Assert.Equal(raw, ReadRawSnapshot(fixture.Path));

        string duplicateReport =
            Path.Combine(_tempRoot, "duplicate.json");
        Assert.Equal(1, Run([
            "recap", "create",
            "--input", fixture.Path,
            "--branch", fixture.BranchName,
            "--report-json", duplicateReport
        ]));
        Assert.False(File.Exists(duplicateReport));
        Assert.Equal(raw, ReadRawSnapshot(fixture.Path));
        AssertNoTemporaryReports();
    }

    [Fact]
    public async Task InspectReportsHealthyAndDamagedWithoutContentOrTokens() {
        Fixture fixture = await CreateFixtureAsync();
        Assert.Equal(0, Run(CreateArgs(fixture)));
        RawSnapshot raw = ReadRawSnapshot(fixture.Path);
        const string secret = "SECRET-RECAP-CONTENT-DO-NOT-REPORT";
        await CreateFinalBuildingAsync(fixture, secret);
        string healthyPath = Path.Combine(_tempRoot, "healthy.json");

        Assert.Equal(0, Run(InspectArgs(fixture, healthyPath)));
        string healthyText = File.ReadAllText(healthyPath);
        using (JsonDocument healthy = JsonDocument.Parse(healthyText)) {
            Assert.Equal(
                "atelia.session-journal.derived-recap-store-inspection.v2",
                String(healthy, "schema")
            );
            Assert.Equal("Available", healthy.RootElement
                .GetProperty("building")
                .GetProperty("state")
                .GetString());
            Assert.Equal(
                "Absent",
                healthy.RootElement
                    .GetProperty("published")
                    .GetProperty("membership")
                    .GetProperty("state")
                    .GetString()
            );
            Assert.Equal(
                "NotApplicable",
                healthy.RootElement
                    .GetProperty("published")
                    .GetProperty("restoreEligibility")
                    .GetProperty("state")
                    .GetString()
            );
            JsonElement block = Assert.Single(
                healthy.RootElement
                    .GetProperty("building")
                    .GetProperty("blocks")
                    .EnumerateArray()
            );
            Assert.Equal(
                "Healthy",
                block.GetProperty("finalState").GetString()
            );
            Assert.Equal(
                "Healthy",
                block.GetProperty("checkpointState").GetString()
            );
        }
        AssertSafeInspectionReport(healthyText, secret);

        await PublishAndDamageBlockAsync(fixture);
        string damagedPath = Path.Combine(_tempRoot, "damaged.json");
        Assert.Equal(0, Run(InspectArgs(fixture, damagedPath)));
        string damagedText = File.ReadAllText(damagedPath);
        using (JsonDocument damaged = JsonDocument.Parse(damagedText)) {
            JsonElement published = damaged.RootElement
                .GetProperty("published");
            Assert.Equal(
                "Invalid",
                published
                    .GetProperty("membership")
                    .GetProperty("state")
                    .GetString()
            );
            JsonElement eligibility =
                published.GetProperty("restoreEligibility");
            Assert.Equal(
                "Available",
                eligibility.GetProperty("state").GetString()
            );
            JsonElement block = Assert.Single(
                eligibility.GetProperty("blocks").EnumerateArray()
            );
            Assert.Equal(
                "Damaged",
                block.GetProperty("finalState").GetString()
            );
            Assert.False(string.IsNullOrWhiteSpace(
                block.GetProperty("capability").GetString()
            ));
            Assert.NotEmpty(
                block.GetProperty("defects").EnumerateArray()
            );
        }
        AssertSafeInspectionReport(damagedText, secret);
        Assert.Equal(raw, ReadRawSnapshot(fixture.Path));
        AssertNoTemporaryReports();
    }

    [Fact]
    public async Task InspectSeparatesOffLineageMembershipFromRestore()
    {
        Fixture present = await CreateFixtureAsync("off-present");
        Assert.Equal(0, Run(CreateArgs(present)));
        await CreateFinalBuildingAsync(present, "present");
        await PublishAsync(present);
        DivergeBefore(present, present.Anchor, present.ReplayStart);
        string presentReport =
            Path.Combine(_tempRoot, "off-present.json");

        Assert.Equal(0, Run(InspectArgs(present, presentReport)));
        using (JsonDocument report = ReadJson(presentReport)) {
            JsonElement published =
                report.RootElement.GetProperty("published");
            Assert.Equal(
                "Present",
                published.GetProperty("membership")
                    .GetProperty("state")
                    .GetString()
            );
            JsonElement eligibility =
                published.GetProperty("restoreEligibility");
            Assert.Equal(
                "Unavailable",
                eligibility.GetProperty("state").GetString()
            );
            Assert.Contains(
                eligibility.GetProperty("defects").EnumerateArray(),
                defect => defect.GetProperty("code").GetString()
                    == "AdmissionAnchorOffLineage"
            );
        }
        AssertSafeInspectionReport(
            File.ReadAllText(presentReport),
            "present"
        );

        Fixture absent = await CreateFixtureAsync("off-absent");
        Assert.Equal(0, Run(CreateArgs(absent)));
        DivergeBefore(absent, absent.Anchor, absent.ReplayStart);
        string absentReport =
            Path.Combine(_tempRoot, "off-absent.json");

        Assert.Equal(0, Run(InspectArgs(absent, absentReport)));
        using JsonDocument missing = ReadJson(absentReport);
        JsonElement missingPublished =
            missing.RootElement.GetProperty("published");
        Assert.Equal(
            "Absent",
            missingPublished.GetProperty("membership")
                .GetProperty("state")
                .GetString()
        );
        Assert.Equal(
            "NotApplicable",
            missingPublished.GetProperty("restoreEligibility")
                .GetProperty("state")
                .GetString()
        );
    }

    [Fact]
    public async Task AbandonIsIdempotentAndPublishedMembershipConflicts() {
        Fixture fixture = await CreateFixtureAsync();
        Assert.Equal(0, Run(CreateArgs(fixture)));
        RawSnapshot raw = ReadRawSnapshot(fixture.Path);
        await CreateBuildingAsync(fixture);
        string firstPath = Path.Combine(_tempRoot, "abandon-1.json");

        Assert.Equal(0, Run(AbandonArgs(fixture, firstPath)));
        using (JsonDocument first = ReadJson(firstPath)) {
            Assert.Equal("Quarantined", String(first, "resultType"));
            Assert.False(string.IsNullOrWhiteSpace(
                String(first, "quarantineId")
            ));
        }

        string secondPath = Path.Combine(_tempRoot, "abandon-2.json");
        Assert.Equal(0, Run(AbandonArgs(fixture, secondPath)));
        using (JsonDocument second = ReadJson(secondPath)) {
            Assert.Equal("AlreadyAbsent", String(second, "resultType"));
        }

        await CreateFinalBuildingAsync(fixture, "publishable");
        await PublishAsync(fixture);
        string conflictPath =
            Path.Combine(_tempRoot, "abandon-conflict.json");
        Assert.Equal(2, Run(AbandonArgs(fixture, conflictPath)));
        using (JsonDocument conflict = ReadJson(conflictPath)) {
            Assert.Equal(
                "PublishedConflict",
                String(conflict, "resultType")
            );
        }
        Assert.Equal(raw, ReadRawSnapshot(fixture.Path));
        AssertNoTemporaryReports();
    }

    [Fact]
    public async Task ResetRequiresExactRefBeforeAnyMutation() {
        Fixture fixture = await CreateFixtureAsync();
        Assert.Equal(0, Run(CreateArgs(fixture)));
        await CreateBuildingAsync(fixture);
        RawSnapshot raw = ReadRawSnapshot(fixture.Path);
        string derivedBefore = HashDerivedFiles(fixture.Path);
        string wrongRefId =
            (fixture.BranchRefId[0] == '0' ? "1" : "0")
            + fixture.BranchRefId[1..];
        string rejectedReport =
            Path.Combine(_tempRoot, "reset-rejected.json");

        Assert.Equal(1, Run([
            "recap", "reset",
            "--input", fixture.Path,
            "--branch", fixture.BranchName,
            "--confirm-ref", wrongRefId,
            "--report-json", rejectedReport
        ]));
        Assert.False(File.Exists(rejectedReport));
        Assert.Equal(derivedBefore, HashDerivedFiles(fixture.Path));

        string invalidReportDirectory =
            Path.Combine(_tempRoot, "reset-report-directory");
        Directory.CreateDirectory(invalidReportDirectory);
        Assert.Equal(1, Run([
            "recap", "reset",
            "--input", fixture.Path,
            "--branch", fixture.BranchName,
            "--confirm-ref", fixture.BranchRefId,
            "--report-json", invalidReportDirectory
        ]));
        Assert.Equal(derivedBefore, HashDerivedFiles(fixture.Path));

        string resetReport = Path.Combine(_tempRoot, "reset.json");
        Assert.Equal(0, Run([
            "recap", "reset",
            "--input", fixture.Path,
            "--branch", fixture.BranchName,
            "--confirm-ref", fixture.BranchRefId,
            "--report-json", resetReport
        ]));
        using (JsonDocument reset = ReadJson(resetReport)) {
            Assert.Equal("Reset", String(reset, "resultType"));
            Assert.Equal(fixture.BranchRefId, String(reset, "branchRefId"));
        }
        using (var engine = SJ.SessionJournalEngine.OpenReadOnly(
                   fixture.Path,
                   fixture.BranchName
               )) {
            DerivedRecapStore store = DerivedRecapStore.Open(
                fixture.Path,
                engine.BranchRefId
            );
            Assert.IsType<BuildingReadResult.Missing>(
                await store.ReadBuildingAsync(fixture.Anchor)
            );
        }
        Assert.Equal(raw, ReadRawSnapshot(fixture.Path));
        AssertNoTemporaryReports();
    }

    [Fact]
    public async Task ReportAndSymlinkPreflightRejectBeforeCreate() {
        Fixture insideFixture = await CreateFixtureAsync("inside");
        RawSnapshot insideRaw = ReadRawSnapshot(insideFixture.Path);
        Assert.Equal(1, Run([
            "recap", "create",
            "--input", insideFixture.Path,
            "--branch", insideFixture.BranchName,
            "--report-json", Path.Combine(
                insideFixture.Path,
                "operator-report.json"
            )
        ]));
        Assert.False(StoreExists(insideFixture));
        Assert.Equal(insideRaw, ReadRawSnapshot(insideFixture.Path));

        Fixture shapeFixture = await CreateFixtureAsync("shape");
        string leafDirectory =
            Path.Combine(_tempRoot, "report-leaf-directory");
        Directory.CreateDirectory(leafDirectory);
        AssertCreateRejectedBeforeStore(shapeFixture, leafDirectory);

        string fileParent = Path.Combine(_tempRoot, "report-parent-file");
        File.WriteAllText(fileParent, "not a directory");
        AssertCreateRejectedBeforeStore(
            shapeFixture,
            Path.Combine(fileParent, "report.json")
        );

        AssertCreateRejectedBeforeStore(shapeFixture, _tempRoot);

        if (OperatingSystem.IsWindows()) {
            return;
        }

        Fixture inputFixture = await CreateFixtureAsync("input-link");
        RawSnapshot inputRaw = ReadRawSnapshot(inputFixture.Path);
        string linkedInput = Path.Combine(_tempRoot, "linked-input");
        Directory.CreateSymbolicLink(linkedInput, inputFixture.Path);
        Assert.Equal(1, Run([
            "recap", "create",
            "--input", linkedInput,
            "--branch", inputFixture.BranchName
        ]));
        Assert.False(StoreExists(inputFixture));
        Assert.Equal(inputRaw, ReadRawSnapshot(inputFixture.Path));

        Fixture reportFixture = await CreateFixtureAsync("report-link");
        RawSnapshot reportRaw = ReadRawSnapshot(reportFixture.Path);
        string realReports = Path.Combine(_tempRoot, "real-reports");
        string linkedReports = Path.Combine(_tempRoot, "linked-reports");
        Directory.CreateDirectory(realReports);
        Directory.CreateSymbolicLink(linkedReports, realReports);
        Assert.Equal(1, Run([
            "recap", "create",
            "--input", reportFixture.Path,
            "--branch", reportFixture.BranchName,
            "--report-json", Path.Combine(linkedReports, "report.json")
        ]));
        Assert.False(StoreExists(reportFixture));
        Assert.False(File.Exists(
            Path.Combine(realReports, "report.json")
        ));
        Assert.Equal(reportRaw, ReadRawSnapshot(reportFixture.Path));
        AssertNoTemporaryReports();
    }

    private void AssertCreateRejectedBeforeStore(
        Fixture fixture,
        string reportPath
    ) {
        Assert.Equal(1, Run([
            "recap", "create",
            "--input", fixture.Path,
            "--branch", fixture.BranchName,
            "--report-json", reportPath
        ]));
        Assert.False(StoreExists(fixture));
    }

    private ValueTask<Fixture> CreateFixtureAsync(
        string? suffix = null
    ) {
        Directory.CreateDirectory(_tempRoot);
        string path = Path.Combine(
            _tempRoot,
            suffix ?? Guid.NewGuid().ToString("N")
        );
        using var engine = SJ.SessionJournalEngine.Create(
            path,
            new SJ.SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        );
        for (int index = 0; index < 3; index++) {
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
        return ValueTask.FromResult(
            new Fixture(
                path,
                engine.BranchName,
                engine.BranchRefId.ToHexString(),
                lineage.CapturedHead,
                lineage.HeadToRoot[2].Address
            )
        );
    }

    private static async ValueTask CreateBuildingAsync(
        Fixture fixture
    ) {
        using var engine = SJ.SessionJournalEngine.Open(
            fixture.Path,
            fixture.BranchName
        );
        DerivedRecapStore store = DerivedRecapStore.Open(
            fixture.Path,
            engine.BranchRefId
        );
        RecapBlockPlan plan = CreatePlan(fixture);
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await store.CreateBuildingAsync(
                DerivedRecapCodec.CreateManifest(
                    engine.BranchRefId,
                    fixture.Anchor,
                    [plan]
                )
            )
        );
    }

    private static async ValueTask CreateFinalBuildingAsync(
        Fixture fixture,
        string content
    ) {
        using var engine = SJ.SessionJournalEngine.Open(
            fixture.Path,
            fixture.BranchName
        );
        DerivedRecapStore store = DerivedRecapStore.Open(
            fixture.Path,
            engine.BranchRefId
        );
        RecapBlockPlan plan = CreatePlan(fixture);
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await store.CreateBuildingAsync(
                DerivedRecapCodec.CreateManifest(
                    engine.BranchRefId,
                    fixture.Anchor,
                    [plan]
                )
            )
        );
        await InstallFinalAsync(
            store,
            fixture.Anchor,
            DerivedRecapCodec.CreateBlock(
                plan,
                fixture.Anchor,
                content
            )
        );
    }

    private static async ValueTask PublishAndDamageBlockAsync(
        Fixture fixture
    ) {
        await PublishAsync(fixture);
        string publishedRoot = Path.Combine(
            fixture.Path,
            "derived",
            "recap",
            "v4",
            "refs",
            fixture.BranchRefId,
            "published"
        );
        string published = Assert.Single(
            Directory.EnumerateDirectories(publishedRoot)
        );
        string block = Assert.Single(
            Directory.EnumerateFiles(
                Path.Combine(published, "blocks"),
                "*.json"
            )
        );
        await File.WriteAllTextAsync(block, "damaged");
    }

    private static async ValueTask PublishAsync(Fixture fixture) {
        using var engine = SJ.SessionJournalEngine.Open(
            fixture.Path,
            fixture.BranchName
        );
        DerivedRecapStore store = DerivedRecapStore.Open(
            fixture.Path,
            engine.BranchRefId
        );
        _ = await new DerivedRecapPublisher(store, engine)
            .PublishAsync(fixture.Anchor);
    }

    private static void DivergeBefore(
        Fixture fixture,
        EventAddress expectedHead,
        EventAddress rewindTo
    ) {
        RefId refId;
        using (var journal =
               EventJournal.EventJournal.OpenExisting(fixture.Path)) {
            refId = journal.OpenBranch(fixture.BranchName).Unwrap();
            journal.MoveRef(refId, expectedHead, rewindTo).Unwrap();
        }
        using (var engine = SJ.SessionJournalEngine.Open(
                   fixture.Path,
                   fixture.BranchName
               )) {
            engine.AppendObservation("diverged observation");
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text("diverged action")
                ]),
                new CompletionDescriptor(
                    "import",
                    "v1",
                    "model-a"
                )
            );
            Assert.Equal(refId, engine.BranchRefId);
            Assert.DoesNotContain(
                engine.ReadCurrentLineageHeaders().HeadToRoot,
                node => node.Address == expectedHead
            );
        }
    }

    private static RecapBlockPlan CreatePlan(Fixture fixture) =>
        new MaintainRecapBlockPlan(
            new RecapBlockId("roleplay.self"),
            new SJ.ContextHeaderBlockPath(
                SJ.ContextHeaderCarrier.System,
                "roleplay.self"
            ),
            "test.roleplay",
            new EmptyRecapMaintainSource(fixture.ReplayStart),
            [fixture.Anchor],
            EmptyRecapPriorContext.Instance
        );

    private static async ValueTask InstallFinalAsync(
        DerivedRecapStore store,
        EventAddress anchor,
        DerivedRecapBlock block
    ) {
        BuildingReadResult.Available building =
            Assert.IsType<BuildingReadResult.Available>(
                await store.ReadBuildingAsync(anchor)
            );
        BuildingBlockInspection inspection =
            await store.InspectBuildingBlockAsync(
                building.Snapshot.Descriptor,
                block.RecapBlockId
            );
        var maintain = Assert.IsType<MaintainRecapBlockPlan>(
            inspection.Plan
        );
        for (int index = 0;
             index < maintain.CatchUpThrough.Count;
             index++) {
            DerivedRecapBlock checkpoint =
                index == maintain.CatchUpThrough.Count - 1
                    ? block
                    : DerivedRecapCodec.CreateBlock(
                        maintain,
                        maintain.CatchUpThrough[index],
                        block.Content
                    );
            Assert.IsType<CheckpointWriteResult.Updated>(
                await store.AdvanceRollingCheckpointAsync(
                    building.Snapshot.Descriptor,
                    block.RecapBlockId,
                    inspection.Checkpoint.StateToken,
                    checkpoint
                )
            );
            inspection = await store.InspectBuildingBlockAsync(
                building.Snapshot.Descriptor,
                block.RecapBlockId
            );
        }
        _ = await store.EnsureFinalBlockAsync(
            building.Snapshot.Descriptor,
            block.RecapBlockId,
            inspection.Final.StateToken,
            block
        );
    }

    private static void AssertSafeInspectionReport(
        string report,
        string secret
    ) {
        Assert.DoesNotContain(secret, report, StringComparison.Ordinal);
        foreach (string forbidden in new[] {
                     "\"content\"",
                     "\"frozenInput\"",
                     "\"priorContext\"",
                     "\"stateToken\"",
                     "\"manifestPayloadSha256\"",
                     "\"envelopeSha256\"",
                     "\"payloadSha256\""
                 }) {
            Assert.DoesNotContain(
                forbidden,
                report,
                StringComparison.OrdinalIgnoreCase
            );
        }
    }

    private static string[] CreateArgs(Fixture fixture) => [
        "recap", "create",
        "--input", fixture.Path,
        "--branch", fixture.BranchName
    ];

    private static string[] InspectArgs(
        Fixture fixture,
        string reportPath
    ) => [
        "recap", "inspect",
        "--input", fixture.Path,
        "--branch", fixture.BranchName,
        "--anchor", SJ.EventAddressTextCodec.Format(fixture.Anchor),
        "--report-json", reportPath
    ];

    private static string[] AbandonArgs(
        Fixture fixture,
        string reportPath
    ) => [
        "recap", "abandon-building",
        "--input", fixture.Path,
        "--branch", fixture.BranchName,
        "--anchor", SJ.EventAddressTextCodec.Format(fixture.Anchor),
        "--report-json", reportPath
    ];

    private static int Run(string[] args) => Program.MainCore(
        args,
        ThrowingCompletionClientFactory.Instance
    );

    private static JsonDocument ReadJson(string path) =>
        JsonDocument.Parse(File.ReadAllText(path));

    private static string String(
        JsonDocument document,
        string property
    ) => document.RootElement.GetProperty(property).GetString()!;

    private static bool StoreExists(Fixture fixture) =>
        File.Exists(Path.Combine(
            fixture.Path,
            "derived",
            "recap",
            "v4",
            "refs",
            fixture.BranchRefId,
            "store.json"
        ));

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
        return new RawSnapshot(
            head,
            chain.Count,
            HashFiles(path, includeDerived: false)
        );
    }

    private static string HashDerivedFiles(string path) =>
        HashFiles(path, includeDerived: true);

    private static string HashFiles(
        string path,
        bool includeDerived
    ) {
        string derivedPart =
            $"{Path.DirectorySeparatorChar}derived"
            + Path.DirectorySeparatorChar;
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        foreach (string file in Directory
                     .EnumerateFiles(
                         path,
                         "*",
                         SearchOption.AllDirectories
                     )
                     .Where(file => includeDerived
                         ? file.Contains(
                             derivedPart,
                             StringComparison.Ordinal
                         )
                         : !file.Contains(
                             derivedPart,
                             StringComparison.Ordinal
                         )
                     )
                     .OrderBy(
                         static file => file,
                         StringComparer.Ordinal
                     )) {
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private void AssertNoTemporaryReports() {
        Assert.Empty(Directory.EnumerateFiles(
            _tempRoot,
            ".*.tmp",
            SearchOption.AllDirectories
        ));
    }

    private sealed record Fixture(
        string Path,
        string BranchName,
        string BranchRefId,
        EventAddress Anchor,
        EventAddress ReplayStart
    );

    private sealed record RawSnapshot(
        EventAddress Head,
        int EventCount,
        string RawFilesSha256
    );

    private sealed class ThrowingCompletionClientFactory
        : ICompletionClientFactory {
        public static ThrowingCompletionClientFactory Instance { get; } =
            new();

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => throw new InvalidOperationException(
            $"recap Store command must not create Completion client "
            + $"'{connection.Id}'."
        );
    }
}
