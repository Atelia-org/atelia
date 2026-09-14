using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.HistoryTimeline;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Control.Tests;

public sealed partial class ControlVerticalTests {
    private static string LegacyFixtureDirectory => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "LegacyV2");

    private string ExtractLegacyControl() {
        string path = NewPath();
        ZipFile.ExtractToDirectory(Path.Combine(LegacyFixtureDirectory, "repository.zip"), path);
        ControlState original = ControlState.Decode(
            File.ReadAllBytes(Path.Combine(LegacyFixtureDirectory, "control.json")));
        Assert.IsType<HistoryTimelineUpgradeResult.Upgraded>(
            HistoryTimelineMaintenance.UpgradeSchemaV2(path, original.Head.RefId,
                original.Head.TimelineId));
        return path;
    }

    [Fact]
    public void LegacyV2ReadReplayExportBackupAndMutationPreserveReceiptAuthority() {
        string path = ExtractLegacyControl();
        using SessionJournalEngine journal = SessionJournalEngine.Open(path);
        Values values = ValuesFor(path, journal);
        byte[] original = File.ReadAllBytes(Path.Combine(LegacyFixtureDirectory, "control.json"));
        ControlState legacy = ControlState.Decode(original);
        ControlHeadRef head = legacy.Head;
        string statePath = ControlStatePath(path, journal.BranchRefId, head);
        Assert.Equal(original, File.ReadAllBytes(statePath));
        Assert.Equal(original, legacy.CanonicalBytes);
        RecapGridControlOperation first = RecapGridControlOperation.Create(
            "legacy-v2-operation", 1, OperationRuntimeDigest);
        var bundle = new RecapGridControlRegistrationBundle([values.Family], [], []);

        using (RecapGridControlHandle handle = Assert.IsType<RecapGridControlOpenResult.Opened>(
            RecapGridControlFactory.Open(path, journal.BranchRefId, values.Admission)).Handle) {
            var replay = Assert.IsType<RecapGridControlOperationResult.Replayed>(
                handle.Coordinator.ApplyRegistrationBundle(head, values.TimelineHead, first, bundle));
            Assert.Equal(first.OperationKey, replay.OperationKey);
            Assert.Equal(head, replay.CurrentHead);
            Assert.False(replay.HeadAdvancedSinceApply);
            Assert.IsType<RecapGridControlPutResult.AlreadyPresent>(
                handle.Coordinator.PutFamilyDefinition(head, values.Family));
            Assert.Equal(original, File.ReadAllBytes(statePath));
        }
        var export = Assert.IsType<RecapGridControlExportResult.Available>(
            RecapGridControlMaintenance.Export(path, journal.BranchRefId));
        Assert.Equal(head, export.Snapshot.Head);
        Assert.Equal(original, export.CanonicalState);
        string backup = Path.Combine(path, "read-only-backup");
        Assert.IsType<RecapGridControlBackupResult.Created>(RecapGridControlMaintenance.Backup(
            path, journal.BranchRefId, head, backup));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(backup, "control.json")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(LegacyFixtureDirectory, "manifest.json")),
            File.ReadAllBytes(Path.Combine(backup, "manifest.json")));
        Assert.Equal(original, File.ReadAllBytes(statePath));

        RecapGridControlOperation second = RecapGridControlOperation.Create(
            "current-v3-operation", 2, OperationRuntimeDigest);
        using (RecapGridControlHandle handle = Assert.IsType<RecapGridControlOpenResult.Opened>(
            RecapGridControlFactory.Open(path, journal.BranchRefId, values.Admission)).Handle) {
            var applied = Assert.IsType<RecapGridControlOperationResult.Applied>(
                handle.Coordinator.ApplyRegistrationBundle(head, values.TimelineHead, second, bundle));
            Assert.Equal(second.OperationKey, applied.OperationKey);
            Assert.Equal(head.Generation + 1, applied.Head.Generation);
            head = applied.Head;
        }
        byte[] upgraded = File.ReadAllBytes(statePath);
        Assert.StartsWith("{\"schemaVersion\":4,", Encoding.UTF8.GetString(upgraded));
        Assert.DoesNotContain("resultIdentity", Encoding.UTF8.GetString(upgraded));
        using (RecapGridControlHandle handle = Assert.IsType<RecapGridControlOpenResult.Opened>(
            RecapGridControlFactory.Open(path, journal.BranchRefId, values.Admission)).Handle) {
            var replay = Assert.IsType<RecapGridControlOperationResult.Replayed>(
                handle.Coordinator.ApplyRegistrationBundle(legacy.Head, values.TimelineHead, first, bundle));
            Assert.Equal(head, replay.CurrentHead);
            Assert.Equal(legacy.Head.Generation, replay.OriginalGeneration);
            Assert.True(replay.HeadAdvancedSinceApply);
        }
        Assert.Equal(upgraded, File.ReadAllBytes(statePath));
        head = Assert.IsType<RecapGridControlAdminResult.Applied>(RecapGridControlMaintenance.Restore(
            path, journal.BranchRefId, head, Path.Combine(path, "legacy-backup"))).Head;
        ControlState restored = ControlState.Decode(File.ReadAllBytes(statePath));
        Assert.Equal(2, restored.OperationReceipts.Count);
        Assert.Equal(legacy.OperationReceipts[first.OperationKey], restored.OperationReceipts[first.OperationKey]);
        Assert.Contains(second.OperationKey, restored.OperationReceipts.Keys);
        head = Assert.IsType<RecapGridControlAdminResult.Applied>(RecapGridControlMaintenance.Reinitialize(
            path, journal.BranchRefId, head)).Head;
        byte[] reinitialized = File.ReadAllBytes(statePath);
        using (RecapGridControlHandle handle = Assert.IsType<RecapGridControlOpenResult.Opened>(
            RecapGridControlFactory.Open(path, journal.BranchRefId, values.Admission)).Handle) {
            foreach (RecapGridControlOperation operation in new[] { first, second }) {
                var replay = Assert.IsType<RecapGridControlOperationResult.Replayed>(
                    handle.Coordinator.ApplyRegistrationBundle(legacy.Head, values.TimelineHead, operation, bundle));
                Assert.Equal(operation.OperationKey, replay.OperationKey);
                Assert.True(replay.InstanceReplaced);
            }
        }
        Assert.Equal(reinitialized, File.ReadAllBytes(statePath));
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("runtime")]
    [InlineData("command")]
    public void LegacyReceiptReplayConflictsBeforeStaleAndLeavesBytesUntouched(string field) {
        string path = ExtractLegacyControl();
        using SessionJournalEngine journal = SessionJournalEngine.Open(path);
        Values values = ValuesFor(path, journal);
        ControlState legacy = ControlState.Decode(File.ReadAllBytes(Path.Combine(LegacyFixtureDirectory, "control.json")));
        string statePath = ControlStatePath(path, journal.BranchRefId, legacy.Head);
        var operation = RecapGridControlOperation.Create("legacy-v2-operation",
            field == "sequence" ? 2 : 1, field == "runtime" ? new string('b', 64) : OperationRuntimeDigest);
        var bundle = field == "command"
            ? new RecapGridControlRegistrationBundle([values.Family], [values.Definition], [])
            : new RecapGridControlRegistrationBundle([values.Family], [], []);
        using RecapGridControlHandle handle = Assert.IsType<RecapGridControlOpenResult.Opened>(
            RecapGridControlFactory.Open(path, journal.BranchRefId, values.Admission)).Handle;
        Assert.IsType<RecapGridControlOperationResult.Conflict>(
            handle.Coordinator.ApplyRegistrationBundle(legacy.Head, values.TimelineHead, operation, bundle));
        Assert.Equal(legacy.CanonicalBytes, File.ReadAllBytes(statePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyUpgradePublishSettlesOnlyWhenReceiptCanBeObserved(bool hidePublishedState) {
        string path = ExtractLegacyControl();
        using SessionJournalEngine journal = SessionJournalEngine.Open(path);
        Values values = ValuesFor(path, journal);
        ControlState legacy = ControlState.Decode(File.ReadAllBytes(Path.Combine(LegacyFixtureDirectory, "control.json")));
        string statePath = ControlStatePath(path, journal.BranchRefId, legacy.Head);
        byte[]? published = null;
        var hooks = new ControlPersistenceTestHooks(AfterStatePublish: _ => {
            published = File.ReadAllBytes(statePath);
            if (hidePublishedState) {
                // Simulate failure to observe the committed file during settlement.
                File.WriteAllText(statePath, "unreadable injected state");
            }
            throw new IOException("injected after publish");
        });
        var operation = RecapGridControlOperation.Create("publish-v3-operation", 2, OperationRuntimeDigest);
        var bundle = new RecapGridControlRegistrationBundle([values.Family], [], []);
        using (RecapGridControlHandle handle = Assert.IsType<RecapGridControlOpenResult.Opened>(
            RecapGridControlFactory.OpenForTest(path, journal.BranchRefId, values.Admission, hooks)).Handle) {
            RecapGridControlOperationResult result = handle.Coordinator.ApplyRegistrationBundle(
                legacy.Head, values.TimelineHead, operation, bundle);
            if (hidePublishedState) {
                var uncertain = Assert.IsType<RecapGridControlOperationResult.CommitIndeterminate>(result);
                Assert.Equal(operation.OperationKey, uncertain.OperationKey);
                Assert.Null(uncertain.Observed);
            }
            else {
                var applied = Assert.IsType<RecapGridControlOperationResult.Applied>(result);
                Assert.Equal(operation.OperationKey, applied.OperationKey);
                Assert.Equal(legacy.Head.Generation + 1, applied.Head.Generation);
            }
        }
        Assert.NotNull(published);
        File.WriteAllBytes(statePath, published);
        using RecapGridControlHandle retry = Assert.IsType<RecapGridControlOpenResult.Opened>(
            RecapGridControlFactory.Open(path, journal.BranchRefId, values.Admission)).Handle;
        var replay = Assert.IsType<RecapGridControlOperationResult.Replayed>(
            retry.Coordinator.ApplyRegistrationBundle(legacy.Head, values.TimelineHead, operation, bundle));
        Assert.Equal(operation.OperationKey, replay.OperationKey);
        Assert.Equal(legacy.Head.Generation + 1, replay.CurrentHead.Generation);
        Assert.Equal(published, File.ReadAllBytes(statePath));
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("runtime")]
    [InlineData("command")]
    [InlineData("instance")]
    [InlineData("generation")]
    public void RestoreRejectsConflictingLegacyAndCurrentReceipts(string field) {
        string path = ExtractLegacyControl();
        using SessionJournalEngine journal = SessionJournalEngine.Open(path);
        ControlState legacy = ControlState.Decode(File.ReadAllBytes(Path.Combine(LegacyFixtureDirectory, "control.json")));
        ControlOperationReceipt receipt = Assert.Single(legacy.OperationReceipts).Value;
        ControlOperationReceipt changed = field switch {
            "sequence" => receipt with { ExecutionSequence = 2 },
            "runtime" => receipt with { RuntimeIdentityDigest = new string('b', 64) },
            "command" => receipt with { CommandDigest = new string('c', 64) },
            "instance" => receipt with { OriginalInstanceId = ControlInstanceId.Generate() },
            _ => receipt with { OriginalGeneration = 2 }
        };
        ControlState current = legacy.WithGenerationAndReceipts(legacy.Head.InstanceId, 2,
            new Dictionary<string, ControlOperationReceipt> { [changed.OperationKey] = changed });
        string statePath = ControlStatePath(path, journal.BranchRefId, current.Head);
        File.WriteAllBytes(statePath, current.CanonicalBytes);
        var conflict = Assert.IsType<RecapGridControlAdminResult.Invalid>(RecapGridControlMaintenance.Restore(
            path, journal.BranchRefId, current.Head, Path.Combine(path, "legacy-backup")));
        Assert.Equal("ControlOperationReceiptConflict", conflict.Code);
        Assert.Equal(current.CanonicalBytes, File.ReadAllBytes(statePath));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("number")]
    [InlineData("empty")]
    [InlineData("uppercase")]
    [InlineData("duplicate")]
    [InlineData("digest")]
    public void LegacyV2RejectsMalformedOldFieldAndDigest(string mutation) {
        string original = File.ReadAllText(Path.Combine(LegacyFixtureDirectory, "control.json"));
        const string value = "17b4909f313d1c0a5eebd0f1d6664b241109490afffba6799c5fa9e30c0dedc9";
        string property = $"\"resultIdentity\":\"{value}\"";
        string replacement = mutation switch {
            "null" => "\"resultIdentity\":null",
            "number" => "\"resultIdentity\":1",
            "empty" => "\"resultIdentity\":\"\"",
            "uppercase" => $"\"resultIdentity\":\"{value.ToUpperInvariant()}\"",
            "duplicate" => property + "," + property,
            _ => property
        };
        string malformed = mutation switch {
            "missing" => original.Replace(property + ",", "", StringComparison.Ordinal),
            "digest" => original.Replace("9198e3aff356f36ae540934b73665e7f351433f2792b419b047eb7b415836dd5", new string('0', 64), StringComparison.Ordinal),
            _ => original.Replace(property, replacement, StringComparison.Ordinal)
        };
        Assert.NotEqual(original, malformed);
        Assert.Throws<ControlStoreException>(() => ControlState.Decode(Encoding.UTF8.GetBytes(malformed)));
    }

    [Fact]
    public void LegacyDerivedResultIsOnlyWireEvidenceAndDoesNotEnterReceiptEquality() {
        byte[] original = File.ReadAllBytes(Path.Combine(LegacyFixtureDirectory, "control.json"));
        LegacyControlFileV2Dto wire = JsonSerializer.Deserialize<LegacyControlFileV2Dto>(original, ControlJson.Options)!;
        LegacyControlOperationReceiptV2Dto receipt = Assert.Single(wire.OperationReceipts);
        wire = wire with { OperationReceipts = [receipt with { ResultIdentity = new string('f', 64) }] };
        byte[] changed = RecodeLegacyWire(wire);
        ControlState decoded = ControlState.Decode(changed);
        Assert.Equal(Assert.Single(ControlState.Decode(original).OperationReceipts).Value,
            Assert.Single(decoded.OperationReceipts).Value);
        Assert.Equal(changed, decoded.CanonicalBytes);
        Assert.NotEqual(ControlState.Decode(original).Head, decoded.Head);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-digest")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void LegacyDerivedFieldShapeIsCheckedAfterValidSourceDigest(string? resultIdentity) {
        byte[] original = File.ReadAllBytes(Path.Combine(LegacyFixtureDirectory, "control.json"));
        LegacyControlFileV2Dto wire = JsonSerializer.Deserialize<LegacyControlFileV2Dto>(original, ControlJson.Options)!;
        wire = wire with { OperationReceipts = [wire.OperationReceipts[0] with { ResultIdentity = resultIdentity! }] };
        Assert.Equal("ControlOperationReceiptInvalid", Assert.Throws<ControlStoreException>(() =>
            ControlState.Decode(RecodeLegacyWire(wire))).Code);
    }

    // Resigns deliberate mutations of the fixed old fixture, never creates the historical fixture itself.
    private static byte[] RecodeLegacyWire(LegacyControlFileV2Dto wire) {
        var body = new LegacyControlBodyV2Dto(wire.SchemaVersion, wire.Head.InstanceId, wire.Head.RefId,
            wire.Head.TimelineId, wire.Head.Generation, wire.Head.ActiveRecipeDigest,
            wire.Families, wire.Definitions, wire.Recipes, wire.OperationReceipts);
        wire = wire with { Head = wire.Head with {
            StateDigest = Hash("atelia.recap-grid.control-state.v2",
                JsonSerializer.SerializeToUtf8Bytes(body, ControlJson.Options))
        } };
        return JsonSerializer.SerializeToUtf8Bytes(wire, ControlJson.Options);
    }

    [Fact]
    public void RestoreUnionsBackupOnlyLegacyAndCurrentOnlyReceipts() {
        string path = ExtractLegacyControl();
        using SessionJournalEngine journal = SessionJournalEngine.Open(path);
        Values values = ValuesFor(path, journal);
        ControlState legacy = ControlState.Decode(File.ReadAllBytes(Path.Combine(LegacyFixtureDirectory, "control.json")));
        var second = RecapGridControlOperation.Create("current-only-operation", 2, OperationRuntimeDigest);
        ControlOperationReceipt firstReceipt = Assert.Single(legacy.OperationReceipts).Value;
        ControlOperationReceipt secondReceipt = firstReceipt with {
            OperationKey = second.OperationKey, ExecutionSequence = 2, OriginalGeneration = 2
        };
        ControlState current = legacy.WithGenerationAndReceipts(legacy.Head.InstanceId, 2,
            new Dictionary<string, ControlOperationReceipt> { [second.OperationKey] = secondReceipt });
        string statePath = ControlStatePath(path, journal.BranchRefId, current.Head);
        File.WriteAllBytes(statePath, current.CanonicalBytes);
        var restored = Assert.IsType<RecapGridControlAdminResult.Applied>(RecapGridControlMaintenance.Restore(
            path, journal.BranchRefId, current.Head, Path.Combine(path, "legacy-backup")));
        byte[] after = File.ReadAllBytes(statePath);
        ControlState merged = ControlState.Decode(after);
        Assert.Equal(2, merged.OperationReceipts.Count);
        Assert.Equal(firstReceipt, merged.OperationReceipts[firstReceipt.OperationKey]);
        Assert.Equal(secondReceipt, merged.OperationReceipts[secondReceipt.OperationKey]);
        using RecapGridControlHandle handle = Assert.IsType<RecapGridControlOpenResult.Opened>(
            RecapGridControlFactory.Open(path, journal.BranchRefId, values.Admission)).Handle;
        var bundle = new RecapGridControlRegistrationBundle([values.Family], [], []);
        foreach (RecapGridControlOperation operation in new[] {
            RecapGridControlOperation.Create("legacy-v2-operation", 1, OperationRuntimeDigest), second
        }) {
            var replay = Assert.IsType<RecapGridControlOperationResult.Replayed>(
                handle.Coordinator.ApplyRegistrationBundle(legacy.Head, values.TimelineHead, operation, bundle));
            Assert.Equal(operation.OperationKey, replay.OperationKey);
            Assert.Equal(restored.Head, replay.CurrentHead);
        }
        Assert.Equal(after, File.ReadAllBytes(statePath));
    }

    [Fact]
    public void CurrentV4RejectsLegacyReceiptField() {
        ControlState legacy = ControlState.Decode(File.ReadAllBytes(Path.Combine(LegacyFixtureDirectory, "control.json")));
        ControlState current = legacy.WithGenerationAndReceipts(legacy.Head.InstanceId, 2, legacy.OperationReceipts);
        string malformed = Encoding.UTF8.GetString(current.CanonicalBytes).Replace(
            "\"originalInstanceId\":", "\"resultIdentity\":\"" + new string('a', 64) + "\",\"originalInstanceId\":", StringComparison.Ordinal);
        Assert.Throws<ControlStoreException>(() => ControlState.Decode(Encoding.UTF8.GetBytes(malformed)));
    }
}
