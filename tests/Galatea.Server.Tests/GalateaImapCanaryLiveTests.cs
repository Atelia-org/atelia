using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Server.Mailbox;
using Atelia.MdJson;
using Atelia.SessionJournal;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.Galatea.Server.Tests;

// Opt in with ATELIA_GALATEA_IMAP_CANARY_CONFIG. No private file is opened by
// ordinary test runs. Receiving canaries send five or six controlled messages,
// never retry SMTP, and never download old messages. Independent probes inspect
// only bounded UID metadata, never message content.
public sealed class GalateaImapCanaryLiveTests(ITestOutputHelper output) {
    private static readonly TimeSpan NetworkDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ArrivalDeadline = TimeSpan.FromSeconds(120);
    private const string ConfigVariable = "ATELIA_GALATEA_IMAP_CANARY_CONFIG";

    // Independent diagnostic: no ledger, host, store, BODY, SEARCH, or SMTP.
    // STATUS is sent before EXAMINE, never against an already opened folder.
    // Only the UID metadata exposed after each command is reported.
    [ImapCanaryFact]
    [Trait("Category", "GalateaImapLive")]
    public async Task ReadOnlyUidMetadataProbe_ReportsCurrentNamespaceForControlledAccounts() {
        bool failed = false;
        IReadOnlyDictionary<string, GalateaEmailAccount> accounts;
        try {
            string? path = Environment.GetEnvironmentVariable(ConfigVariable);
            Require(!string.IsNullOrWhiteSpace(path), "CANARY_CONFIG_NOT_SET");
            accounts = ReadAccounts(path!);
        }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            throw new InvalidOperationException("IMAP_METADATA_CONFIG_FAILED exceptionType=" + exception.GetType().FullName);
        }
        foreach (string id in new[] { "gpt", "cyber" }) {
            using var deadline = new CancellationTokenSource(NetworkDeadline);
            using var client = new ImapClient { Timeout = checked((int)NetworkDeadline.TotalMilliseconds) };
            string stage = "IMAP_CONNECT_FAILED";
            try {
                GalateaEmailAccount account = accounts[id];
                await client.ConnectAsync(account.Imap!.Host, account.Imap.Port,
                    account.Imap.TlsMode == "implicit" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
                    deadline.Token);
                stage = "IMAP_AUTH_FAILED";
                await client.AuthenticateAsync(account.Address, account.AuthorizationCode, deadline.Token);
                if (client.Capabilities.HasFlag(ImapCapabilities.Id)) {
                    stage = "IMAP_ID_FAILED";
                    await client.IdentifyAsync(new ImapImplementation { Name = "Galatea", Version = "1" }, deadline.Token);
                }
                stage = "IMAP_STATUS_FAILED";
                await client.Inbox.StatusAsync(StatusItems.UidValidity | StatusItems.UidNext, deadline.Token);
                uint statusUidValidity = client.Inbox.UidValidity;
                uint? statusUidNext = client.Inbox.UidNext is { IsValid: true } next ? next.Id : null;
                stage = "IMAP_EXAMINE_FAILED";
                await client.Inbox.OpenAsync(FolderAccess.ReadOnly, deadline.Token);
                uint examineUidValidity = client.Inbox.UidValidity;
                uint? examineUidNext = client.Inbox.UidNext is { IsValid: true } after ? after.Id : null;
                int examineMessageCount = client.Inbox.Count;
                string code = statusUidValidity == 0 || statusUidNext is null or 0
                    || examineUidValidity == 0 || examineUidNext is null or 0
                    ? "IMAP_INVALID_UID_METADATA" : "IMAP_UID_METADATA_AVAILABLE";
                output.WriteLine(JsonSerializer.Serialize(new {
                    characterId = id, statusUidValidity, statusUidNext, examineUidValidity, examineUidNext,
                    examineMessageCount, code
                }));
            }
            catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
                failed = true;
                string code = exception is OperationCanceledException ? "IMAP_METADATA_TIMEOUT" : stage;
                output.WriteLine(JsonSerializer.Serialize(new {
                    characterId = id, code, exceptionType = exception.GetType().FullName
                }));
            }
        }
        Require(!failed, "IMAP_METADATA_PROBE_FAILED");
    }

    // Only UID metadata: two independent selections and at most the last two
    // UID summaries. Sequence indexes are transient probe selectors, never IDs.
    // This probe does not establish persistent-UID support or a receive baseline.
    [ImapCanaryFact]
    [Trait("Category", "GalateaImapLive")]
    public async Task ReadOnlyUidHorizonProbe_ReportsStableTailAcrossSessions() {
        IReadOnlyDictionary<string, GalateaEmailAccount> accounts;
        try {
            string? path = Environment.GetEnvironmentVariable(ConfigVariable);
            Require(!string.IsNullOrWhiteSpace(path), "CANARY_CONFIG_NOT_SET");
            accounts = ReadAccounts(path!);
        }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            throw new InvalidOperationException("IMAP_HORIZON_CONFIG_FAILED exceptionType=" + exception.GetType().FullName);
        }
        bool failed = false;
        foreach (string id in new[] { "gpt", "cyber" }) {
            UidHorizonSnapshot? previousSession = null;
            for (int sessionNumber = 1; sessionNumber <= 2; sessionNumber++) {
                using var deadline = new CancellationTokenSource(NetworkDeadline);
                using var client = GalateaNetworkImapTransport.CreateReadOnlyClient(checked((int)NetworkDeadline.TotalMilliseconds));
                string stage = "IMAP_CONNECT_FAILED";
                int? tailSummaryCount = null;
                string tailProbeMode = "not-selected";
                try {
                    GalateaEmailAccount account = accounts[id];
                    await client.ConnectAsync(account.Imap!.Host, account.Imap.Port,
                        account.Imap.TlsMode == "implicit" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
                        deadline.Token);
                    stage = "IMAP_AUTH_FAILED";
                    await client.AuthenticateAsync(account.Address, account.AuthorizationCode, deadline.Token);
                    if (client.Capabilities.HasFlag(ImapCapabilities.Id)) {
                        stage = "IMAP_ID_FAILED";
                        await client.IdentifyAsync(new ImapImplementation { Name = "Galatea", Version = "1" }, deadline.Token);
                    }
                    stage = "IMAP_STATUS_FAILED";
                    await client.Inbox.StatusAsync(StatusItems.UidValidity | StatusItems.UidNext, deadline.Token);
                    uint statusUidValidity = client.Inbox.UidValidity;
                    uint? statusUidNext = client.Inbox.UidNext is { IsValid: true } next ? next.Id : null;
                    stage = "IMAP_EXAMINE_FAILED";
                    // The diagnostic deliberately observes missing native UIDNEXT.
                    // It does not invoke the production baseline-selection helper.
                    await client.Inbox.OpenAsync(FolderAccess.ReadOnly, deadline.Token);
                    uint examineUidValidity = client.Inbox.UidValidity;
                    uint? examineUidNext = client.Inbox.UidNext is { IsValid: true } after ? after.Id : null;
                    int examineMessageCount = client.Inbox.Count;
                    uint? tailUid = null, previousTailUid = null;
                    int lastTwoSummaryCount = 0;
                    tailProbeMode = examineUidNext is null ? "highest-uid-fallback" : "native-next-sequence-tail";
                    if (examineMessageCount > 0) {
                        if (examineUidNext is null) {
                            stage = "IMAP_TAIL_UID_FETCH_FAILED";
                            // Only the missing-native path requires this range.
                            var tailSummaries = await client.Inbox.FetchAsync(
                                new UniqueIdRange(UniqueId.MaxValue, UniqueId.MaxValue),
                                new FetchRequest(MessageSummaryItems.UniqueId), deadline.Token);
                            tailSummaryCount = tailSummaries.Count;
                            Require(tailSummaryCount == 1 && tailSummaries[0].UniqueId.IsValid,
                                "IMAP_TAIL_UID_RESULT_INVALID");
                            tailUid = tailSummaries[0].UniqueId.Id;
                        }
                        stage = "IMAP_LAST_TWO_UID_FETCH_FAILED";
                        int minimumIndex = Math.Max(0, examineMessageCount - 2);
                        var lastTwo = await client.Inbox.FetchAsync(minimumIndex, examineMessageCount - 1,
                            new FetchRequest(MessageSummaryItems.UniqueId), deadline.Token);
                        lastTwoSummaryCount = lastTwo.Count;
                        Require(lastTwo.Count == examineMessageCount - minimumIndex
                            && lastTwo.All(summary => summary.UniqueId.IsValid
                                && summary.Index >= minimumIndex && summary.Index < examineMessageCount),
                            "IMAP_LAST_TWO_UID_RESULT_INVALID");
                        var ordered = lastTwo.OrderBy(summary => summary.Index).ToArray();
                        Require(ordered.Select(summary => summary.Index).Distinct().Count() == ordered.Length
                            && (tailUid is null || ordered[^1].UniqueId.Id == tailUid),
                            "IMAP_TAIL_UID_CHANGED_DURING_PROBE");
                        tailUid = ordered[^1].UniqueId.Id;
                        if (ordered.Length == 2) {
                            previousTailUid = ordered[0].UniqueId.Id;
                            Require(previousTailUid < tailUid, "IMAP_LAST_TWO_UID_ORDER_INVALID");
                        }
                    }
                    Require(client.Inbox.UidValidity == examineUidValidity && client.Inbox.Count == examineMessageCount,
                        "IMAP_NAMESPACE_CHANGED_DURING_PROBE");
                    ulong? tailUidGap = previousTailUid is { } earlier && tailUid is { } latest
                        ? (ulong)latest - earlier : null;
                    ulong? missingUidSlotsBetweenLastTwo = tailUidGap is { } gap ? gap - 1 : null;
                    var current = new UidHorizonSnapshot(examineUidValidity, examineMessageCount, tailUid, previousTailUid);
                    bool? tailStableAcrossSessions = previousSession is null ? null
                        : previousSession.UidValidity == current.UidValidity && previousSession.TailUid == current.TailUid;
                    bool? lastTwoStableAcrossSessions = previousSession is null ? null : previousSession == current;
                    output.WriteLine(JsonSerializer.Serialize(new {
                        characterId = id, sessionNumber, statusUidValidity, statusUidNext,
                        examineUidValidity, examineUidNext, examineMessageCount, tailUid, previousTailUid,
                        tailUidGap, missingUidSlotsBetweenLastTwo, tailSummaryCount, lastTwoSummaryCount,
                        tailProbeMode, tailStableAcrossSessions, lastTwoStableAcrossSessions,
                        uidPersistence = "not-observable", code = "IMAP_UID_HORIZON_OBSERVED"
                    }));
                    previousSession = current;
                }
                catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
                    failed = true;
                    string code = exception is CanaryFailure failure ? failure.Code
                        : exception is OperationCanceledException ? "IMAP_HORIZON_TIMEOUT" : stage;
                    output.WriteLine(JsonSerializer.Serialize(new {
                        characterId = id, sessionNumber, code, tailProbeMode, tailSummaryCount,
                        exceptionType = exception.GetType().FullName
                    }));
                }
            }
        }
        Require(!failed, "IMAP_UID_HORIZON_PROBE_FAILED");
    }

    [ImapCanaryFact]
    [Trait("Category", "GalateaImapLive")]
    public Task ControlledMailboxes_AdmissionAndRuntimeObservation_AreDurableAndReadOnly() =>
        RunCanaryAsync(receiveOnSecondAccount: true);

    [ImapCanaryFact]
    [Trait("Category", "GalateaImapLive")]
    public Task QqOnlyReceiver_AdmissionAndRuntimeObservation_AreDurableAndReadOnly() =>
        RunCanaryAsync(receiveOnSecondAccount: false);

    [ImapCanaryFact]
    [Trait("Category", "GalateaImapLive")]
    public Task DualReceiversAfterUidHorizonAdaptation_AdmissionAndRuntimeObservation_AreDurableAndReadOnly() =>
        RunCanaryAsync(receiveOnSecondAccount: true, guardName: "ledger-dual-horizon.json",
            receiverMode: "dual-receiver-native-or-observed-tail-horizon");

    private async Task RunCanaryAsync(bool receiveOnSecondAccount, string? guardName = null, string? receiverMode = null) {
        string? configPath = Environment.GetEnvironmentVariable(ConfigVariable);
        Require(!string.IsNullOrWhiteSpace(configPath), "CANARY_CONFIG_NOT_SET");

        // A durable attempt guard makes accidental reruns explicit. Failed or
        // uncertain sends require examining this ledger before preparing a new run.
        string ledgerName = guardName ?? (receiveOnSecondAccount ? "ledger.json" : "ledger-qq.json");
        using var ledger = new CanaryLedger(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath!))!, ledgerName),
            receiverMode ?? (receiveOnSecondAccount ? "dual-receiver" : "gpt-only-receiver"));
        try {
            IReadOnlyDictionary<string, GalateaEmailAccount> accounts = ReadAccounts(configPath!);
            var completion = new NoReplyCompletion();
            await using var files = GalateaTestHost.CreateMissingSession(completion,
                DisabledGalateaUserMessageNormalizer.Instance);
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(files.RootDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(files.ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            JsonNode root = JsonNode.Parse(File.ReadAllText(files.ConfigPath))!;
            JsonNode template = root["characters"]![0]!.DeepClone();
            var characters = new JsonArray();
            foreach (string id in new[] { "gpt", "cyber" }) {
                JsonNode character = template.DeepClone();
                character["id"] = id;
                character["name"] = id == "gpt" ? "GptCanary" : "CyberCanary";
                character["sessionDir"] = "sessions/" + id;
                character["delegationStateDir"] = "delegation-state/" + id;
                character["characterMemoryStateDir"] = "character-memory/" + id;
                character["homeDir"] = Directory.CreateDirectory(Path.Combine(
                    Path.GetDirectoryName(files.ConfigPath)!, "homes", id)).FullName;
                character["sessionProvisioning"] = "create-if-missing";
                JsonObject email = Email(accounts[id]);
                if (id == "cyber" && !receiveOnSecondAccount) { email.Remove("imap"); }
                character["email"] = email;
                characters.Add(character);
            }
            root["characters"] = characters;
            var clock = new GalateaLabClock();
            string token = "GalateaIMAP-" + Guid.NewGuid().ToString("N");
            var baselines = new Dictionary<string, GalateaImapCheckpointSnapshot>(StringComparer.Ordinal);
            var deniedMarkers = new List<string>();
            var acceptedUids = new Dictionary<string, List<uint>>(StringComparer.Ordinal) { ["gpt"] = [], ["cyber"] = [] };
            string[] receivingIds = receiveOnSecondAccount ? ["gpt", "cyber"] : ["gpt"];
            int expectedModelCalls = receiveOnSecondAccount ? 3 : 2;

            // The production poller may establish the baseline during startup;
            // all subsequently controlled messages are sent only after Ready.
            WriteConfig(smtpEnabled: false, gptAllowed: false, cyberAllowed: false);
            ledger.Phase("baseline");
            await using (var web = OpenWeb()) {
                var host = web.Services.GetRequiredService<GalateaHostService>();
                foreach (string id in accounts.Keys) {
                    _ = await host.GetSessionAsync(id, CancellationToken.None);
                    if (!receivingIds.Contains(id)) { continue; }
                    await PollAsync(web, id, ledger);
                    Require(host.ReadImapInboundStatus(id).State == "Ready", "CANARY_BASELINE_NOT_READY");
                    var session = await host.GetSessionAsync(id, CancellationToken.None);
                    var checkpoint = session.DelegationHandle!.Store.ReadImapCheckpoint(host.Imap.ReferenceFor(id)!)!;
                    baselines.Add(id, checkpoint);
                    ledger.Record("baseline", id, "IMAP_READY", checkpoint.ScannedThroughUid);
                }
                await IncomingAsync(web, "cyber", "gpt", "unknown-gpt", allowed: false);
                if (receiveOnSecondAccount) {
                    await IncomingAsync(web, "gpt", "cyber", "unknown-cyber", allowed: false);
                }
                Require(completion.Inputs.IsEmpty, "CANARY_UNKNOWN_REACHED_MODEL");
                RequireNoSmtpOutbox(host, "gpt", "cyber");
            }
            AssertArtifacts();

            WriteConfig(smtpEnabled: false, gptAllowed: true, cyberAllowed: true);
            ledger.Phase("static-list");
            await using (var web = OpenWeb()) {
                await CheckPreservedBaselineAsync(web);
                await IncomingAsync(web, "cyber", "gpt", "static", allowed: true);
                RequireNoSmtpOutbox(web.Services.GetRequiredService<GalateaHostService>(), "gpt", "cyber");
            }

            // Leave only cyber's static list enabled for the later reverse
            // direction. The target gpt has neither static nor derived permission.
            WriteConfig(smtpEnabled: false, gptAllowed: false, cyberAllowed: true);
            ledger.Phase("static-removed");
            await using (var web = OpenWeb()) {
                await CheckPreservedBaselineAsync(web);
                RequireNoSmtpOutbox(web.Services.GetRequiredService<GalateaHostService>(), "gpt", "cyber");
                await IncomingAsync(web, "cyber", "gpt", "removed", allowed: false);
            }
            AssertArtifacts();

            WriteConfig(smtpEnabled: true, gptAllowed: false, cyberAllowed: true);
            ledger.Phase("derived-contact");
            await using (var web = OpenWeb()) {
                await CheckPreservedBaselineAsync(web);
                var host = web.Services.GetRequiredService<GalateaHostService>();
                var gpt = await host.GetSessionAsync("gpt", CancellationToken.None);
                var cyber = await host.GetSessionAsync("cyber", CancellationToken.None);
                string marker = token + "-outgoing";
                var cyberCheckpoint = receiveOnSecondAccount
                    ? cyber.DelegationHandle!.Store.ReadImapCheckpoint(host.Imap.ReferenceFor("cyber")!) : null;
                await CaptureAndSendAsync(web, gpt, accounts["cyber"].Address, marker, ledger);
                if (receiveOnSecondAccount) {
                    uint previousCyberUid = cyberCheckpoint!.ScannedThroughUid;
                    var delivered = await WaitForNewUidAsync(accounts["cyber"], previousCyberUid, marker);
                    ledger.Record("arrived", "cyber", "IMAP_NEW_UID", delivered.Uid, delivered.Seen,
                        previousScannedUid: previousCyberUid,
                        uidGap: (ulong)delivered.Uid - previousCyberUid,
                        observedScanUpperUid: delivered.ObservedScanUpperUid);
                    await ProcessAsync(web, "cyber", delivered.Uid, allowed: true);
                    await VerifySeenAsync(accounts["cyber"], delivered, "cyber");
                }
                await IncomingAsync(web, "cyber", "gpt", "reply", allowed: true);
                Require(gpt.DelegationHandle!.Store.ReadSnapshot().SmtpMailOutboxes.Single().State
                    == GalateaSmtpMailState.ProviderAccepted, "CANARY_NORMAL_SMTP_NOT_ACCEPTED");
                Require(RuntimeObservationCount(gpt) == 2
                    && RuntimeObservationCount(cyber) == (receiveOnSecondAccount ? 1 : 0),
                    "CANARY_OBSERVATION_COUNTS");
                Require(completion.Inputs.Count == expectedModelCalls, "CANARY_MODEL_CALL_COUNTS");
            }
            AssertArtifacts();

            ledger.Phase("cold-reopen");
            await using (var web = OpenWeb()) {
                await CheckPreservedBaselineAsync(web);
                foreach (string id in receivingIds) {
                    await PollAsync(web, id, ledger);
                    var host = web.Services.GetRequiredService<GalateaHostService>();
                    var session = await host.GetSessionAsync(id, CancellationToken.None);
                    Require(RuntimeObservationCount(session) == (id == "gpt" ? 2 : 1), "CANARY_REOPEN_REPLAYED");
                    foreach (uint uid in acceptedUids[id]) {
                        var row = session.DelegationHandle!.Store.ReadExternalMail(acceptedUids[id].IndexOf(uid) + 1);
                        Require(row?.Uid == uid && row.State == GalateaExternalMailInboxState.Observed,
                            "CANARY_REOPEN_PROOF_MISSING");
                    }
                }
                await web.Services.GetRequiredService<GalateaCharacterMailRelay>().SweepAsync(CancellationToken.None);
                Require(completion.Inputs.Count == expectedModelCalls, "CANARY_COLD_REOPEN_MODEL_REPLAY");
                var gpt = await web.Services.GetRequiredService<GalateaHostService>().GetSessionAsync("gpt", CancellationToken.None);
                Require(!await new GalateaSmtpOutboxConsumer(web.Services.GetRequiredService<IGalateaSmtpSender>())
                    .ConsumeOneAsync(gpt.DelegationHandle!.Store, CancellationToken.None), "CANARY_SMTP_REPLAY");
            }
            ledger.Complete();

            async Task IncomingAsync(WebApplicationFactory<Program> web, string from, string to, string phase, bool allowed) {
                ledger.Phase(phase);
                var host = web.Services.GetRequiredService<GalateaHostService>();
                var session = await host.GetSessionAsync(to, CancellationToken.None);
                uint previous = session.DelegationHandle!.Store.ReadImapCheckpoint(host.Imap.ReferenceFor(to)!)!.ScannedThroughUid;
                string marker = token + "-" + phase;
                if (!allowed) { deniedMarkers.Add(marker); }
                // External client utility: this send creates no role-store capture.
                ledger.Record("smtp-attempting", from, "CLIENT_ATTEMPTING");
                var smtp = new GalateaSmtpConfig(new(true, 30), accounts);
                using var deadline = new CancellationTokenSource(NetworkDeadline);
                GalateaSmtpSendResult result = await new GalateaNetworkSmtpSender(smtp).SendAsync(
                    new(Guid.NewGuid().ToString("N"), from, smtp.ReferenceFor(from), accounts[to].Address,
                        marker, marker), deadline.Token);
                ledger.Record("smtp-result", from, result.Code ?? "SMTP_NO_CODE");
                Require(result.State == GalateaSmtpMailState.ProviderAccepted, "CANARY_CLIENT_SEND_NOT_ACCEPTED");
                var delivered = await WaitForNewUidAsync(accounts[to], previous, marker);
                ledger.Record("arrived", to, "IMAP_NEW_UID", delivered.Uid, delivered.Seen,
                    previousScannedUid: previous, uidGap: (ulong)delivered.Uid - previous,
                    observedScanUpperUid: delivered.ObservedScanUpperUid);
                await ProcessAsync(web, to, delivered.Uid, allowed);
                await VerifySeenAsync(accounts[to], delivered, to);
            }

            async Task ProcessAsync(WebApplicationFactory<Program> web, string id, uint uid, bool allowed) {
                var host = web.Services.GetRequiredService<GalateaHostService>();
                var session = await host.GetSessionAsync(id, CancellationToken.None);
                var store = session.DelegationHandle!.Store;
                using var deadline = new CancellationTokenSource(ArrivalDeadline);
                while (store.ReadImapCheckpoint(host.Imap.ReferenceFor(id)!)!.ScannedThroughUid < uid) {
                    await PollAsync(web, id, ledger);
                    deadline.Token.ThrowIfCancellationRequested();
                    await Task.Delay(50, deadline.Token);
                }
                if (allowed) {
                    acceptedUids[id].Add(uid);
                    long inboxId = acceptedUids[id].Count;
                    while (store.ReadExternalMail(inboxId)?.State != GalateaExternalMailInboxState.Observed
                        || session.GetCurrentTurn() is not null) {
                        await web.Services.GetRequiredService<GalateaCharacterMailRelay>().SweepAsync(deadline.Token);
                        await Task.Delay(20, deadline.Token);
                    }
                    var row = store.ReadExternalMail(inboxId)!;
                    Require(row.Uid == uid, "CANARY_ACCEPTED_UID_MISMATCH");
                    var observation = session.Engine.ReadRecentCompletedTurns(16).RequireSnapshot().Turns
                        .Single(turn => EventAddressTextCodec.Format(turn.ObservationAddress) == row.ObservationAddress);
                    Require(row.ObservationContent == observation.ObservationContent, "CANARY_EXACT_JOURNAL_PROOF_MISMATCH");
                    ledger.Record("journal-proof", id, "IMAP_EXACT_OBSERVATION_PROOF", uid,
                        messageId: row.MessageId, observationAddress: row.ObservationAddress,
                        proofSha256: Convert.ToHexStringLower(SHA256.HashData(observation.ObservationContent.ToUtf8Json())));
                }
                else {
                    Require(store.ReadExternalMail(acceptedUids[id].Count + 1) is null, "CANARY_FILTER_PERSISTED_INBOX");
                    Require(RuntimeObservationCount(session) == acceptedUids[id].Count, "CANARY_FILTER_APPENDED_OBSERVATION");
                }
                ledger.Record("processed", id, allowed ? "IMAP_OBSERVED" : "IMAP_CURSOR_ONLY", uid);
            }

            async Task VerifySeenAsync(GalateaEmailAccount account, DeliveredMessage delivered, string id) {
                bool seen = await ReadSeenAsync(account, delivered.Uid);
                Require(seen == delivered.Seen, "CANARY_SEEN_CHANGED");
                ledger.Record("flags-unchanged", id, "IMAP_SEEN_UNCHANGED", delivered.Uid, seen);
            }

            async Task CheckPreservedBaselineAsync(WebApplicationFactory<Program> web) {
                var host = web.Services.GetRequiredService<GalateaHostService>();
                foreach (string id in accounts.Keys) {
                    var session = await host.GetSessionAsync(id, CancellationToken.None);
                    if (!receivingIds.Contains(id)) { continue; }
                    var actual = session.DelegationHandle!.Store.ReadImapCheckpoint(host.Imap.ReferenceFor(id)!)!;
                    Require(actual.UidValidity == baselines[id].UidValidity
                        && actual.BaselineAtUnixTimeMilliseconds == baselines[id].BaselineAtUnixTimeMilliseconds
                        && actual.ScannedThroughUid >= baselines[id].ScannedThroughUid,
                        "CANARY_BASELINE_RESET");
                }
            }

            void WriteConfig(bool smtpEnabled, bool gptAllowed, bool cyberAllowed) {
                root["runtime"]!["smtp"] = new JsonObject { ["enabled"] = smtpEnabled, ["timeoutSeconds"] = 30 };
                root["runtime"]!["imap"] = new JsonObject { ["enabled"] = true, ["timeoutSeconds"] = 30, ["pollIntervalSeconds"] = 3600 };
                root["characters"]![0]!["email"]!["imap"]!["autoDisplaySenders"] = gptAllowed
                    ? new JsonArray(accounts["cyber"].Address) : new JsonArray();
                if (receiveOnSecondAccount) {
                    root["characters"]![1]!["email"]!["imap"]!["autoDisplaySenders"] = cyberAllowed
                        ? new JsonArray(accounts["gpt"].Address) : new JsonArray();
                }
                File.WriteAllText(files.ConfigPath, root.ToJsonString());
            }

            WebApplicationFactory<Program> OpenWeb() => new GalateaWebApplicationFactory(files.ConfigPath,
                completion, DisabledGalateaUserMessageNormalizer.Instance, null, null, clock)
                .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
                    // Keep the real IMAP poller, relay, runner, and shutdown owner.
                    // A single explicit SMTP consumer provides the send boundary.
                    foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)
                        && item.ImplementationType == typeof(GalateaSmtpOutboxBackgroundService)).ToArray()) {
                        services.Remove(descriptor);
                    }
                }));

            void AssertArtifacts() {
                foreach (string artifact in Directory.EnumerateFiles(files.RootDirectory, "*", SearchOption.AllDirectories)) {
                    if (artifact == files.ConfigPath) { continue; }
                    byte[] bytes = File.ReadAllBytes(artifact);
                    foreach (string sentinel in deniedMarkers.Concat(accounts.Values.Select(account => account.AuthorizationCode))) {
                        Require(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(sentinel)) < 0, "CANARY_PRIVATE_CONTENT_PERSISTED");
                    }
                }
            }
        }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            string code = exception is CanaryFailure failure ? failure.Code
                : exception is GalateaImapReadException read ? read.Code
                : exception is OperationCanceledException ? "CANARY_TIMEOUT" : "CANARY_FAILED";
            ledger.Fail(code, exception.GetType().FullName ?? "unknown");
            // Do not attach a provider or config exception as InnerException.
            throw new InvalidOperationException(code + " phase=" + ledger.CurrentPhase
                + " exceptionType=" + exception.GetType().FullName);
        }
    }

    private static IReadOnlyDictionary<string, GalateaEmailAccount> ReadAccounts(string path) {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Require(document.RootElement.GetProperty("schema").GetString() == "atelia.galatea.imap-canary.v1", "CANARY_CONFIG_SCHEMA");
        var accounts = new Dictionary<string, GalateaEmailAccount>(StringComparer.Ordinal);
        foreach (JsonElement entry in document.RootElement.GetProperty("accounts").EnumerateArray()) {
            string id = entry.GetProperty("characterId").GetString()!;
            JsonElement email = entry.GetProperty("email");
            JsonElement imap = email.GetProperty("imap");
            var account = new GalateaEmailAccount(email.GetProperty("address").GetString()!,
                email.GetProperty("authorizationCode").GetString()!, email.GetProperty("smtpHost").GetString()!,
                email.GetProperty("smtpPort").GetInt32(), email.GetProperty("tlsMode").GetString()!,
                new(imap.GetProperty("host").GetString()!, imap.GetProperty("port").GetInt32(),
                    imap.GetProperty("tlsMode").GetString()!));
            accounts.Add(id, account);
        }
        Require(accounts.Count == 2 && accounts.ContainsKey("gpt") && accounts.ContainsKey("cyber"),
            "CANARY_CONTROLLED_ACCOUNTS_ONLY");
        _ = new GalateaImapConfig(new(true, 3600, 30), accounts);
        Require(!GalateaExternalMailAddress.SameMailbox(accounts["gpt"].Address, accounts["cyber"].Address),
            "CANARY_ACCOUNTS_MUST_DIFFER");
        return accounts;
    }

    private static JsonObject Email(GalateaEmailAccount account) => new() {
        ["address"] = account.Address, ["authorizationCode"] = account.AuthorizationCode,
        ["smtpHost"] = account.SmtpHost, ["smtpPort"] = account.SmtpPort, ["tlsMode"] = account.TlsMode,
        ["imap"] = new JsonObject { ["host"] = account.Imap!.Host, ["port"] = account.Imap.Port,
            ["tlsMode"] = account.Imap.TlsMode, ["autoDisplaySenders"] = new JsonArray() }
    };

    private static async Task PollAsync(WebApplicationFactory<Program> web, string id, CanaryLedger ledger) {
        using var deadline = new CancellationTokenSource(NetworkDeadline);
        var result = await web.Services.GetRequiredService<GalateaImapPoller>().PollCharacterAsync(id, deadline.Token);
        ledger.Record("poll", id, result.Code);
        Require(result.Code is "IMAP_READY" or "IMAP_BASELINE_ESTABLISHED" or "IMAP_POLL_LIMIT", "CANARY_POLL_FAILED");
    }

    private static async Task CaptureAndSendAsync(WebApplicationFactory<Program> web, CharacterSessionHost session,
        string recipient, string marker, CanaryLedger ledger) {
        using var deadline = new CancellationTokenSource(NetworkDeadline);
        await session.TurnLock.WaitAsync(deadline.Token);
        try {
            string action = "收件人：" + recipient + "\n主题：" + marker + "\n" + marker + "\n[GptCanary] 我已寄出。";
            session.Engine.AppendObservation("受控外发测试。");
            session.Engine.AppendImportedAgentAction(new([new ActionBlock.Text(action)]),
                new CompletionDescriptor("canary", "canary", "canary"));
            var reconciler = new GalateaOutboundMailExtractionReconciler(session.DelegationHandle!.Store,
                new CapturedContactExtractor(recipient, marker, action),
                new("character", "gpt", "GptCanary"), smtpSenderAccountReference:
                    web.Services.GetRequiredService<GalateaConfig>().Smtp.ReferenceFor("gpt"));
            Require(await reconciler.ReconcileAsync(session.Engine, deadline.Token)
                is GalateaOutboundMailExtractionReconcileResult.Captured, "CANARY_CONTACT_NOT_CAPTURED");
        }
        finally { session.TurnLock.Release(); }
        ledger.Record("smtp-attempting", "gpt", "OUTBOX_ATTEMPTING");
        Require(await new GalateaSmtpOutboxConsumer(web.Services.GetRequiredService<IGalateaSmtpSender>())
            .ConsumeOneAsync(session.DelegationHandle!.Store, deadline.Token), "CANARY_OUTBOX_NOT_CLAIMED");
        var row = session.DelegationHandle.Store.ReadSnapshot().SmtpMailOutboxes.Single();
        ledger.Record("smtp-result", "gpt", row.ResultCode ?? "SMTP_NO_CODE");
        Require(row.State == GalateaSmtpMailState.ProviderAccepted, "CANARY_OUTBOX_SEND_NOT_ACCEPTED");
        ledger.Record("capture-proof", "gpt", "SMTP_NORMAL_CAPTURE", dispatchId: row.DispatchId,
            sourceActionAddress: row.SourceActionAddress);
    }

    private static async Task<DeliveredMessage> WaitForNewUidAsync(GalateaEmailAccount account, uint previous, string marker) {
        using var deadline = new CancellationTokenSource(ArrivalDeadline);
        while (true) {
            using var client = await OpenReadOnlyAsync(account, deadline.Token);
            uint validity = client.Inbox.UidValidity;
            Require(validity != 0, "CANARY_INVALID_UID_VALIDITY");
            uint upper = await GalateaNetworkImapTransport.ReadScanUpperUidAsync(client, deadline.Token);
            Require(client.Inbox.UidValidity == validity, "CANARY_UID_VALIDITY_CHANGED");
            if (upper > previous) {
                // Locate the unique controlled marker only in a bounded newest
                // window. The real poller still processes all preceding windows.
                uint first = checked((uint)Math.Max((ulong)previous + 1,
                    (ulong)upper >= GalateaImapBounds.MaximumSearchUidSpan
                        ? (ulong)upper - GalateaImapBounds.MaximumSearchUidSpan + 1 : 1));
                int count = checked((int)((ulong)upper - first + 1));
                Require(first != 0 && count <= GalateaImapBounds.MaximumSearchUidSpan,
                    "CANARY_INVALID_UID_RANGE");
                // A range ending at uint.MaxValue becomes '*'; the bounded list
                // preserves the numeric endpoint if that message is expunged.
                var requested = new UniqueId[count];
                for (int index = 0; index < requested.Length; index++) {
                    requested[index] = new UniqueId(checked(first + (uint)index));
                }
                var uids = await client.Inbox.SearchAsync(SearchQuery.And(
                    SearchQuery.Uids(requested),
                    SearchQuery.SubjectContains(marker)), deadline.Token);
                Require(client.Inbox.UidValidity == validity, "CANARY_UID_VALIDITY_CHANGED");
                Require(uids.All(uid => uid.IsValid && uid.Id >= first && uid.Id <= upper),
                    "CANARY_INVALID_SEARCH_RESULT");
                Require(uids.Count <= 1, "CANARY_DUPLICATE_CONTROLLED_DELIVERY");
                if (uids.Count == 1) {
                    bool seen = await FlagsAsync(client, uids[0], deadline.Token);
                    Require(client.Inbox.UidValidity == validity, "CANARY_UID_VALIDITY_CHANGED");
                    return new(uids[0].Id, seen, upper);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
        }
    }

    private static async Task<bool> ReadSeenAsync(GalateaEmailAccount account, uint uid) {
        using var deadline = new CancellationTokenSource(NetworkDeadline);
        using var client = await OpenReadOnlyAsync(account, deadline.Token);
        return await FlagsAsync(client, new UniqueId(uid), deadline.Token);
    }

    private static async Task<ImapClient> OpenReadOnlyAsync(GalateaEmailAccount account, CancellationToken ct) {
        var client = GalateaNetworkImapTransport.CreateReadOnlyClient(30_000);
        try {
            await client.ConnectAsync(account.Imap!.Host, account.Imap.Port,
                account.Imap.TlsMode == "implicit" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
            await client.AuthenticateAsync(account.Address, account.AuthorizationCode, ct);
            if (client.Capabilities.HasFlag(ImapCapabilities.Id)) {
                await client.IdentifyAsync(new ImapImplementation { Name = "Galatea", Version = "1" }, ct);
            }
            await GalateaNetworkImapTransport.OpenInboxReadOnlyAsync(client, ct);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task<bool> FlagsAsync(ImapClient client, UniqueId uid, CancellationToken ct) {
        var summaries = await client.Inbox.FetchAsync(new[] { uid },
            new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags), ct);
        var matches = summaries.Where(summary => summary.UniqueId == uid).ToArray();
        Require(matches.Length == 1 && matches[0].Flags is not null, "CANARY_FLAGS_MISSING");
        return matches[0].Flags!.Value.HasFlag(MessageFlags.Seen);
    }

    private static int RuntimeObservationCount(CharacterSessionHost session) => session.Engine.ReadRecentCompletedTurns(16)
        .RequireSnapshot().Turns.Count(turn => turn.ObservationContent.SchemaId == GalateaObservationContent.V5SchemaId
            && turn.ObservationContent.JsonValue.GetProperty("kind").GetString() == "email-inbound");

    private static void RequireNoSmtpOutbox(GalateaHostService host, params string[] ids) {
        foreach (string id in ids) {
            Require(host.DelegationSupervisor.TryGetAttachedStore(id, out var store)
                && store.ReadSnapshot().SmtpMailOutboxes.Count == 0, "CANARY_UNEXPECTED_DERIVED_CONTACT");
        }
    }

    private static void Require(bool condition, string code) { if (!condition) { throw new CanaryFailure(code); } }
    private sealed class ImapCanaryFactAttribute : FactAttribute {
        public ImapCanaryFactAttribute() {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConfigVariable))) {
                Skip = "Set ATELIA_GALATEA_IMAP_CANARY_CONFIG to explicitly enable controlled-mailbox live acceptance.";
            }
        }
    }
    private sealed class CanaryFailure(string code) : InvalidOperationException(code) { internal string Code { get; } = code; }
    private sealed record DeliveredMessage(uint Uid, bool Seen, uint ObservedScanUpperUid);
    private sealed record UidHorizonSnapshot(uint UidValidity, int MessageCount, uint? TailUid, uint? PreviousTailUid);

    private sealed class CapturedContactExtractor(string recipient, string marker, string exactAction) : IOutboundMailExtractor {
        public string ContractId => "atelia.galatea.imap-canary-contact.v1";
        public ValueTask<IReadOnlyList<SendMailIntent>> ExtractAsync(string visibleActionText,
            CancellationToken cancellationToken, TextExtractionSource? source = null) {
            cancellationToken.ThrowIfCancellationRequested();
            Require(visibleActionText == exactAction, "CANARY_SOURCE_ACTION_CHANGED");
            return ValueTask.FromResult<IReadOnlyList<SendMailIntent>>([new(recipient, marker, marker, null, "我已寄出。")]);
        }
    }

    private sealed class NoReplyCompletion : ICompletionClientFactory, ICompletionClient {
        internal ConcurrentQueue<JsonElement> Inputs { get; } = new();
        public string Name => "imap-canary-no-reply";
        public string ApiSpecId => "openai-chat-v1";
        public ICompletionClient Create(CompletionConnectionConfig connection) => this;
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement input = MdJsonSerializer.Read((string)request.PromptPrefix.SharedContextMessages
                .OfType<ObservationMessage>().Last().Content!);
            Require(input.GetProperty("kind").GetString() == "email-inbound", "CANARY_UNEXPECTED_MODEL_CALL");
            Require(input.GetProperty("sender").GetProperty("kind").GetString() == "runtime", "CANARY_SENDER_PROMOTED");
            Require(input.GetProperty("sender").GetProperty("id").GetString() == "galatea", "CANARY_RUNTIME_SENDER_CHANGED");
            Inputs.Enqueue(input);
            const string text = "我看到了这封信，并把注意力转回眼前的故事。";
            observer?.OnTextDelta(text);
            return Task.FromResult(new CompletionResult(new ActionMessage([new ActionBlock.Text(text)]),
                CompletionDescriptor.From(this, request)));
        }
    }

    private sealed class CanaryLedger : IDisposable {
        private readonly FileStream _stream;
        private readonly string _receiverMode;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private readonly List<object> _events = [];
        private string _state = "Running";
        internal string CurrentPhase { get; private set; } = "preflight";
        internal CanaryLedger(string path, string receiverMode) {
            _receiverMode = receiverMode;
            _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try {
                if (_stream.Length != 0) {
                    using JsonDocument existing = JsonDocument.Parse(_stream);
                    Require(existing.RootElement.GetProperty("state").GetString() == "Prepared", "CANARY_ALREADY_ATTEMPTED");
                }
                Save();
            }
            catch { _stream.Dispose(); throw; }
        }
        internal void Phase(string phase) { CurrentPhase = phase; Save(); }
        internal void Record(string step, string characterId, string code, uint? uid = null, bool? seen = null,
            string? messageId = null, string? observationAddress = null, string? proofSha256 = null,
            string? dispatchId = null, string? sourceActionAddress = null, uint? previousScannedUid = null,
            ulong? uidGap = null, uint? observedScanUpperUid = null) {
            _events.Add(new { phase = CurrentPhase, step, characterId, code, uid, seen,
                messageId, observationAddress, proofSha256, dispatchId, sourceActionAddress,
                previousScannedUid, uidGap, observedScanUpperUid });
            Save();
        }
        internal void Complete() { _state = "Completed"; CurrentPhase = "complete"; Save(); }
        internal void Fail(string code, string exceptionType) {
            _state = "Failed";
            _events.Add(new { phase = CurrentPhase, code, exceptionType });
            Save();
        }
        private void Save() {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new {
                schema = "atelia.galatea.imap-canary-ledger.v1", state = _state, phase = CurrentPhase,
                receiverMode = _receiverMode,
                mode = "real-network-store-relay-journal-with-deterministic-no-reply-completion",
                extractionMode = "deterministic-exact-original-action",
                productionLlmJudgmentTested = false, startedAtUtc = _startedAt,
                elapsedMilliseconds = _elapsed.ElapsedMilliseconds, events = _events
            }, new JsonSerializerOptions { WriteIndented = true });
            _stream.Position = 0;
            _stream.Write(bytes);
            _stream.SetLength(bytes.Length);
            _stream.Flush(flushToDisk: true);
        }
        public void Dispose() => _stream.Dispose();
    }
}
