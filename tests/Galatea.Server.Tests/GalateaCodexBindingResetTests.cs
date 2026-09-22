using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using Fixture = Atelia.Galatea.Server.Tests.GalateaDelegationOperatorRecoveryTests.RecoveryFixture;

namespace Atelia.Galatea.Server.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class GalateaCodexBindingResetTests {
    [Fact]
    public async Task PinnedHomesRebindThroughProductionSidecarWithoutRecreatingStore() {
        string? repository = Environment.GetEnvironmentVariable("ATELIA_CODEX_HOME_CANARY_REPO");
        if (repository is null) { return; }
        string command = Environment.GetEnvironmentVariable("ATELIA_CODEX_HOME_CANARY_COMMAND")
            ?? throw new InvalidOperationException("Pinned canary command is required.");
        Assert.Null(Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME"));
        using var fixture = new Fixture(true);
        Execute(fixture, fixture.Evidence.DispatchId, true);
        string root = Path.GetDirectoryName(fixture.StateDirectory)!;
        string homeA = Path.Combine(root, "codex-a"), homeB = Path.Combine(root, "codex-b");
        foreach (string home in new[] { homeA, homeB }) {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), """
                model = "local-fixture"
                model_provider = "fixture"
                approval_policy = "never"
                sandbox_mode = "read-only"
                [features]
                apps = false
                [model_providers.fixture]
                name = "Provider-free fixture"
                base_url = "http://127.0.0.1:1/v1"
                wire_api = "responses"
                requires_openai_auth = false
                """);
        }
        GalateaDelegateConfig config = GalateaDelegateTestConfiguration.Create(root);
        config = config with { Sidecar = config.Sidecar with {
            NodeCommand = "/usr/bin/node", CodexCommand = command, CodexHome = homeA,
            EntryPoint = Path.Combine(repository, "local-codex-mcp/dist/src/galatea-durable-sidecar.js"),
            RpcTimeoutMs = 15_000
        } };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        async Task<string> Bind(GalateaDelegateConfig selected) {
            using var store = fixture.Reopen();
            await using var transport = new GalateaCodexDurableSidecarClient(selected);
            var driver = new GalateaDurableDelegationDriver(store, transport, fixture.User.HomeDir);
            await driver.PulseAsync(deadline.Token); // Unbound -> Binding
            await driver.PulseAsync(deadline.Token); // Real app-server creates the owned thread.
            var bound = store.ReadSnapshot();
            Assert.Equal(GalateaDelegationRouteState.Bound, bound.Route.State);
            Assert.Equal(GalateaDurableMailState.Queued, bound.Mails.Last().State);
            return bound.Route.ThreadId!;
        }
        string threadA = await Bind(config);
        var before = Snapshot(fixture);
        Assert.Equal("Reset", Execute(fixture, null, true).Outcome);
        string threadB = await Bind(config with { Sidecar = config.Sidecar with { CodexHome = homeB } });
        Assert.NotEqual(threadA, threadB);
        var after = Snapshot(fixture);
        Assert.Equal(before.Owner, after.Owner);
        Assert.Equal(before.Baseline, after.Baseline);
        Assert.Equal(before.Mails, after.Mails);
        Assert.Equal(before.Notices, after.Notices);
        Assert.Equal(before.Captures, after.Captures);
        // This probe stops before dispatch. FIFO delivery is verified by the adjacent test.
    }

    [Fact]
    public async Task ResetSendsQueuedFifoToOneNewThreadWithoutReplayingOldWork() {
        using var fixture = new Fixture(true);
        var old = Snapshot(fixture);
        Assert.Equal("AbandonedAndReset", Execute(fixture, fixture.Evidence.DispatchId, true).Outcome);
        using var store = fixture.Reopen();
        store.CaptureActionBatch(new("ej1:000000000000000a0000000100000000", new string('b', 64), 12,
            "extractor-contract-v1", [new("Codex", null, "another independent task", null, "sent it")],
            GalateaDelegationTestInputs.Sender(store, "Galatea")));
        var queued = store.ReadSnapshot().Mails.Where(value => value.State == GalateaDurableMailState.Queued).ToArray();
        var tasks = queued.Select(value => GalateaDelegationTestInputs.Task(store, value.DispatchId)).ToArray();
        await using var transport = new RecordingTransport();
        var driver = new GalateaDurableDelegationDriver(store, transport, fixture.User.HomeDir);
        for (int index = 0; index < 12 && store.ReadSnapshot().Mails.Any(value => value.State == GalateaDurableMailState.Queued
                 || value.State == GalateaDurableMailState.Accepted); index++) { await driver.PulseAsync(); }
        Assert.Equal(1, transport.BindingCount);
        Assert.Equal(queued.Select(value => value.DispatchId), transport.Starts.Select(value => value.DispatchId));
        Assert.Equal(tasks, transport.Starts.Select(value => value.Task));
        Assert.All(transport.Starts, value => Assert.Equal("new-thread", value.ThreadId));
        Assert.DoesNotContain(transport.Starts, value => value.DispatchId == fixture.Evidence.DispatchId);
        Assert.Equal(old.Baseline, store.ReadSnapshot().Baseline);
        Assert.Equal("RESULT_UNCONFIRMED", store.ReadSnapshot().Mails[0].TerminalCode);
    }

    [Theory]
    [InlineData((int)GalateaDurableMailState.Started)]
    [InlineData((int)GalateaDurableMailState.OutcomeUnknown)]
    [InlineData((int)GalateaDurableMailState.Accepted)]
    public void ActiveRequiresExactDecisionAndPreservesReservationAndLease(int state) {
        using var fixture = new Fixture(true, withActiveLease: true, activeState: (GalateaDurableMailState)state, maximumInboxReplies: 2);
        var before = Snapshot(fixture);
        byte[] digest = Digest(fixture);
        Assert.Equal("ActiveMailRequiresDecision", Execute(fixture, null, true).Outcome);
        Assert.Equal("ActiveDispatchMismatch", Execute(fixture, "wrong", true).Outcome);
        Assert.Equal("WouldAbandonAndReset", Execute(fixture, fixture.Evidence.DispatchId, false).Outcome);
        Assert.Equal(digest, Digest(fixture));
        Assert.Equal("AbandonedAndReset", Execute(fixture, fixture.Evidence.DispatchId, true).Outcome);
        var after = Snapshot(fixture);
        Assert.Equal(GalateaDelegationRouteState.Unbound, after.Route.State);
        Assert.Null(after.Route.ThreadId);
        Assert.Null(after.Route.ActiveDispatchId);
        Assert.Equal(before.StoreRevision + 1, after.StoreRevision);
        Assert.Equal(before.Captures, after.Captures);
        Assert.Equal(before.Baseline, after.Baseline);
        Assert.Equal(before.Mails.Last(), after.Mails.Last());
        Assert.Equal(before.Notices.Single(), after.Notices.First());
        Assert.Equal(JsonSerializer.Serialize(before.ActiveLease), JsonSerializer.Serialize(after.ActiveLease));
        var mail = after.Mails.Single(value => value.DispatchId == fixture.Evidence.DispatchId);
        Assert.Equal(GalateaDurableMailState.TerminalFailed, mail.State);
        Assert.Equal("RESULT_UNCONFIRMED", mail.TerminalCode);
        Assert.Equal(before.Mails[1].RecoveryFailureCount, mail.RecoveryFailureCount);
        var notice = after.Notices.Last();
        Assert.Equal("RESULT_UNCONFIRMED", notice.Code);
        Assert.Equal(GalateaReplyNoticeState.Ready, notice.State);
        Assert.Equal(before.NextCompletionSequence, notice.CompletionSequence);
        Assert.DoesNotContain(notice.NoticeId, after.ActiveLease!.NoticeIds);
        digest = Digest(fixture);
        Assert.Equal("AlreadySatisfied", Execute(fixture, fixture.Evidence.DispatchId, true).Outcome);
        Assert.Equal(digest, Digest(fixture));
        using var store = fixture.Reopen();
        var next = after.Mails.Last();
        store.BeginThreadBinding("new-binding", after.Route.Revision, next.DispatchId, next.Revision);
        Assert.Equal("ActiveDispatchMismatch", GalateaCodexBindingReset.ExecuteOnStore(store, fixture.Evidence.DispatchId, true).Outcome);
        Assert.Equal(GalateaDelegationRouteState.Binding, store.ReadSnapshot().Route.State);
        var late = store.RecordCompletedMail(fixture.Evidence.DispatchId, before.Mails[1].Revision,
            "thread-1", "turn-1", "late remote success");
        Assert.Equal("RESULT_UNCONFIRMED", late.Code);
        Assert.Equal(GalateaDelegationRouteState.Binding, store.ReadSnapshot().Route.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdleBoundAndBindingResetOnlyRouteAndRepeatWithoutWrites(bool binding) {
        using var fixture = new Fixture(true, withActiveLease: true);
        using (var store = fixture.Reopen()) {
            var active = store.ReadSnapshot().Mails[1];
            store.RecordCompletedMail(active.DispatchId, active.Revision, "thread-1", "turn-1", fixture.Final);
            if (binding) {
                GalateaCodexBindingReset.ExecuteOnStore(store, null, true);
                var snapshot = store.ReadSnapshot();
                var queued = snapshot.Mails.Last();
                store.BeginThreadBinding("incomplete-binding", snapshot.Route.Revision, queued.DispatchId, queued.Revision);
            }
        }
        var before = Snapshot(fixture);
        using (var store = fixture.Reopen()) {
            Assert.Throws<GalateaDelegationStoreConflictException>(() => store.ResetIdleCodexBinding(before.Route.Revision - 1));
        }
        byte[] digest = Digest(fixture);
        Assert.Equal("WouldReset", Execute(fixture, null, false).Outcome);
        Assert.Equal(digest, Digest(fixture));
        Assert.Equal("Reset", Execute(fixture, null, true).Outcome);
        var after = Snapshot(fixture);
        Assert.Equal(JsonSerializer.Serialize(before with { Route = after.Route, StoreRevision = after.StoreRevision }),
            JsonSerializer.Serialize(after));
        Assert.Equal(before.Route.Revision + 1, after.Route.Revision);
        Assert.Equal(before.StoreRevision + 1, after.StoreRevision);
        Assert.Equal(GalateaDelegationRouteState.Unbound, after.Route.State);
        digest = Digest(fixture);
        Assert.Equal("AlreadyUnbound", Execute(fixture, null, true).Outcome);
        Assert.Equal(digest, Digest(fixture));
    }

    [Fact]
    public void LockQuarantineMissingStoreAndStaleRevisionRefuse() {
        using var fixture = new Fixture(true);
        using (var store = fixture.Reopen()) {
            Assert.ThrowsAny<IOException>(() => Execute(fixture, null, false));
            var snapshot = store.ReadSnapshot();
            var mail = snapshot.Mails[0];
            Assert.Throws<GalateaDelegationStoreConflictException>(() => store.ResetIdleCodexBinding(snapshot.Route.Revision));
            store.QuarantineActiveMail(mail.DispatchId, mail.Revision, "IDENTITY_CONFLICT");
        }
        var digest = Digest(fixture);
        Assert.Equal("Quarantined", Execute(fixture, fixture.Evidence.DispatchId, true).Outcome);
        Assert.Equal(digest, Digest(fixture));
        string absent = Path.Combine(Path.GetDirectoryName(fixture.StateDirectory)!, "absent");
        Assert.ThrowsAny<IOException>(() => GalateaCodexBindingReset.Execute(
            fixture.User with { DelegationStateDir = absent }, fixture.Route, null, true));
        Assert.False(Directory.Exists(absent));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CommitFailureColdReopenHasOnlyWholeTransition(bool abandon, bool afterCommit) {
        using var fixture = new Fixture(true);
        if (!abandon) {
            using var store = fixture.Reopen();
            var mail = store.ReadSnapshot().Mails[0];
            store.RecordCompletedMail(mail.DispatchId, mail.Revision, "thread-1", "turn-1", fixture.Final);
        }
        var before = Snapshot(fixture);
        void Fail(string _) => throw new IOException("injected");
        var hooks = afterCommit ? new GalateaDelegationStoreTestHooks(AfterCommitBeforeReturn: Fail)
            : new GalateaDelegationStoreTestHooks(BeforeCommit: Fail);
        using (var store = GalateaDelegationSqliteStore.OpenExisting(fixture.StateDirectory, before.Owner, before.Limits, hooks)) {
            if (afterCommit) { GalateaCodexBindingReset.ExecuteOnStore(store, abandon ? fixture.Evidence.DispatchId : null, true); }
            else { Assert.Throws<IOException>(() => GalateaCodexBindingReset.ExecuteOnStore(store, abandon ? fixture.Evidence.DispatchId : null, true)); }
        }
        var after = Snapshot(fixture);
        if (afterCommit) {
            Assert.Equal(GalateaDelegationRouteState.Unbound, after.Route.State);
            Assert.Equal(before.Notices.Count + (abandon ? 1 : 0), after.Notices.Count);
        }
        else { Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after)); }
    }

    [Fact]
    public void CommandUsesStrictConfigAndOutputsMetadataOnly() {
        using var fixture = new Fixture(true);
        string config = fixture.WriteConfigFiles();
        var output = new StringWriter();
        var error = new StringWriter();
        string[] args = ["operator", "reset-codex-binding", "--config", config, "--character", fixture.User.CharacterId];
        Assert.True(GalateaCodexBindingReset.IsInvocation(args));
        Assert.False(GalateaCodexBindingReset.IsInvocation(["operator", "reset-codex-binding-extra"]));
        Assert.Equal(2, GalateaCodexBindingReset.Run(args, output, error));
        Assert.Contains("ActiveMailRequiresDecision", output.ToString());
        Assert.Contains(fixture.Evidence.DispatchId, output.ToString());
        Assert.DoesNotContain(fixture.Final, output.ToString());
        Assert.Equal(0, GalateaCodexBindingReset.Run([..args, "--abandon-active", fixture.Evidence.DispatchId, "--apply"], output, error));
        Assert.Equal(2, GalateaCodexBindingReset.Run([..args, "--apply", "--apply"], output, error));
    }

    [Fact]
    public void OutputFailureAfterCommitCanBeRetriedWithoutDuplicateNotice() {
        using var fixture = new Fixture(true);
        string config = fixture.WriteConfigFiles();
        string[] args = ["operator", "reset-codex-binding", "--config", config, "--character", fixture.User.CharacterId,
            "--abandon-active", fixture.Evidence.DispatchId, "--apply"];
        Assert.Equal(2, GalateaCodexBindingReset.Run(args, new FailingWriter(), new StringWriter()));
        Assert.Single(Snapshot(fixture).Notices);
        byte[] digest = Digest(fixture);
        var output = new StringWriter();
        Assert.Equal(0, GalateaCodexBindingReset.Run(args, output, new StringWriter()));
        Assert.Contains("AlreadySatisfied", output.ToString());
        Assert.Equal(digest, Digest(fixture));
    }

    [Fact]
    public void InvalidCapacityCannotBypassStrictOpen() {
        using var fixture = new Fixture(true, withActiveLease: true, maximumInboxReplies: 2);
        byte[] digest = Digest(fixture);
        Assert.ThrowsAny<Exception>(() => GalateaCodexBindingReset.Execute(fixture.User,
            fixture.Route with { MaximumInboxReplies = 1 }, fixture.Evidence.DispatchId, true));
        Assert.Equal(digest, Digest(fixture));
    }

    private static GalateaCodexBindingResetResult Execute(Fixture fixture, string? abandon, bool apply) =>
        GalateaCodexBindingReset.Execute(fixture.User, fixture.Route, abandon, apply);
    private static GalateaDelegationStateSnapshot Snapshot(Fixture fixture) {
        using var store = fixture.ReopenReadOnly();
        return store.ReadSnapshot();
    }
    private static byte[] Digest(Fixture fixture) => SHA256.HashData(File.ReadAllBytes(
        Path.Combine(fixture.StateDirectory, GalateaDelegationSqliteStore.DatabaseFileName)));

    private sealed class RecordingTransport : IGalateaDurableDelegateTransport {
        internal int BindingCount { get; private set; }
        internal List<GalateaStartDelegateTurnRequest> Starts { get; } = [];
        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(GalateaEnsureDelegateBindingRequest request, CancellationToken ct) {
            BindingCount++;
            return Task.FromResult(new GalateaDelegateBindingEstablished(request.BindingOperationId, "new-thread"));
        }
        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(GalateaStartDelegateTurnRequest request, CancellationToken ct) {
            Starts.Add(request);
            return Task.FromResult(new GalateaDelegateTurnAccepted(request.DispatchId, request.ThreadId, "turn-" + Starts.Count));
        }
        public Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(GalateaInspectDelegateDispatchRequest request, CancellationToken ct) =>
            Task.FromResult<GalateaDelegateDispatchInspection>(new GalateaDelegateDispatchInspection.Completed(
                request.DispatchId, request.ThreadId, "turn-" + Starts.Count, "completed", GalateaDelegateInspectionSource.Persistent));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingWriter : StringWriter {
        public override void WriteLine(string? value) => throw new IOException("injected output failure");
    }
}
