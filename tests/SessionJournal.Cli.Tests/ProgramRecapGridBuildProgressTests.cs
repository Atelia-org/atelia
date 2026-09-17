using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.RecapGrid;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed partial class ProgramRecapGridCommandTests {
    [Fact]
    public async Task GalateaBuildReportsProgressBeforeSlowProviderFinishesThenColdBuildCreatesNothing() {
        BuildProgressFixture fixture = PrepareBuildProgressFixture();
        var provider = new GatedBuildFactory();
        using var output = new BuildCaptureWriter();
        using var error = new BuildCaptureWriter();
        TextWriter originalOutput = Console.Out;
        TextWriter originalError = Console.Error;
        Task<int>? running = null;
        string firstRowResultId;
        try {
            Console.SetOut(output);
            Console.SetError(error);
            running = Task.Run(() => RunWithFactory(provider, fixture.Arguments(64)));
            await Task.WhenAny(provider.Entered.Task, running).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(provider.Entered.Task.IsCompleted, "Provider was not invoked. stderr=" + error.Snapshot() + " stdout=" + output.Snapshot());
            await error.LineWritten.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(running.IsCompleted);
            Assert.Empty(output.Snapshot());
            Assert.Contains("[recap-build] request-start", error.Snapshot());
            provider.Release.TrySetResult();
            Assert.Equal(0, await running.WaitAsync(TimeSpan.FromSeconds(30)));
            // Parse the entire output: logging an extra line to stdout must fail.
            using JsonDocument report = JsonDocument.Parse(output.Snapshot());
            Assert.Equal("fulfilled", report.RootElement.GetProperty("status").GetString());
            JsonElement result = report.RootElement.GetProperty("detail").GetProperty("result");
            JsonElement proof = result.GetProperty("Proof");
            firstRowResultId = proof.GetProperty("RowResultId").GetProperty("Value").GetString()!;
            Assert.Equal(fixture.Recipe.Digest.Value, proof.GetProperty("RecipeDigest").GetProperty("Value").GetString());
            Assert.Equal(fixture.RowCount * 2, provider.RequestCount);
            Assert.Equal(fixture.RowCount * 2, result.GetProperty("Metrics").GetProperty("NewCalls").GetInt32());
            Assert.Equal(1, provider.CreateCount);
            Assert.Equal(1, provider.DisposeCount);
            string progress = error.Snapshot();
            int ended = progress.IndexOf("[recap-build] request-end", StringComparison.Ordinal);
            int rowCommitted = progress.IndexOf("[recap-build] row-committed", StringComparison.Ordinal);
            Assert.True(ended >= 0 && rowCommitted > ended, progress);
            Assert.Contains("[recap-build] summary", progress);
        }
        finally {
            provider.Release.TrySetResult();
            try {
                if (running is not null) { await running.WaitAsync(TimeSpan.FromSeconds(30)); }
            }
            finally {
                Console.SetOut(originalOutput);
                Console.SetError(originalError);
            }
        }

        DomainSnapshot before = SnapshotDomains();
        int environmentReads = 0;
        var coldFactory = new CliCompletionClientFactory(ThrowingCompletionClientFactory.Instance, _ => {
            environmentReads++;
            throw new InvalidOperationException("A cached build must not inspect subscription environment.");
        });
        using var coldOutput = new BuildCaptureWriter();
        using var coldError = new BuildCaptureWriter();
        try {
            Console.SetOut(coldOutput);
            Console.SetError(coldError);
            Assert.Equal(0, RunWithFactory(coldFactory, fixture.Arguments(0)));
        }
        finally {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
        using JsonDocument coldReport = JsonDocument.Parse(coldOutput.Snapshot());
        JsonElement coldResult = coldReport.RootElement.GetProperty("detail").GetProperty("result");
        Assert.Equal("fulfilled", coldReport.RootElement.GetProperty("status").GetString());
        Assert.Equal(firstRowResultId, coldResult.GetProperty("Proof").GetProperty("RowResultId").GetProperty("Value").GetString());
        Assert.Equal(0, coldResult.GetProperty("Metrics").GetProperty("NewCalls").GetInt32());
        Assert.Equal(0, environmentReads);
        Assert.Empty(coldReport.RootElement.GetProperty("detail").GetProperty("evidence").GetProperty("Events").EnumerateArray());
        AssertDomainsEqual(before, SnapshotDomains());
    }

    [Fact]
    public void MissingBuildRoutePreservesSpecificCodeAndDetailInFinalJson() {
        BuildProgressFixture fixture = PrepareBuildProgressFixture();
        WriteFormattedBuildRoutes(fixture.RoutesPath, RecapGridRouteManifest.Create([]));
        DomainSnapshot before = SnapshotDomains();
        TextWriter originalOutput = Console.Out;
        TextWriter originalError = Console.Error;
        using var output = new BuildCaptureWriter();
        using var error = new BuildCaptureWriter();
        try {
            Console.SetOut(output);
            Console.SetError(error);
            int exitCode = RunWithFactory(ThrowingCompletionClientFactory.Instance, fixture.Arguments(64));
            Assert.True(exitCode == 2, "Unexpected exit code " + exitCode + ". stderr=" + error.Snapshot() + " stdout=" + output.Snapshot());
        }
        finally {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
        using JsonDocument report = JsonDocument.Parse(output.Snapshot());
        Assert.Equal("executor-rejected", report.RootElement.GetProperty("status").GetString());
        JsonElement result = report.RootElement.GetProperty("detail").GetProperty("result");
        Assert.Equal("ExactRouteAbsent", result.GetProperty("Code").GetString());
        Assert.Equal("No exact recap completion route is configured.", result.GetProperty("Detail").GetString());
        // Route resolution happens after the first durable RowWork selection.
        // No provider is constructed, and raw/timeline/control authority stays
        // unchanged; the V5 grid records the frozen retryable work.
        DomainSnapshot after = SnapshotDomains();
        AssertSnapshotEqual(before.Raw, after.Raw);
        AssertSnapshotEqual(before.Timeline, after.Timeline);
        AssertSnapshotEqual(before.Control, after.Control);
        Assert.NotEqual(before.Grid.Values.SelectMany(static value => value),
            after.Grid.Values.SelectMany(static value => value));
    }

    [Fact]
    public void LiveBuildWithoutPolicyDoesNotReadRoutesOrConnectionsOrCreateClient() {
        BuildProgressFixture fixture = PrepareBuildProgressFixture();
        var factory = new GatedBuildFactory();
        string missingRoutes = Path.Combine(_root, "must-not-read-routes.json");
        string missingConnections = Path.Combine(_root, "must-not-read-connections.json");
        using var output = new BuildCaptureWriter();
        TextWriter original = Console.Out;
        try {
            Console.SetOut(output);
            Assert.Equal(2, RunWithFactory(factory, fixture.LiveArguments(
                missingRoutes, missingConnections)));
        }
        finally {
            Console.SetOut(original);
        }
        using JsonDocument report = JsonDocument.Parse(output.Snapshot());
        Assert.Equal("producer-policy-required",
            report.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public void LiveBuildAcceptsCanonicalProducerTargetFile() {
        BuildProgressFixture fixture = PrepareBuildProgressFixture();
        string target = Path.Combine(_root, "producer-target.canonical");
        File.WriteAllBytes(target, fixture.Recipe.Target.ToCanonicalBytes());
        var factory = new GatedBuildFactory();
        factory.Release.TrySetResult();
        using var output = new BuildCaptureWriter();
        TextWriter original = Console.Out;
        try {
            Console.SetOut(output);
            Assert.Equal(0, RunWithFactory(factory, fixture.LiveArguments(
                fixture.RoutesPath, fixture.ConnectionsPath, target)));
        }
        finally {
            Console.SetOut(original);
        }
        using JsonDocument report = JsonDocument.Parse(output.Snapshot());
        Assert.Equal("fulfilled", report.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public void RecipeBuildRejectsProducerTargetBeforeReadingItsFileOrCreatingClient() {
        BuildProgressFixture fixture = PrepareBuildProgressFixture();
        var factory = new GatedBuildFactory();
        string missingTarget = Path.Combine(_root, "must-not-read-target.canonical");
        Assert.Equal(1, RunWithFactory(factory, [
            .. fixture.Arguments(64), "--producer-target", missingTarget]));
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public void ProgressAndPromotionRejectProducerTargetOption() {
        var factory = new GatedBuildFactory();
        Assert.Equal(1, RunWithFactory(factory,
            "progress", "--input", _root, "--live",
            "--producer-target", "not-a-target"));
        Assert.Equal(1, RunWithFactory(factory,
            "control", "promote", "--input", _root,
            "--producer-target", "not-a-target"));
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public void ControlPromoteAcceptsPrefixThroughRowForCompletedFullV2Candidate() {
        BuildProgressFixture fixture = PrepareBuildProgressFixture(turns: 10);
        var factory = new GatedBuildFactory();
        factory.Release.TrySetResult();
        Assert.Equal(0, RunWithFactory(factory, fixture.Arguments(64)));

        RefId refId = RefId.ParseHex(fixture.RefId).Value;
        HistoryTimelineSelectedRow h5;
        HistoryTimelineSelectedRow h10;
        using (HistoryTimelineReaderHandle timeline = Assert.IsType<
                   HistoryTimelineReaderOpenResult.Opened
               >(HistoryTimelineMaintenance.OpenReader(_root, refId)).Handle) {
            IReadOnlyList<HistoryTimelineSelectedRow> rows = Assert.IsType<
                HistoryTimelinePathPageResult.Page>(
                timeline.Reader.ReadSelectedPathPage(
                    fixture.TimelineHead, maximumRows: 10)
            ).Value.Rows;
            h5 = rows[4];
            h10 = Assert.Single(rows, row =>
                row.Descriptor.RowId == fixture.TimelineHead.HeadRowId);
        }
        GridBuildRecipe candidate = GridBuildRecipe.CreateFull(
            fixture.TimelineHead.TimelineId, h5.Descriptor.RowId,
            fixture.Recipe.Target, fixture.Recipe.Digest);
        using (RecapGridControlHandle control = Assert.IsType<
                   RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(_root, refId, fixture.Admission)).Handle) {
            ControlHeadRef head = Assert.IsType<RecapGridControlSnapshotResult.Available>(
                control.Reader.ReadSnapshot()).Snapshot.Head;
            _ = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutBuildRecipe(
                    head, fixture.TimelineHead, candidate, h5.Witness));
        }
        string[] candidateBuild = [
            "build", "--input", _root, "--confirm-ref", fixture.RefId,
            "--recipe", candidate.Digest.Value,
            "--through-row", h5.Descriptor.RowId.Value,
            "--max-recipe-row-steps", "64", "--max-new-calls", "64",
            "--max-elapsed-ms", "30000", "--routes", fixture.RoutesPath,
            "--connections", fixture.ConnectionsPath
        ];
        int callsBeforeCandidate = factory.RequestCount;
        factory.FailRequestNumberOnce = callsBeforeCandidate + 2;
        (int failedBuildCode, JsonElement failedBuild) =
            RunCapturedWithFactory(factory, candidateBuild);
        Assert.Equal(2, failedBuildCode);
        Assert.Equal("incomplete",
            failedBuild.GetProperty("status").GetString());
        Assert.Equal(callsBeforeCandidate + 2, factory.RequestCount);
        Assert.Equal(fixture.Recipe.Digest,
            ReadControlHead(fixture.RefId).ActiveRecipeDigest);

        List<RecapGridStoreExportItem> ReadCandidateWorks() {
            var works = new List<RecapGridStoreExportItem>();
            RecapGridStoreExportCursor? cursor = null;
            do {
                RecapGridStoreExportPage page = Assert.IsType<
                    RecapGridStoreExportResult.Page
                >(RecapGridStoreMaintenance.Export(
                    _root,
                    cursor,
                    includeContent: true
                )).Value;
                foreach (RecapGridStoreExportItem item in page.Items.Where(
                             static item => item.Kind == "row-work")) {
                    using JsonDocument work = JsonDocument.Parse(
                        Assert.IsType<byte[]>(item.Json));
                    if (string.Equals(
                            work.RootElement.GetProperty("rootRecipeDigest")
                                .GetString(),
                            candidate.Digest.Value,
                            StringComparison.Ordinal)) {
                        works.Add(item);
                    }
                }
                if (!page.Incomplete) { break; }
                cursor = Assert.IsType<RecapGridStoreExportCursor>(
                    page.NextCursor);
            } while (true);
            return works;
        }
        RecapGridStoreExportItem frozenWork = Assert.Single(
            ReadCandidateWorks());
        (int resumedBuildCode, JsonElement resumedBuild) =
            RunCapturedWithFactory(factory, candidateBuild);
        Assert.Equal(0, resumedBuildCode);
        Assert.Equal("fulfilled-through",
            resumedBuild.GetProperty("status").GetString());
        List<RecapGridStoreExportItem> completedWorks = ReadCandidateWorks();
        int expectedCandidateCells = 0;
        foreach (RecapGridStoreExportItem item in completedWorks) {
            using JsonDocument work = JsonDocument.Parse(
                Assert.IsType<byte[]>(item.Json));
            expectedCandidateCells += work.RootElement
                .GetProperty("orderedAssignments").GetArrayLength();
        }
        Assert.Equal(
            expectedCandidateCells - 1,
            resumedBuild.GetProperty("detail").GetProperty("result")
                .GetProperty("Metrics").GetProperty("NewCalls").GetInt32()
        );
        Assert.Equal(
            callsBeforeCandidate + expectedCandidateCells + 1,
            factory.RequestCount
        );
        RecapGridStoreExportItem resumedWork = Assert.Single(
            completedWorks,
            work => string.Equals(
                work.Key, frozenWork.Key, StringComparison.Ordinal)
        );
        Assert.Equal(
            Assert.IsType<byte[]>(frozenWork.Json),
            Assert.IsType<byte[]>(resumedWork.Json)
        );
        Assert.Equal(fixture.Recipe.Digest,
            ReadControlHead(fixture.RefId).ActiveRecipeDigest);

        string admission = Path.Combine(_root, "prefix-promote-admission.json");
        File.WriteAllBytes(admission, fixture.Admission.ToCanonicalBytes());
        using (RecapGridControlHandle lostResponse = Assert.IsType<
                   RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   _root, refId, fixture.Admission)).Handle) {
            ControlHeadRef head = Assert.IsType<
                RecapGridControlSnapshotResult.Available
            >(lostResponse.Reader.ReadSnapshot()).Snapshot.Head;
            _ = lostResponse.Coordinator.CompareExchangeActiveRecipe(
                head,
                fixture.TimelineHead,
                candidate.Digest,
                RecapGridControlActivationPurpose.Promotion
            );
        }
        ControlHeadRef publishedHead = ReadControlHead(fixture.RefId);
        Assert.Equal(candidate.Digest, publishedHead.ActiveRecipeDigest);
        DomainSnapshot beforeLostResponseRecovery = SnapshotDomains();
        (int code, JsonElement report) = RunCaptured(
            "control", "promote", "--input", _root,
            "--confirm-ref", fixture.RefId, "--admission", admission,
            "--recipe", candidate.Digest.Value,
            "--through-row", h5.Descriptor.RowId.Value,
            "--max-recipe-row-steps", "64", "--max-new-calls", "0",
            "--max-elapsed-ms", "30000");
        Assert.Equal(0, code);
        Assert.Equal("already-active",
            report.GetProperty("status").GetString());
        Assert.Equal(h5.Descriptor.RowId.Value, report.GetProperty("detail")
            .GetProperty("adoptedThroughRowId").GetProperty("Value").GetString());
        Assert.True(report.GetProperty("detail")
            .GetProperty("candidateTailDebtAtProof").GetBoolean());
        AssertDomainsEqual(beforeLostResponseRecovery, SnapshotDomains());
        Assert.Equal(publishedHead, ReadControlHead(fixture.RefId));
        using RecapGridControlHandle reopened = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(_root, refId, fixture.Admission)).Handle;
        Assert.Equal(candidate.Digest, Assert.IsType<
            RecapGridControlSnapshotResult.Available
        >(reopened.Reader.ReadSnapshot()).Snapshot.Head.ActiveRecipeDigest);

        GridBuildRecipe throughHead = GridBuildRecipe.CreateFull(
            fixture.TimelineHead.TimelineId, h10.Descriptor.RowId,
            fixture.Recipe.Target, candidate.Digest);
        ControlHeadRef nextHead = Assert.IsType<
            RecapGridControlSnapshotResult.Available
        >(reopened.Reader.ReadSnapshot()).Snapshot.Head;
        Assert.IsType<RecapGridControlPutResult.Stored>(
            reopened.Coordinator.PutBuildRecipe(
                nextHead, fixture.TimelineHead, throughHead, h10.Witness));
        string[] headBuild = [
            "build", "--input", _root, "--confirm-ref", fixture.RefId,
            "--recipe", throughHead.Digest.Value,
            "--max-recipe-row-steps", "64", "--max-new-calls", "64",
            "--max-elapsed-ms", "30000", "--routes", fixture.RoutesPath,
            "--connections", fixture.ConnectionsPath
        ];
        Assert.Equal(0, RunWithFactory(factory, headBuild));
        (int headCode, JsonElement headReport) = RunCaptured(
            "control", "promote", "--input", _root,
            "--confirm-ref", fixture.RefId, "--admission", admission,
            "--recipe", throughHead.Digest.Value,
            "--max-recipe-row-steps", "64", "--max-new-calls", "0",
            "--max-elapsed-ms", "30000");
        Assert.Equal(0, headCode);
        JsonElement headDetail = headReport.GetProperty("detail");
        Assert.Equal(fixture.TimelineHead.HeadRowId!.Value.Value, headDetail
            .GetProperty("adoptedThroughRowId").GetProperty("Value").GetString());
        Assert.False(headDetail.GetProperty("candidateTailDebtAtProof")
            .GetBoolean());
    }

    private BuildProgressFixture PrepareBuildProgressFixture(int turns = 2) {
        CreateJournal(turns);
        string refText = InitializeTimeline(maxRawEvents: 64);
        Assert.Equal(0, Run("timeline", "sync", "--input", _root, "--confirm-ref", refText, "--max-rows", "64"));
        RefId refId = RefId.ParseHex(refText).Value;
        TimelineHeadRef head = ReadTimelineHead(refText);
        Assert.True(head.SelectedPathCount > 0);
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV7, GalateaParameters, out RecapGridControlRegistrationBundle? bundle));
        FamilyDefinition family = Assert.Single(bundle!.Families);
        Assert.Equal(2, bundle.Definitions.Count);
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(head.TimelineId, head.HeadRowId,
            BuildTarget.Create(bundle.Definitions.Select(value => new BuildTargetColumn(value.LogicalColumnId, value.Digest))));
        var admission = new RecapGridControlAdmission(RecapGridControlPermission.All, [family.Digest],
            bundle.Definitions.Select(value => value.Capability.CapabilityFingerprint),
            [ContextHeaderCarrier.Observation, ContextHeaderCarrier.Action],
            ["world-understanding", "autobiography"], 64, 1024);
        using (HistoryTimelineReaderHandle timeline = Assert.IsType<HistoryTimelineReaderOpenResult.Opened>(
                   HistoryTimelineMaintenance.OpenReader(_root, refId)).Handle)
        using (RecapGridControlHandle control = Assert.IsType<RecapGridControlOpenResult.Opened>(
                   RecapGridControlFactory.Open(_root, refId, admission)).Handle) {
            HistoryTimelineSelectedRow row = Assert.IsType<HistoryTimelineReaderRowResult.Selected>(
                timeline.Reader.ReadSelectedRow(head, head.HeadRowId!.Value)).Row;
            ControlHeadRef controlHead = Assert.IsType<RecapGridControlSnapshotResult.Available>(control.Reader.ReadSnapshot()).Snapshot.Head;
            Assert.IsType<RecapGridControlOperationResult.Applied>(control.Coordinator.ApplyRegistrationBundle(controlHead,
                head, RecapGridControlOperation.Create("progress-fixture", 1, new string('d', 64)),
                new RecapGridControlRegistrationBundle(bundle.Families, bundle.Definitions, [new(recipe, row.Witness)])));
            ControlHeadRef registered = Assert.IsType<
                RecapGridControlSnapshotResult.Available
            >(control.Reader.ReadSnapshot()).Snapshot.Head;
            Assert.IsType<RecapGridControlActivateResult.Applied>(
                control.Coordinator.CompareExchangeActiveRecipe(
                    registered, head, recipe.Digest,
                    RecapGridControlActivationPurpose.Direct));
        }
        string routesPath = Path.Combine(_root, "routes.json");
        WriteFormattedBuildRoutes(routesPath, RecapGridRouteManifest.Create([
            new RecapGridRouteManifestEntry(new RecapCompletionRouteKey(family.Digest,
                RecapRewriterProtocolV3.RuntimeProtocolId, null), "test", 1, TimeSpan.FromSeconds(30))]));
        string connectionsPath = Path.Combine(_root, "connections.json");
        File.WriteAllText(connectionsPath, """
            {
              "v": 3,
              "connections": [{
                "id": "test", "kind": "test", "modelId": "test-model",
                "completionSurfaceId": "test-v1", "baseAddress": "https://example.invalid"
              }]
            }
            """);
        return new BuildProgressFixture(_root, refText, recipe, head,
            checked((int)head.SelectedPathCount), routesPath, connectionsPath,
            admission);
    }

    private static void WriteFormattedBuildRoutes(string path, RecapGridRouteManifest manifest) {
        using JsonDocument canonical = JsonDocument.Parse(manifest.ToCanonicalBytes());
        File.WriteAllText(path, JsonSerializer.Serialize(canonical.RootElement, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Contains("\n", File.ReadAllText(path));
    }

    private sealed record BuildProgressFixture(string Path, string RefId,
        GridBuildRecipe Recipe, TimelineHeadRef TimelineHead, int RowCount,
        string RoutesPath, string ConnectionsPath,
        RecapGridControlAdmission Admission) {
        internal string[] Arguments(int maximumCalls) => ["build", "--input", Path, "--confirm-ref", RefId,
            "--recipe", Recipe.Digest.Value, "--max-recipe-row-steps", "64", "--max-new-calls", maximumCalls.ToString(),
            "--max-elapsed-ms", "30000", "--routes", RoutesPath, "--connections", ConnectionsPath];

        internal string[] LiveArguments(
            string routes, string connections, string? producerTarget = null
        ) => producerTarget is null ? [
            "build", "--input", Path, "--confirm-ref", RefId, "--live",
            "--max-recipe-row-steps", "64", "--max-new-calls", "64",
            "--max-elapsed-ms", "30000", "--routes", routes,
            "--connections", connections] : [
            "build", "--input", Path, "--confirm-ref", RefId, "--live",
            "--max-recipe-row-steps", "64", "--max-new-calls", "64",
            "--max-elapsed-ms", "30000", "--routes", routes,
            "--connections", connections, "--producer-target", producerTarget];
    }

    private sealed class BuildCaptureWriter : TextWriter {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();
        internal TaskCompletionSource LineWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { lock (_gate) { _text.Append(value); if (value == '\n') { LineWritten.TrySetResult(); } } }
        public override void Write(string? value) { lock (_gate) { _text.Append(value); if (value?.Contains('\n') == true) { LineWritten.TrySetResult(); } } }
        public override void WriteLine(string? value) { lock (_gate) { _text.AppendLine(value); LineWritten.TrySetResult(); } }
        internal string Snapshot() { lock (_gate) { return _text.ToString(); } }
    }

    private sealed class GatedBuildFactory : ICompletionClientFactory {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CreateCount;
        internal int RequestCount;
        internal int DisposeCount;
        internal int FailRequestNumberOnce;
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Interlocked.Increment(ref CreateCount);
            return new Client(this);
        }
        private sealed class Client(GatedBuildFactory owner) : ICompletionClient, IDisposable {
            public string Name => "slow-recap-test";
            public string ApiSpecId => "slow-recap-test-v1";
            public async Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
                CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
                int count = Interlocked.Increment(ref owner.RequestCount);
                if (count == 1) { owner.Entered.TrySetResult(); await owner.Release.Task.WaitAsync(cancellationToken); }
                Assert.Empty(request.PromptPrefix.OutputContract.Tools);
                string workInput = Assert.IsType<string>(
                    Assert.IsType<ObservationMessage>(
                        Assert.Single(request.TailMessages)).Content);
                if (Interlocked.CompareExchange(
                        ref owner.FailRequestNumberOnce, 0, count) == count) {
                    throw new IOException("injected one-shot provider failure");
                }
                using JsonDocument work = JsonDocument.Parse(workInput);
                string column = work.RootElement.GetProperty("logicalColumnId").GetString()!;
                return new CompletionResult(new ActionMessage([new ActionBlock.Text("Synthetic " + column + " recap.")]),
                    new CompletionDescriptor(Name, ApiSpecId, request.ModelId));
            }
            public void Dispose() => Interlocked.Increment(ref owner.DisposeCount);
        }
    }
}
