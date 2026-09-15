using System.IO.Compression;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed partial class ProgramRecapGridCommandTests {
    [Theory]
    [InlineData("after-control-commit", false)]
    [InlineData("after-tool-result", true)]
    public void LegacyControlReceiptContinuesAcrossJournalCommitWindows(
        string fixtureName,
        bool resultAlreadyCommitted
    ) {
        ExtractControlReceiptFixture(fixtureName);
        // Only this extracted, disposable Store is reset. The source ZIP and
        // durable Journal/Control bytes remain the legacy production fixture.
        var preparedReset = Assert.IsType<RecapGridStorePrepareResetResult.Prepared>(
            RecapGridStoreMaintenance.PrepareReset(_root));
        Assert.IsType<RecapGridStoreResetResult.Reset>(
            RecapGridStoreMaintenance.Reset(_root, preparedReset.Witness));
        (SessionJournalAuditScanResult initial, List<SessionJournalAuditEvent> before) = AuditReceiptFixture();
        string refId = initial.BranchRefId.ToHexString();
        Assert.Equal(resultAlreadyCommitted ? SessionEventKind.ToolResultObserved
                : SessionEventKind.ToolExecutionStarted,
            initial.ExecutionStateAtCapturedHead.HeadKind);
        Assert.Equal(1, initial.ExecutionStateAtCapturedHead.ToolExecutionSequenceCheckpoint);
        SessionJournalAuditEvent legacyPrepared = Assert.Single(before, static value =>
            value.Kind == SessionEventKind.CompletionRequestPrepared);
        Assert.Equal(8, legacyPrepared.BodySchemaVersion);
        byte[] legacyPreparedBytes = ReadReceiptEventPayload(legacyPrepared.Address);
        SessionRequestCommitment legacyCommitment = Assert.IsType<SessionRequestCommitment>(
            ReconstructReceiptRequest(legacyPrepared.Address).Manifest.Commitment);
        Assert.Equal(resultAlreadyCommitted ? 1 : 0,
            before.Count(static value => value.Kind == SessionEventKind.ToolResultObserved));

        string controlPath = Directory.GetFiles(Path.Combine(_root, "control"),
            "control.json", SearchOption.AllDirectories).Single();
        byte[] controlBytes = File.ReadAllBytes(controlPath);
        ControlHeadRef controlHead = ReadControlHead(refId);
        using JsonDocument control = JsonDocument.Parse(controlBytes);
        Assert.Equal(2, control.RootElement.GetProperty("schemaVersion").GetInt32());
        JsonElement receipt = Assert.Single(control.RootElement.GetProperty("operationReceipts").EnumerateArray());
        string operationKey = receipt.GetProperty("operationKey").GetString()!;
        Assert.Equal(1, controlHead.Generation);
        byte[]? oldResult = resultAlreadyCommitted
            ? File.ReadAllBytes(Path.Combine(_root, "expected-tool-result.json")) : null;
        if (oldResult is not null) {
            Assert.Equal(oldResult, ReadReceiptEventPayload(before.Single(static value =>
                value.Kind == SessionEventKind.ToolResultObserved).Address));
            // This boundary needs no executable tool profile. Removing the file and
            // omitting --admission makes accidental frozen-tool binding fail before
            // it could replay a receipt and hide behind unchanged Control bytes.
            File.Delete(Path.Combine(_root, "admission.json"));
        }

        var provider = new DeterministicCompletionClientFactory();
        Assert.Equal(0, ResumeControlReceiptFixture(provider, refId,
            includeAdmission: !resultAlreadyCommitted));
        CompletionRequest request = Assert.Single(provider.Requests);
        Assert.Equal(0, provider.RecapRequestCount);
        Assert.Equal(controlHead, ReadControlHead(refId));
        Assert.Equal(controlBytes, File.ReadAllBytes(controlPath));

        (SessionJournalAuditScanResult completed, List<SessionJournalAuditEvent> after) = AuditReceiptFixture();
        Assert.Equal(SessionExecutionPhase.Idle, completed.ExecutionStateAtCapturedHead.Phase);
        Assert.Equal(1, completed.ExecutionStateAtCapturedHead.ToolExecutionSequenceCheckpoint);
        Assert.Single(after, static value => value.Kind == SessionEventKind.ToolExecutionStarted);
        SessionJournalAuditEvent resultEvent = Assert.Single(after, static value =>
            value.Kind == SessionEventKind.ToolResultObserved);
        byte[] resultBytes = ReadReceiptEventPayload(resultEvent.Address);
        using JsonDocument result = JsonDocument.Parse(resultBytes);
        JsonElement body = result.RootElement.GetProperty("body");
        Assert.Equal("success", body.GetProperty("status").GetString());
        string outputText = Assert.Single(body.GetProperty("blocks").EnumerateArray())
            .GetProperty("content").GetString()!;
        using JsonDocument output = JsonDocument.Parse(outputText);
        if (resultAlreadyCommitted) {
            Assert.Equal(oldResult, resultBytes);
            Assert.Equal(1, output.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("applied", output.RootElement.GetProperty("status").GetString());
            Assert.True(output.RootElement.TryGetProperty("resultIdentity", out _));
        }
        else {
            Assert.Equal(2, output.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("replayed", output.RootElement.GetProperty("status").GetString());
            Assert.Equal(operationKey, output.RootElement.GetProperty("operationKey").GetString());
            Assert.False(output.RootElement.TryGetProperty("resultIdentity", out _));
        }
        ToolResultsMessage visibleResult = Assert.Single(request.PromptPrefix.SharedContextMessages
            .OfType<ToolResultsMessage>());
        Assert.Equal(outputText, Assert.IsType<ToolResultBlock.Text>(
            Assert.Single(Assert.Single(visibleResult.Results).Blocks)).Content);
        SessionJournalAuditEvent prepared = after.Last(static value =>
            value.Kind == SessionEventKind.CompletionRequestPrepared);
        Assert.Equal(9, prepared.BodySchemaVersion);
        Assert.NotEqual(legacyPrepared.Address, prepared.Address);
        Assert.Equal(legacyPreparedBytes, ReadReceiptEventPayload(legacyPrepared.Address));
        Assert.Equal(legacyCommitment, ReconstructReceiptRequest(legacyPrepared.Address).Manifest.Commitment);
        SessionPreparedRequestReconstruction reconstruction = ReconstructReceiptRequest(prepared.Address);
        Assert.Equal(SessionRequestCanonicalizer.Canonicalize(request), reconstruction.CanonicalBytes);
        Assert.Null(reconstruction.Manifest.Commitment);
        SessionJournalAuditEvent started = Assert.Single(after, value =>
            value.Kind == SessionEventKind.CompletionAttemptStarted && value.Parent == prepared.Address);
        CompletionAttemptStartedBody evidence = Assert.IsType<CompletionAttemptStartedBody>(
            SessionEventCodec.Decode(SessionEventKind.CompletionAttemptStarted,
                ReadReceiptEventPayload(started.Address), out int startedVersion));
        Assert.Equal(2, startedVersion);
        Assert.Equal(SessionRequestManifestDefaults.CanonicalRequestCodecId, evidence.CanonicalRequestCodecId);
        Assert.Equal(SessionRequestCanonicalizer.CreateCommitment(request), evidence.Commitment);
    }

    [Fact]
    public void LegacyToolResultInFrozenPreparedRetainsExactRequestAndAudit() {
        ExtractControlReceiptFixture("prepared-with-old-tool-result");
        string oldStorePath = Path.Combine(_root, "derived", "recap-grid", "v1", "grid.sqlite");
        byte[] oldStoreBytes = File.ReadAllBytes(oldStorePath);
        Assert.Equal(2, Assert.IsType<RecapGridStoreOpenResult.UnsupportedSchema>(
            RecapGridStoreFactory.Open(_root)).SchemaVersion);
        (SessionJournalAuditScanResult initial, List<SessionJournalAuditEvent> before) = AuditReceiptFixture();
        Assert.Equal(SessionEventKind.CompletionRequestPrepared, initial.ExecutionStateAtCapturedHead.HeadKind);
        string refId = initial.BranchRefId.ToHexString();
        EventAddress prepared = initial.CapturedHead!.Value;
        EventAddress result = Assert.Single(before, static value =>
            value.Kind == SessionEventKind.ToolResultObserved).Address;
        byte[] expectedRequest = File.ReadAllBytes(Path.Combine(_root, "expected-request.json"));
        byte[] expectedResult = File.ReadAllBytes(Path.Combine(_root, "expected-tool-result.json"));
        string expectedInputs = File.ReadAllText(Path.Combine(_root, "expected-exact-inputs.json"));
        string controlPath = Directory.GetFiles(Path.Combine(_root, "control"),
            "control.json", SearchOption.AllDirectories).Single();
        byte[] originalControl = File.ReadAllBytes(controlPath);
        SessionPreparedRequestReconstruction frozen = ReconstructReceiptRequest(prepared);
        Assert.Equal(expectedRequest, frozen.CanonicalBytes);
        Assert.Equal(expectedInputs, JsonSerializer.Serialize(frozen.Manifest.Plan.ExactContextInputs));
        Assert.Equal(SessionRequestCanonicalizer.CreateCommitment(frozen.Request), frozen.Manifest.Commitment);
        Assert.Equal(expectedResult, ReadReceiptEventPayload(result));

        var provider = new DeterministicCompletionClientFactory();
        Assert.Equal(0, ResumeControlReceiptFixture(provider, refId));
        Assert.Equal(expectedRequest, SessionRequestCanonicalizer.Canonicalize(Assert.Single(provider.Requests)));
        Assert.Equal(0, provider.RecapRequestCount);
        Assert.Equal(originalControl, File.ReadAllBytes(controlPath));
        Assert.Equal(oldStoreBytes, File.ReadAllBytes(oldStorePath));
        Assert.Equal(expectedResult, ReadReceiptEventPayload(result));
        SessionPreparedRequestReconstruction reopened = ReconstructReceiptRequest(prepared);
        Assert.Equal(expectedRequest, reopened.CanonicalBytes);
        Assert.Equal(frozen.Manifest.Commitment, reopened.Manifest.Commitment);
        Assert.Equal(expectedInputs, JsonSerializer.Serialize(reopened.Manifest.Plan.ExactContextInputs));
        (SessionJournalAuditScanResult completed, List<SessionJournalAuditEvent> after) = AuditReceiptFixture();
        Assert.Equal(SessionExecutionPhase.Idle, completed.ExecutionStateAtCapturedHead.Phase);
        Assert.Single(after, static value => value.Kind == SessionEventKind.ToolResultObserved);
        Assert.Equal(1, completed.ExecutionStateAtCapturedHead.ToolExecutionSequenceCheckpoint);
    }

    private void ExtractControlReceiptFixture(string name, bool upgradeTimeline = true) {
        ZipFile.ExtractToDirectory(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "ControlReceiptV2", name + ".zip"), _root);
        if (!OperatingSystem.IsWindows()) {
            // ZIP extraction does not restore directory modes. Cadence checks its
            // owner-only directory/file contract before opening persisted state.
            foreach (string directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                         .Prepend(_root)) {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        if (upgradeTimeline) {
            string locatorPath = Directory.GetFiles(Path.Combine(_root, "derived", "history-timeline"),
                "locator.json", SearchOption.AllDirectories).Single();
            ActiveTimelineLocator locator = HistoryTimelineCanonicalCodec.DecodeActiveTimelineLocator(
                File.ReadAllBytes(locatorPath));
            Assert.IsType<HistoryTimelineUpgradeResult.Upgraded>(
                HistoryTimelineMaintenance.UpgradeSchemaV2(_root, locator.RefId, locator.ActiveTimelineId));
        }
    }

    private int ResumeControlReceiptFixture(
        DeterministicCompletionClientFactory provider,
        string refId,
        bool includeAdmission = true
    ) {
        var arguments = new List<string> {
            "run-online-turn", "--input", _root,
            "--branch", SessionJournalDefaults.MainBranchName, "--confirm-ref", refId,
            "--connection", "test",
            "--connections", Path.Combine(_root, "connections.json"),
            "--routes", Path.Combine(_root, "routes.json")
        };
        if (includeAdmission) {
            arguments.AddRange(["--admission", Path.Combine(_root, "admission.json")]);
        }
        (int code, JsonElement report) = RunCapturedWithFactory(provider, arguments.ToArray());
        Assert.True(code == 0, report.GetRawText());
        return code;
    }

    private (SessionJournalAuditScanResult, List<SessionJournalAuditEvent>) AuditReceiptFixture() {
        using SessionJournalEngine engine = SessionJournalEngine.OpenReadOnly(_root);
        var events = new List<SessionJournalAuditEvent>();
        SessionJournalAuditScanResult full = engine.ScanCheckedAuditEvents(events.Add);
        SessionSelectedLineageAuditSession selected = engine.BeginSelectedLineageAudit();
        while (!selected.IsCaptureComplete) {
            _ = selected.ReadNextPage(SessionSelectedLineageAuditLimits.MaximumPageEventCount);
        }
        _ = selected.Complete();
        Assert.Equal(full.EventCount, selected.EventCount);
        return (full, events);
    }

    private byte[] ReadReceiptEventPayload(EventAddress address) {
        using var journal = Atelia.EventJournal.EventJournal.OpenExisting(_root);
        using EventFrame frame = journal.ReadEvent(address).Unwrap();
        return frame.Payload.ToArray();
    }

    private SessionPreparedRequestReconstruction ReconstructReceiptRequest(EventAddress prepared) {
        using var journal = Atelia.EventJournal.EventJournal.OpenExisting(_root);
        return SessionPreparedRequestReconstructor.Reconstruct(journal, prepared);
    }
}
