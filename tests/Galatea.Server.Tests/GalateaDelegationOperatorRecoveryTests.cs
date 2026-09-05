using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.EventJournal;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.Testing;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

[SupportedOSPlatform("linux")]
public sealed class GalateaDelegationOperatorRecoveryTests {
    [Fact]
    public void Execute_DryRunIsBytePreservingAndLeavesAcceptedState() {
        using var fixture = new RecoveryFixture(closeStore: true);
        byte[] before = DatabaseDigest(fixture.StateDirectory);

        GalateaCodexCompletionRecoveryResult result =
            GalateaDelegationOperatorRecovery.Execute(
                fixture.User,
                fixture.Route,
                fixture.Evidence,
                apply: false
            );

        Assert.Equal(
            GalateaCodexCompletionRecoveryOutcome.DryRunReady,
            result.Outcome
        );
        Assert.Equal(before, DatabaseDigest(fixture.StateDirectory));
        Assert.False(File.Exists(DatabasePath(fixture.StateDirectory) + "-journal"));
        using GalateaDelegationSqliteStore reopened = fixture.Reopen();
        GalateaDelegationStateSnapshot snapshot = reopened.ReadSnapshot();
        Assert.Equal(
            GalateaDurableMailState.Accepted,
            snapshot.Mails[0].State
        );
        Assert.Equal(
            fixture.Evidence.DispatchId,
            snapshot.Route.ActiveDispatchId
        );
        Assert.Empty(snapshot.Notices);
    }

    [Fact]
    public void Execute_ApplyIsExactAtomicAndRerunIsZeroWrite() {
        using var fixture = new RecoveryFixture(closeStore: true);
        using (GalateaDelegationSqliteStore beforeStore =
               fixture.ReopenReadOnly()) {
            GalateaDelegationStateSnapshot before = beforeStore.ReadSnapshot();
            Assert.Equal(2, before.Mails.Count);
            Assert.Equal(GalateaDurableMailState.Queued, before.Mails[1].State);
        }

        GalateaCodexCompletionRecoveryResult applied =
            GalateaDelegationOperatorRecovery.Execute(
                fixture.User,
                fixture.Route,
                fixture.Evidence,
                apply: true
            );

        Assert.Equal(
            GalateaCodexCompletionRecoveryOutcome.Applied,
            applied.Outcome
        );
        using (GalateaDelegationSqliteStore afterStore =
               fixture.ReopenReadOnly()) {
            GalateaDelegationStateSnapshot after = afterStore.ReadSnapshot();
            GalateaOutboundMailSnapshot completed = after.Mails[0];
            Assert.Equal(
                GalateaDurableMailState.TerminalCompleted,
                completed.State
            );
            Assert.Equal(fixture.Evidence.ThreadId, completed.AcceptedThreadId);
            Assert.Equal(fixture.Evidence.TurnId, completed.AcceptedTurnId);
            Assert.Equal(fixture.Evidence.FinalSha256,
                completed.TerminalFinalSha256);
            Assert.Null(after.Route.ActiveDispatchId);
            GalateaReplyNoticeSnapshot notice = Assert.Single(after.Notices);
            Assert.Equal(GalateaReplyNoticeKind.Reply, notice.Kind);
            Assert.Equal(GalateaReplyNoticeState.Ready, notice.State);
            Assert.Equal(fixture.Final, notice.Body);
            Assert.Equal(GalateaDurableMailState.Queued, after.Mails[1].State);
            Assert.Equal(fixture.QueuedMailBefore, after.Mails[1]);
        }

        byte[] beforeRerun = DatabaseDigest(fixture.StateDirectory);
        GalateaCodexCompletionRecoveryResult repeated =
            GalateaDelegationOperatorRecovery.Execute(
                fixture.User,
                fixture.Route,
                fixture.Evidence,
                apply: true
            );
        Assert.Equal(
            GalateaCodexCompletionRecoveryOutcome.AlreadyApplied,
            repeated.Outcome
        );
        Assert.Equal(beforeRerun, DatabaseDigest(fixture.StateDirectory));
        Assert.False(File.Exists(DatabasePath(fixture.StateDirectory) + "-journal"));
    }

    [Fact]
    public void Execute_ConflictingEvidenceNeverCallsTerminalConflictPath() {
        using var fixture = new RecoveryFixture(closeStore: true);
        _ = GalateaDelegationOperatorRecovery.Execute(
            fixture.User,
            fixture.Route,
            fixture.Evidence,
            apply: true
        );
        GalateaCodexCompletionRecoveryEvidence conflict = Evidence(
            fixture.Evidence,
            "different final"
        );
        byte[] before = DatabaseDigest(fixture.StateDirectory);

        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationOperatorRecovery.Execute(
                fixture.User,
                fixture.Route,
                conflict,
                apply: true
            ));

        Assert.Equal(before, DatabaseDigest(fixture.StateDirectory));
        using GalateaDelegationSqliteStore store = fixture.ReopenReadOnly();
        Assert.Equal(
            GalateaDelegationRouteState.Bound,
            store.ReadSnapshot().Route.State
        );
    }

    [Fact]
    public void Execute_WrongTaskOrWrongReconcileCodeIsZeroWrite() {
        using var fixture = new RecoveryFixture(closeStore: true);
        byte[] before = DatabaseDigest(fixture.StateDirectory);
        GalateaCodexCompletionRecoveryEvidence wrongTask =
            fixture.Evidence with { TaskSha256 = new string('0', 64) };

        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationOperatorRecovery.Execute(
                fixture.User,
                fixture.Route,
                wrongTask,
                apply: true
            ));
        Assert.Equal(before, DatabaseDigest(fixture.StateDirectory));

        using (GalateaDelegationSqliteStore writable = fixture.Reopen()) {
            GalateaDelegationStateSnapshot snapshot = writable.ReadSnapshot();
            GalateaOutboundMailSnapshot accepted = snapshot.Mails[0];
            _ = writable.ConfirmAcceptedMailRunning(
                accepted.DispatchId,
                accepted.Revision,
                fixture.Evidence.ThreadId,
                fixture.Evidence.TurnId
            );
        }
        before = DatabaseDigest(fixture.StateDirectory);
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationOperatorRecovery.Execute(
                fixture.User,
                fixture.Route,
                fixture.Evidence,
                apply: true
            ));
        Assert.Equal(before, DatabaseDigest(fixture.StateDirectory));
    }

    [Fact]
    public void Execute_WrongAcceptedIdentityIsZeroWrite() {
        using var fixture = new RecoveryFixture(closeStore: true);
        byte[] before = DatabaseDigest(fixture.StateDirectory);

        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationOperatorRecovery.Execute(
                fixture.User,
                fixture.Route,
                fixture.Evidence with { TurnId = "wrong-turn" },
                apply: true
            ));

        Assert.Equal(before, DatabaseDigest(fixture.StateDirectory));
    }

    [Fact]
    public void Execute_HeldLifetimeLockRefusesWithoutWriting() {
        using var fixture = new RecoveryFixture(closeStore: false);
        byte[] before = DatabaseDigest(fixture.StateDirectory);

        Assert.ThrowsAny<IOException>(() =>
            GalateaDelegationOperatorRecovery.Execute(
                fixture.User,
                fixture.Route,
                fixture.Evidence,
                apply: true
            ));

        Assert.Equal(before, DatabaseDigest(fixture.StateDirectory));
    }

    [Theory]
    [InlineData("{\"v\":1,\"v\":1}")]
    [InlineData("{\"v\":1,\"kind\":\"codex-turn-completed\",\"extra\":1}")]
    [InlineData("[]")]
    public void DecodeEvidence_RejectsNonClosedShapes(string json) {
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationOperatorRecovery.DecodeEvidence(
                Encoding.UTF8.GetBytes(json)
            ));
    }

    [Fact]
    public void DecodeEvidence_DecodesCanonicalBase64WithoutChangingBytes() {
        const string task = "task";
        const string final = "reply\n终";
        byte[] taskBytes = Encoding.UTF8.GetBytes(task);
        byte[] finalBytes = Encoding.UTF8.GetBytes(final);
        string json = $$"""
            {
              "v": 1,
              "kind": "codex-turn-completed",
              "userId": "gpt",
              "dispatchId": "gd1-{{new string('a', 64)}}",
              "threadId": "thread",
              "turnId": "turn",
              "taskUtf8Bytes": {{taskBytes.Length}},
              "taskSha256": "{{Sha(taskBytes)}}",
              "finalUtf8Bytes": {{finalBytes.Length}},
              "finalSha256": "{{Sha(finalBytes)}}",
              "finalUtf8Base64": "{{Convert.ToBase64String(finalBytes)}}"
            }
            """;

        GalateaCodexCompletionRecoveryEvidence decoded =
            GalateaDelegationOperatorRecovery.DecodeEvidence(
                Encoding.UTF8.GetBytes(json)
            );

        Assert.Equal(final, decoded.Final);
        Assert.Equal(finalBytes.Length, decoded.FinalUtf8Bytes);
        Assert.Equal(Sha(finalBytes), decoded.FinalSha256);
    }

    [Fact]
    public void DecodeEvidence_RejectsBase64Utf8HashAndCountDrift() {
        const string final = "final";
        byte[] finalBytes = Encoding.UTF8.GetBytes(final);
        GalateaCodexCompletionRecoveryEvidence evidence = Evidence(
            new GalateaCodexCompletionRecoveryEvidence(
                1,
                GalateaDelegationOperatorRecovery.EvidenceKind,
                "gpt",
                "gd1-" + new string('a', 64),
                "thread",
                "turn",
                4,
                Sha("task"u8),
                finalBytes.Length,
                Sha(finalBytes),
                final
            ),
            final
        );

        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationOperatorRecovery.DecodeEvidence(
                EncodeEvidence(evidence, finalBase64: "ZmluYWw= ")
            ));
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationOperatorRecovery.DecodeEvidence(
                EncodeEvidence(evidence, finalBase64: "/w==")
            ));
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationOperatorRecovery.DecodeEvidence(
                EncodeEvidence(evidence with {
                    FinalSha256 = evidence.FinalSha256.ToUpperInvariant()
                })
            ));
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationOperatorRecovery.DecodeEvidence(
                EncodeEvidence(evidence with {
                    FinalUtf8Bytes = evidence.FinalUtf8Bytes + 1
                })
            ));
    }

    [Fact]
    public void EvidencePath_RequiresOwnerOnlyModeAndRejectsSymlink() {
        using var fixture = new RecoveryFixture(closeStore: true);
        string evidencePath = fixture.WriteEvidenceFile();
        File.SetUnixFileMode(
            evidencePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead
        );
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationOperatorRecovery.RequireEvidenceFilePath(
                evidencePath
            ));

        File.SetUnixFileMode(
            evidencePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
        );
        Assert.Equal(
            evidencePath,
            GalateaDelegationOperatorRecovery.RequireEvidenceFilePath(
                evidencePath
            )
        );
        string linkPath = evidencePath + ".link";
        File.CreateSymbolicLink(linkPath, evidencePath);
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationOperatorRecovery.RequireEvidenceFilePath(
                linkPath
            ));
    }

    [Fact]
    public void Run_HappyDryRunLoadsConfigAndNeverPrintsFinal() {
        using var fixture = new RecoveryFixture(closeStore: true);
        string configPath = fixture.WriteConfigFiles();
        string evidencePath = fixture.WriteEvidenceFile();
        var output = new StringWriter();
        var error = new StringWriter();

        int exitCode = GalateaDelegationOperatorRecovery.Run(
            [
                "operator",
                GalateaDelegationOperatorRecovery.CommandName,
                "--config",
                configPath,
                "--evidence",
                evidencePath
            ],
            output,
            error
        );

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Contains("outcome=DryRunReady", output.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Final, output.ToString(),
            StringComparison.Ordinal);
        using GalateaDelegationSqliteStore store = fixture.ReopenReadOnly();
        Assert.Equal(GalateaDurableMailState.Accepted,
            store.ReadSnapshot().Mails[0].State);
    }

    [Fact]
    public void Program_OperatorBranchReturnsWithoutStartingWebHost() {
        string assembly = typeof(Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("operator");
        start.ArgumentList.Add(GalateaDelegationOperatorRecovery.CommandName);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start Galatea CLI.");
        bool exited = process.WaitForExit(10_000);
        if (!exited) {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();

        Assert.True(exited);
        Assert.Equal(2, process.ExitCode);
        Assert.Contains("Usage: Galatea.Server operator", error,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Now listening", output + error,
            StringComparison.OrdinalIgnoreCase);
    }

    private static GalateaCodexCompletionRecoveryEvidence Evidence(
        GalateaCodexCompletionRecoveryEvidence template,
        string final
    ) {
        byte[] bytes = Encoding.UTF8.GetBytes(final);
        return template with {
            FinalUtf8Bytes = bytes.Length,
            FinalSha256 = Sha(bytes),
            Final = final
        };
    }

    private static byte[] DatabaseDigest(string stateDirectory) =>
        SHA256.HashData(File.ReadAllBytes(DatabasePath(stateDirectory)));

    private static string DatabasePath(string stateDirectory) => Path.Combine(
        stateDirectory,
        GalateaDelegationSqliteStore.DatabaseFileName
    );

    private static string Sha(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] EncodeEvidence(
        GalateaCodexCompletionRecoveryEvidence evidence,
        string? finalBase64 = null
    ) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> {
        ["v"] = evidence.Version,
        ["kind"] = evidence.Kind,
        ["userId"] = evidence.UserId,
        ["dispatchId"] = evidence.DispatchId,
        ["threadId"] = evidence.ThreadId,
        ["turnId"] = evidence.TurnId,
        ["taskUtf8Bytes"] = evidence.TaskUtf8Bytes,
        ["taskSha256"] = evidence.TaskSha256,
        ["finalUtf8Bytes"] = evidence.FinalUtf8Bytes,
        ["finalSha256"] = evidence.FinalSha256,
        ["finalUtf8Base64"] = finalBase64
            ?? Convert.ToBase64String(Encoding.UTF8.GetBytes(evidence.Final))
    });

    private sealed class RecoveryFixture : IDisposable {
        private readonly string _root;
        private GalateaDelegationSqliteStore? _store;

        internal RecoveryFixture(bool closeStore) {
            _root = Path.Combine(
                Path.GetTempPath(),
                "atelia-galatea-operator-recovery-"
                    + Guid.NewGuid().ToString("N")
            );
            TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(_root);
            TestDirectorySafety.CreateDirectoryNew(_root);
            string sessionDirectory = Path.Combine(_root, "session");
            TestDirectorySafety.CreateDirectoryNew(sessionDirectory);
            StateDirectory = Path.Combine(_root, "delegation");
            Route = GalateaDelegateTestConfiguration.Create(_root).CodexRoute;
            User = new GalateaUserConfig(
                "gpt",
                "password",
                new GalateaCharacterName("Galatea"),
                new GalateaPlayerName("Player"),
                sessionDirectory,
                StateDirectory,
                Path.Combine(_root, "character-memory"),
                GalateaSessionProvisioning.ExistingOnly,
                "system prompt"
            );
            GalateaDelegationStoreLimits limits =
                GalateaDelegationSupervisor.CreateLimits(Route);
            var owner = new GalateaDelegationStoreOwner(
                User.UserId,
                GalateaDelegationSupervisor.CreateSessionRepositoryId(
                    User.SessionDir
                ),
                GalateaDelegationDurableContract
                    .CreateRoutePolicyFingerprint(Route)
            );
            string selectedHead = Address(2);
            EventAddress address = EventAddressTextCodec.Parse(selectedHead);
            var baseline = new GalateaDelegationStoreBaseline(
                new EventJournalPhysicalAppendFrontier(
                    address.SegmentNumber,
                    address.Ticket.EndOffsetExclusive
                ),
                selectedHead
            );
            _store = GalateaDelegationSqliteStore.CreateNew(
                StateDirectory,
                owner,
                baseline,
                limits
            );
            const string task = "Please complete exact recovery task.";
            GalateaDelegationCaptureResult capture = _store.CaptureActionBatch(
                new GalateaDelegationCaptureRequest(
                    Address(9),
                    new string('a', 64),
                    VisibleActionUtf8Bytes: 12,
                    "extractor-contract-v1",
                    [
                        Mail(task),
                        Mail("queued untouched")
                    ]
                )
            );
            GalateaDelegationStateSnapshot initial = _store.ReadSnapshot();
            GalateaRouteBindingSnapshot binding = _store.BeginThreadBinding(
                "bind-op",
                initial.Route.Revision
            );
            GalateaRouteBindingSnapshot bound = _store.CompleteThreadBinding(
                "bind-op",
                "thread-1",
                binding.Revision
            );
            GalateaOutboundMailSnapshot started = _store.StartQueuedMail(
                capture.DispatchIds[0],
                initial.Mails[0].Revision,
                bound.Revision
            );
            GalateaOutboundMailSnapshot accepted = _store.RecordMailAccepted(
                started.DispatchId,
                started.Revision,
                "thread-1",
                "turn-1"
            );
            accepted = _store.RecordMailPollMiss(
                accepted.DispatchId,
                accepted.Revision,
                GalateaDelegateDispatchInspection.AcceptedTurnNotVisible
                    .FailureCode,
                nowUnixTimeMilliseconds: 1_000
            );
            QueuedMailBefore = _store.ReadSnapshot().Mails[1];
            Final = "exact final reply\nwith UTF-8: 终";
            byte[] taskBytes = Encoding.UTF8.GetBytes(task);
            byte[] finalBytes = Encoding.UTF8.GetBytes(Final);
            Evidence = new GalateaCodexCompletionRecoveryEvidence(
                GalateaDelegationOperatorRecovery.EvidenceVersion,
                GalateaDelegationOperatorRecovery.EvidenceKind,
                User.UserId,
                accepted.DispatchId,
                "thread-1",
                "turn-1",
                taskBytes.Length,
                Sha(taskBytes),
                finalBytes.Length,
                Sha(finalBytes),
                Final
            );
            if (closeStore) {
                _store.Dispose();
                _store = null;
            }
        }

        internal string StateDirectory { get; }
        internal GalateaUserConfig User { get; }
        internal GalateaDelegateRouteConfig Route { get; }
        internal GalateaCodexCompletionRecoveryEvidence Evidence { get; }
        internal GalateaOutboundMailSnapshot QueuedMailBefore { get; }
        internal string Final { get; }

        internal GalateaDelegationSqliteStore Reopen() =>
            GalateaDelegationSqliteStore.OpenExisting(
                StateDirectory,
                new GalateaDelegationStoreOwner(
                    User.UserId,
                    GalateaDelegationSupervisor.CreateSessionRepositoryId(
                        User.SessionDir
                    ),
                    GalateaDelegationDurableContract
                        .CreateRoutePolicyFingerprint(Route)
                ),
                GalateaDelegationSupervisor.CreateLimits(Route)
            );

        internal GalateaDelegationSqliteStore ReopenReadOnly() =>
            GalateaDelegationSqliteStore.OpenExistingReadOnly(
                StateDirectory,
                new GalateaDelegationStoreOwner(
                    User.UserId,
                    GalateaDelegationSupervisor.CreateSessionRepositoryId(
                        User.SessionDir
                    ),
                    GalateaDelegationDurableContract
                        .CreateRoutePolicyFingerprint(Route)
                ),
                GalateaDelegationSupervisor.CreateLimits(Route)
            );

        internal string WriteEvidenceFile() {
            string path = Path.Combine(_root, "completed-evidence.json");
            File.WriteAllBytes(path, EncodeEvidence(Evidence));
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
            );
            return path;
        }

        internal string WriteConfigFiles() {
            RecapGridAgentControlProfile profile = CreateProfile();
            File.WriteAllBytes(
                Path.Combine(_root, "profile.json"),
                profile.ToCanonicalBytes()
            );
            var users = new GalateaUsersFileConfig(
                GalateaStrictConfigReader.CurrentConfigVersion,
                [new GalateaUserFileConfig(
                    User.UserId,
                    User.Password,
                    User.CharacterName.Value,
                    User.PlayerName.Value,
                    User.SessionDir,
                    User.DelegationStateDir,
                    User.CharacterMemoryStateDir,
                    User.SessionProvisioning,
                    CharacterContextTemplate: "prompt ${characterName}"
                )],
                RecapGrid: new GalateaRecapGridFileConfig(
                    "routes.json",
                    ["profile.json"],
                    profile.ProfileId
                )
            );
            string configPath = Path.Combine(_root, "config.json");
            File.WriteAllText(
                configPath,
                JsonSerializer.Serialize(users, GalateaJson.Options)
            );
            var connection = new CompletionConnectionConfig(
                "test",
                "openai-chat",
                "model-a",
                "openai-chat/strict",
                "http://localhost:8000/",
                ApiKey: "test-key"
            );
            GalateaTestHost.WriteConnectionsFile(
                Path.Combine(_root, GalateaConfigLoader.ConnectionsFileName),
                [connection],
                connection.Id
            );
            GalateaTestHost.WriteDelegatesFile(_root);
            return configPath;
        }

        private static RecapGridAgentControlProfile CreateProfile() {
            Assert.True(RecapGridAgentControlBuiltIns
                .TryCreateRegistrationBundle(
                    RecapGridAgentControlBuiltIns.MysteryInvestigationV4,
                    out RecapGridControlRegistrationBundle? builtIn
                ));
            return RecapGridAgentControlProfile.Create(
                "operator-recovery-profile",
                new RecapGridControlAdmission(
                    RecapGridControlPermission.All,
                    [builtIn!.Families[0].Digest],
                    builtIn.Definitions.Select(static value =>
                        value.Capability.CapabilityFingerprint),
                    [ContextHeaderCarrier.System],
                    ["case."],
                    maximumBootstrapRows: 64,
                    maximumProjectedCalls: 1_024
                )
            );
        }

        private static SendMailIntent Mail(string body) => new(
            GalateaDelegateConfigReader.CanonicalRecipient,
            Subject: null,
            body,
            InReplyToMessageId: null,
            EvidenceQuote: "sent it"
        );

        private static string Address(int value) =>
            $"ej1:{value:x16}0000000100000000";

        public void Dispose() {
            _store?.Dispose();
            TestDirectorySafety.DeleteOwnedTreeNoFollow(_root);
        }
    }
}
