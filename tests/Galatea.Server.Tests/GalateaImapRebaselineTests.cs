using System.Text.Json;
using Atelia.Completion;
using Atelia.EventJournal;
using Atelia.Galatea.Server.Mailbox;
using Atelia.Testing;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaImapRebaselineTests {
    private const string Secret = "SYNTHETIC_REBASELINE_PRIVATE_ARGUMENT";

    [Theory]
    [InlineData("missing-config")]
    [InlineData("relative-config")]
    [InlineData("unknown-option")]
    [InlineData("duplicate-character")]
    [InlineData("missing-option-value")]
    [InlineData("incomplete-apply")]
    [InlineData("zero-validity")]
    [InlineData("negative-revision")]
    [InlineData("overflow-uid")]
    [InlineData("preview-with-apply-values")]
    [InlineData("valid-preview-missing-file")]
    [InlineData("valid-apply-missing-file")]
    [InlineData("valid-max-cursor-missing-file")]
    [InlineData("removed-uidnext-option")]
    public async Task InvalidOrUnavailableOfflineInvocationFailsWithoutEchoingPrivateArgumentsOrCreatingState(string scenario) {
        string directory = Directory.CreateTempSubdirectory("atelia-imap-rebaseline-tests-").FullName;
        try {
            string missingConfig = Path.Combine(directory, Secret + ".json");
            string[] prefix = ["operator", "rebaseline-imap", "--config", missingConfig, "--character", Secret];
            string[] values = ["--expected-validity", "41", "--expected-cursor", "0",
                "--expected-revision", "0", "--new-validity", "42", "--new-cursor", "0"];
            string[] args = scenario switch {
                "missing-config" => ["operator", "rebaseline-imap", "--character", Secret],
                "relative-config" => ["operator", "rebaseline-imap", "--config", Secret + ".json", "--character", "alice"],
                "unknown-option" => [.. prefix, "--password", Secret],
                "duplicate-character" => [.. prefix, "--character", "alice"],
                "missing-option-value" => [.. prefix, "--new-cursor"],
                "incomplete-apply" => [.. prefix, "--apply", "--expected-validity", "41"],
                "zero-validity" => [.. prefix, "--apply", .. Change(values, "--new-validity", "0")],
                "negative-revision" => [.. prefix, "--apply", .. Change(values, "--expected-revision", "-1")],
                "overflow-uid" => [.. prefix, "--apply", .. Change(values, "--new-cursor", "4294967296")],
                "preview-with-apply-values" => [.. prefix, .. values],
                "valid-preview-missing-file" => prefix,
                "valid-apply-missing-file" => [.. prefix, "--apply", .. values],
                "valid-max-cursor-missing-file" => [.. prefix, "--apply", .. Change(values, "--new-cursor", "4294967295")],
                "removed-uidnext-option" => [.. prefix, "--apply", "--expected-validity", "41", "--expected-cursor", "0",
                    "--expected-revision", "0", "--new-validity", "42", "--new-uidnext", "1"],
                _ => throw new ArgumentException("Unknown fixture scenario.", nameof(scenario))
            };
            using var output = new StringWriter();
            using var error = new StringWriter();

            int exitCode = await GalateaImapRebaseline.RunAsync(args, output, error);

            Assert.Equal(2, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.StartsWith("IMAP rebaseline failed: exceptionType=", error.ToString());
            Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(directory, error.ToString(), StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            // Valid command shapes stop at the missing config file, before
            // any host, store or IMAP network connection can be constructed.
            if (scenario.StartsWith("valid-", StringComparison.Ordinal)) {
                Assert.Contains("System.IO.FileNotFoundException", error.ToString(), StringComparison.Ordinal);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(true, 100u)]
    [InlineData(false, 100u)]
    [InlineData(false, 0u)]
    [InlineData(false, uint.MaxValue)]
    public async Task RealOwnerPreviewAndExactApplyUseTheSameScanHorizonAndRetainOldFacts(bool hasNativeNext, uint newCursor) {
        using var fixture = new OperatorFixture();
        byte[] before = File.ReadAllBytes(fixture.DatabasePath);
        var previewConnection = new FakeConnection(42, hasNativeNext ? newCursor + 1 : null, newCursor);
        var previewTransport = new FakeTransport(previewConnection);
        using var preview = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, await GalateaImapRebaseline.RunAsync(fixture.PreviewArguments, preview, error, previewTransport));

        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains($"expectedValidity=41, expectedCursor=8, expectedRevision={fixture.Expected.Revision}", preview.ToString());
        Assert.Contains($"newValidity=42, newCursor={newCursor}", preview.ToString());
        Assert.DoesNotContain("newUidNext", preview.ToString());
        Assert.DoesNotContain(Secret, preview.ToString());
        Assert.DoesNotContain(fixture.Root, preview.ToString());
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Equal(1, previewConnection.HorizonReadCount);
        Assert.True(previewConnection.Disposed);
        Assert.Equal(1, previewTransport.OpenCount);

        var applyConnection = new FakeConnection(42, hasNativeNext ? newCursor + 1 : null, newCursor);
        using var applied = new StringWriter();
        Assert.Equal(0, await GalateaImapRebaseline.RunAsync(fixture.ApplyArguments(42, newCursor), applied, error,
            new FakeTransport(applyConnection)));
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains($"uidValidity=42, cursor={newCursor}, revision={fixture.Expected.Revision + 1}", applied.ToString());
        Assert.Equal(1, applyConnection.HorizonReadCount);
        Assert.True(applyConnection.Disposed);
        using GalateaDelegationSqliteStore inspected = fixture.OpenReadOnly();
        GalateaImapCheckpointSnapshot checkpoint = inspected.ReadImapCheckpoint(fixture.Reference)!;
        Assert.Equal(42u, checkpoint.UidValidity);
        Assert.Equal(newCursor, checkpoint.ScannedThroughUid);
        Assert.Null(checkpoint.BlockedCode);
        Assert.Equal(fixture.Expected.Revision + 1, checkpoint.Revision);
        Assert.Equal(fixture.OldMail, inspected.ReadExternalMail(fixture.OldMail.InboxId));
        Assert.Equal(fixture.SmtpAttempt, Assert.Single(inspected.ReadSnapshot().SmtpMailOutboxes));
        Assert.Equal(GalateaSmtpMailState.Attempting, fixture.SmtpAttempt.State);
        // The command never creates a SessionJournal repository or starts a model.
        Assert.False(Directory.Exists(fixture.SessionDirectory));
    }

    [Fact]
    public async Task MissingNativeNextMayBecomeConsistentWhileReadingHorizon() {
        using var fixture = new OperatorFixture();
        var connection = new FakeConnection(42, null, 100) { NativeNextAfterHorizon = 101 };
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, await GalateaImapRebaseline.RunAsync(fixture.ApplyArguments(42, 100), output, error,
            new FakeTransport(connection)));

        Assert.Equal(string.Empty, error.ToString());
        Assert.Equal(1, connection.HorizonReadCount);
        Assert.True(connection.Disposed);
        using GalateaDelegationSqliteStore inspected = fixture.OpenReadOnly();
        Assert.Equal(100u, inspected.ReadImapCheckpoint(fixture.Reference)!.ScannedThroughUid);
        Assert.Equal(fixture.SmtpAttempt, Assert.Single(inspected.ReadSnapshot().SmtpMailOutboxes));
    }

    [Theory]
    [InlineData("expected-validity")]
    [InlineData("expected-cursor")]
    [InlineData("expected-revision")]
    [InlineData("new-validity")]
    [InlineData("new-cursor")]
    [InlineData("wrong-block")]
    [InlineData("same-validity")]
    [InlineData("namespace-changed")]
    [InlineData("uidnext-regressed")]
    [InlineData("native-next-below-horizon")]
    [InlineData("native-next-equals-horizon")]
    public async Task StalePreviewOrUnrelatedBlockNeverWritesCheckpointOrRecoversSmtp(string mismatch) {
        using var fixture = new OperatorFixture(mismatch == "wrong-block" ? "IMAP_UIDNEXT_REGRESSED" : "IMAP_UIDVALIDITY_CHANGED");
        string[] args = fixture.ApplyArguments(42, 100);
        if (mismatch is "expected-validity" or "expected-cursor" or "expected-revision" or "new-validity" or "new-cursor") {
            args = Change(args, "--" + mismatch, mismatch == "expected-revision" ? "999" : "99");
        }
        var connection = new FakeConnection(mismatch == "same-validity" ? 41u : 42u,
            mismatch == "uidnext-regressed" ? 101u : null, 100) {
            ChangeNamespaceAfterHorizon = mismatch == "namespace-changed",
            RegressNativeNextAfterHorizon = mismatch == "uidnext-regressed",
            NativeNextAfterHorizon = mismatch == "native-next-below-horizon" ? 99u
                : mismatch == "native-next-equals-horizon" ? 100u : null
        };
        if (mismatch == "same-validity") { args = Change(args, "--new-validity", "41"); }
        byte[] before = File.ReadAllBytes(fixture.DatabasePath);
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(2, await GalateaImapRebaseline.RunAsync(args, output, error, new FakeTransport(connection)));

        Assert.Equal(string.Empty, output.ToString());
        Assert.StartsWith("IMAP rebaseline failed: exceptionType=", error.ToString());
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.DoesNotContain(fixture.Root, error.ToString());
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Equal(1, connection.HorizonReadCount);
        Assert.True(connection.Disposed);
        using GalateaDelegationSqliteStore inspected = fixture.OpenReadOnly();
        Assert.Equal(fixture.Expected, inspected.ReadImapCheckpoint(fixture.Reference));
        Assert.Equal(fixture.SmtpAttempt, Assert.Single(inspected.ReadSnapshot().SmtpMailOutboxes));
    }

    private sealed class OperatorFixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "atelia-imap-operator-" + Guid.NewGuid().ToString("N"));
        internal string ConfigPath => Path.Combine(Root, "config.json");
        internal string StoreDirectory { get; }
        internal string SessionDirectory { get; }
        internal string DatabasePath => Path.Combine(StoreDirectory, GalateaDelegationSqliteStore.DatabaseFileName);
        internal GalateaDelegationStoreOwner Owner { get; }
        internal GalateaDelegationStoreLimits Limits { get; }
        internal string Reference { get; }
        internal GalateaImapCheckpointSnapshot Expected { get; }
        internal GalateaExternalMailInboxSnapshot OldMail { get; }
        internal GalateaSmtpMailOutboxSnapshot SmtpAttempt { get; }
        internal string[] PreviewArguments => ["operator", "rebaseline-imap", "--config", ConfigPath, "--character", "alice"];

        internal OperatorFixture(string blockCode = "IMAP_UIDVALIDITY_CHANGED") {
            TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(Root);
            TestDirectorySafety.CreateDirectoryNew(Root);
            GalateaTestHost.WriteDelegatesFile(Root);
            GalateaTestHost.WriteConnectionsFile(Path.Combine(Root, GalateaConfigLoader.ConnectionsFileName), [
                new CompletionConnectionConfig("test", "openai-chat", "model-a", "openai-chat/strict", "http://localhost:8000/", ApiKey: "synthetic-key")
            ]);
            var email = new GalateaEmailAccount("alice@example.test", Secret, "smtp.example.test", 465,
                "implicit", new GalateaImapAccount("imap.example.test", 993, "implicit"));
            var rootConfig = new GalateaRootFileConfig(GalateaStrictConfigReader.CurrentConfigVersion,
                [new GalateaCharacterFileConfig("alice", "Alice", "sessions/alice", "delegation-state/alice", "character-memory/alice",
                    Directory.CreateDirectory(Path.Combine(Root, "homes/alice")).FullName, GalateaSessionProvisioning.ExistingOnly,
                    "test", [new GalateaCharacterConnectionOption("test", "", "")], CharacterContextTemplate: "inline ${characterName}", Email: email)],
                [new GalateaPlayerFileConfig("player", "Player", "pw")],
                new GalateaRuntimeFileConfig(RecapGrid: new GalateaRecapGridFileConfig(new GalateaRecapGridMaintenanceFileConfig("test", 1, 900_000))));
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(rootConfig, GalateaJsonContext.Default.GalateaRootFileConfig));
            GalateaConfig loaded = GalateaConfigLoader.Load(ConfigPath);
            GalateaCharacterConfig character = Assert.Single(loaded.Characters);
            StoreDirectory = character.DelegationStateDir;
            SessionDirectory = character.SessionDir;
            Owner = new(character.CharacterId, GalateaDelegationSupervisor.CreateSessionRepositoryId(SessionDirectory));
            Limits = GalateaDelegationSupervisor.CreateLimits(loaded.Delegates.CodexRoute);
            Reference = GalateaImapConfig.AccountReference(character.CharacterId, loaded.Imap.Accounts[character.CharacterId]);
            Directory.CreateDirectory(Path.GetDirectoryName(StoreDirectory)!);
            using GalateaDelegationSqliteStore store = GalateaDelegationSqliteStore.CreateNew(StoreDirectory, Owner,
                new(new EventJournalPhysicalAppendFrontier(1, 4), null), Limits);
            GalateaImapCheckpointSnapshot checkpoint = store.EstablishImapBaseline(Reference, 41, 7, DateTimeOffset.UnixEpoch.AddDays(1));
            OldMail = store.AcceptImapMail(checkpoint, 8, "Alice", "friend@example.test", "old subject", "retained old body", 0)!;
            Expected = store.BlockImapCheckpoint(store.ReadImapCheckpoint(Reference)!, blockCode);
            store.CaptureActionBatch(new("ej1:00000000000000640000000100000000", new string('a', 64), 12, "operator-tests",
                [new("friend@example.test", null, "outgoing body", null, "sent")], new GalateaSenderSnapshot("character", "alice", "Alice"),
                SmtpSenderAccountReference: GalateaSmtpConfig.AccountReference("alice", email)));
            SmtpAttempt = store.ClaimPendingSmtpMail()!;
        }

        internal string[] ApplyArguments(uint validity, uint cursor) => [.. PreviewArguments, "--apply",
            "--expected-validity", Expected.UidValidity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--expected-cursor", Expected.ScannedThroughUid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--expected-revision", Expected.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--new-validity", validity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--new-cursor", cursor.ToString(System.Globalization.CultureInfo.InvariantCulture)];

        internal GalateaDelegationSqliteStore OpenReadOnly() => GalateaDelegationSqliteStore.OpenExistingReadOnly(StoreDirectory, Owner, Limits);
        public void Dispose() => TestDirectorySafety.DeleteOwnedTreeNoFollow(Root);
    }

    private sealed class FakeTransport(FakeConnection connection) : IGalateaImapTransport {
        internal int OpenCount { get; private set; }
        public Task<IGalateaImapConnection> OpenAsync(GalateaEmailAccount account, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("alice@example.test", account.Address);
            Assert.Equal(Secret, account.AuthorizationCode);
            OpenCount++;
            return Task.FromResult<IGalateaImapConnection>(connection);
        }
    }

    private sealed class FakeConnection(uint validity, uint? nativeNext, uint upper) : IGalateaImapConnection {
        public uint UidValidity { get; private set; } = validity;
        public uint? UidNext { get; private set; } = nativeNext;
        internal int HorizonReadCount { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool ChangeNamespaceAfterHorizon { get; init; }
        internal bool RegressNativeNextAfterHorizon { get; init; }
        internal uint? NativeNextAfterHorizon { get; init; }
        public Task<uint> ReadScanUpperUidAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            HorizonReadCount++;
            if (ChangeNamespaceAfterHorizon) { UidValidity++; }
            if (RegressNativeNextAfterHorizon) { UidNext--; }
            if (NativeNextAfterHorizon is { } newNativeNext) { UidNext = newNativeNext; }
            return Task.FromResult(upper);
        }
        public Task<IReadOnlyList<uint>> SearchUidsAsync(uint first, uint last, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Operator must only read the scan horizon.");
        public Task<GalateaImapRawMessage> ReadRawAsync(uint uid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Operator must never fetch message content.");
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private static string[] Change(string[] arguments, string option, string value) {
        string[] changed = (string[])arguments.Clone();
        int index = Array.IndexOf(changed, option);
        Assert.True(index >= 0);
        changed[index + 1] = value;
        return changed;
    }
}
