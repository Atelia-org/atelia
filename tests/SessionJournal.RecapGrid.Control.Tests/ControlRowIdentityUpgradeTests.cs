using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Control.Tests;

public sealed partial class ControlVerticalTests {
    private static string RowIdentityFixtureDirectory => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "RowIdentityV2");

    private static byte[] LegacyV3ControlBytes() {
        using ZipArchive archive = ZipFile.OpenRead(
            Path.Combine(RowIdentityFixtureDirectory, "control-backup.zip"));
        using Stream source = archive.GetEntry("control.json")!.Open();
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        return bytes.ToArray();
    }

    [Fact]
    public void LegacyV3NonemptyBootstrapPreservesSourceAndReceiptsUntilNormalMutation() {
        string path = NewPath();
        ZipFile.ExtractToDirectory(Path.Combine(RowIdentityFixtureDirectory, "repository.zip"), path);
        byte[] original = LegacyV3ControlBytes();
        ControlState legacy = ControlState.Decode(original);
        Assert.Equal(3, legacy.OperationReceipts.Count);
        Assert.Equal(original, legacy.CanonicalBytes);
        using JsonDocument expected = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(RowIdentityFixtureDirectory, "expected.json")));
        RegisteredGridRecipe registered = legacy.Recipes[
            expected.RootElement.GetProperty("newRecipeDigest").GetString()!];
        Assert.Equal(registered.Recipe.BootstrapThroughRowId, registered.Bootstrap.RowId);
        TimelineHeadRef timelineHead = registered.Bootstrap.TimelineHead;
        Assert.Equal(timelineHead, Assert.IsType<HistoryTimelineUpgradeResult.Upgraded>(
            HistoryTimelineMaintenance.UpgradeSchemaV2(path, legacy.Head.RefId,
                legacy.Head.TimelineId)).Head);
        string statePath = ControlStatePath(path, legacy.Head.RefId, legacy.Head);
        Assert.Equal(original, File.ReadAllBytes(statePath));
        var admission = new RecapGridControlAdmission(RecapGridControlPermission.All,
            legacy.Families.Values.Select(value => value.Digest).ToArray(),
            legacy.Definitions.Values.Select(value => value.Capability.CapabilityFingerprint).Distinct().ToArray(),
            [ContextHeaderCarrier.System], ["case."], 1_000, 10_000);
        using JsonDocument commands = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(RowIdentityFixtureDirectory, "commands.json")));
        using HistoryTimelineReaderHandle timeline = Assert.IsType<HistoryTimelineReaderOpenResult.Opened>(
            HistoryTimelineMaintenance.OpenReader(path, legacy.Head.RefId)).Handle;
        HistoryTimelineAncestorWitness witness = Assert.IsType<HistoryTimelineReaderRowResult.Selected>(
            timeline.Reader.ReadSelectedRow(timelineHead, registered.Bootstrap.RowId!.Value)).Row.Witness;
        using (RecapGridControlHandle control = Assert.IsType<RecapGridControlOpenResult.Opened>(
            RecapGridControlFactory.Open(path, legacy.Head.RefId, admission)).Handle) {
            foreach (JsonElement command in commands.RootElement.EnumerateArray()) {
                string name = command.GetProperty("name").GetString()!;
                if (name == "empty") {
                    Assert.Throws<ArgumentException>(() => new RecapGridControlRegistrationBundle([], [], []));
                    continue;
                }
                if (name == "promotion") {
                    Assert.Equal(command.GetProperty("commandDigest").GetString(),
                        ControlOperationCanonicalizer.PromotionDigest(registered.Recipe.Digest));
                    continue;
                }
                RecapGridControlRegistrationBundle bundle = DecodeBaselineBundle(command,
                    name == "recipe-nonempty-witness" ? witness : null);
                byte[] oldCommand = Convert.FromBase64String(command.GetProperty("commandBase64").GetString()!);
                string oldDigest = command.GetProperty("commandDigest").GetString()!;
                Assert.Equal(oldDigest, Hash("atelia.recap-grid.control-command.registration.v1", oldCommand));
                if (bundle.Recipes.Count == 0) {
                    Assert.Equal(oldCommand, bundle.ToCanonicalCommandBytes());
                    Assert.Equal(oldDigest, bundle.CanonicalCommandDigest);
                }
                else {
                    Assert.NotEqual(oldCommand, bundle.ToCanonicalCommandBytes());
                    Assert.NotEqual(oldDigest, bundle.CanonicalCommandDigest);
                    Assert.DoesNotContain("bootstrapDescriptorDigest", Encoding.UTF8.GetString(bundle.ToCanonicalCommandBytes()));
                }
                if (!command.GetProperty("applied").GetBoolean()) continue;
                var operation = RecapGridControlOperation.Create(command.GetProperty("operationId").GetString()!,
                    command.GetProperty("executionSequence").GetInt64(),
                    command.GetProperty("runtimeIdentityDigest").GetString()!);
                Assert.Equal(command.GetProperty("operationKey").GetString(), operation.OperationKey);
                RecapGridControlOperationResult result = control.Coordinator.ApplyRegistrationBundle(
                    legacy.Head, timelineHead, operation, bundle);
                if (bundle.Recipes.Count == 0) {
                    Assert.IsType<RecapGridControlOperationResult.Replayed>(result);
                }
                else {
                    // Changed commands must conflict with the exact original receipt;
                    // the upgrade cannot silently reinterpret a pending registration.
                    Assert.IsType<RecapGridControlOperationResult.Conflict>(result);
                }
                Assert.Equal(original, File.ReadAllBytes(statePath));
            }
        }
        var export = Assert.IsType<RecapGridControlExportResult.Available>(
            RecapGridControlMaintenance.Export(path, legacy.Head.RefId));
        Assert.Equal(legacy.Head, export.Snapshot.Head);
        Assert.Equal(original, export.CanonicalState);
        string readBackup = Path.Combine(path, "read-backup");
        Assert.IsType<RecapGridControlBackupResult.Created>(RecapGridControlMaintenance.Backup(
            path, legacy.Head.RefId, legacy.Head, readBackup));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(readBackup, "control.json")));
        var nextOperation = RecapGridControlOperation.Create("v4-family-noop", 20, OperationRuntimeDigest);
        ControlHeadRef current;
        using (RecapGridControlHandle control = Assert.IsType<RecapGridControlOpenResult.Opened>(
            RecapGridControlFactory.Open(path, legacy.Head.RefId, admission)).Handle) {
            current = Assert.IsType<RecapGridControlOperationResult.Applied>(
                control.Coordinator.ApplyRegistrationBundle(legacy.Head, timelineHead, nextOperation,
                    new RecapGridControlRegistrationBundle([legacy.Families.Values.First()], [], []))).Head;
        }
        ControlState upgraded = ControlState.Decode(File.ReadAllBytes(statePath));
        Assert.Equal(legacy.Head.Generation + 1, current.Generation);
        Assert.Equal(current, upgraded.Head);
        Assert.StartsWith("{\"schemaVersion\":4,", Encoding.UTF8.GetString(upgraded.CanonicalBytes));
        Assert.DoesNotContain("descriptorDigest", Encoding.UTF8.GetString(upgraded.CanonicalBytes));
        foreach (var pair in legacy.OperationReceipts) Assert.Equal(pair.Value, upgraded.OperationReceipts[pair.Key]);
        Assert.Equal(registered.Recipe.ToCanonicalBytes(), upgraded.Recipes[registered.Recipe.Digest.Value].Recipe.ToCanonicalBytes());
        Assert.Equal(registered.Bootstrap.TimelineHead, upgraded.Recipes[registered.Recipe.Digest.Value].Bootstrap.TimelineHead);
        string oldBackup = Path.Combine(path, "original-v3-backup");
        ZipFile.ExtractToDirectory(Path.Combine(RowIdentityFixtureDirectory, "control-backup.zip"), oldBackup);
        current = Assert.IsType<RecapGridControlAdminResult.Applied>(RecapGridControlMaintenance.Restore(
            path, legacy.Head.RefId, current, oldBackup)).Head;
        ControlState restored = ControlState.Decode(File.ReadAllBytes(statePath));
        Assert.Equal(current, restored.Head);
        Assert.Equal(4, restored.OperationReceipts.Count);
        foreach (var pair in upgraded.OperationReceipts) Assert.Equal(pair.Value, restored.OperationReceipts[pair.Key]);
        Assert.Equal(registered.Bootstrap.RowId, restored.Recipes[registered.Recipe.Digest.Value].Bootstrap.RowId);
        Assert.StartsWith("{\"schemaVersion\":4,", Encoding.UTF8.GetString(restored.CanonicalBytes));
        using RecapGridControlHandle reopened = Assert.IsType<RecapGridControlOpenResult.Opened>(
            RecapGridControlFactory.Open(path, legacy.Head.RefId, admission)).Handle;
        Assert.IsType<RecapGridControlActivateResult.Applied>(reopened.Coordinator.CompareExchangeActiveRecipe(
            current, timelineHead, registered.Recipe.Digest, RecapGridControlActivationPurpose.Direct));
    }

    private static RecapGridControlRegistrationBundle DecodeBaselineBundle(
        JsonElement command, HistoryTimelineAncestorWitness? witness) => new(
            command.GetProperty("familyCanonicalBase64").EnumerateArray().Select(value =>
                FamilyDefinition.DecodeCanonical(Convert.FromBase64String(value.GetString()!))).ToArray(),
            command.GetProperty("definitionCanonicalBase64").EnumerateArray().Select(value =>
                MaintainerDefinitionRevision.DecodeCanonical(Convert.FromBase64String(value.GetString()!))).ToArray(),
            command.GetProperty("recipeCanonicalBase64").EnumerateArray().Select(value =>
                new RecapGridControlRecipeRegistration(GridBuildRecipe.DecodeCanonical(
                    Convert.FromBase64String(value.GetString()!)), witness)).ToArray());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-digest")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void LegacyV3BootstrapDescriptorIsValidatedBeforeProjection(string? descriptor) {
        LegacyControlFileV3Dto original = JsonSerializer.Deserialize<LegacyControlFileV3Dto>(
            LegacyV3ControlBytes(), ControlJson.Options)!;
        LegacyRecipeEntryDto entry = original.Recipes.First(value => value.Bootstrap.RowId is not null);
        LegacyControlFileV3Dto changed = original with { Recipes = original.Recipes.Select(value => value == entry
            ? value with { Bootstrap = value.Bootstrap with { DescriptorDigest = descriptor } } : value).ToArray() };
        byte[] unresigned = JsonSerializer.SerializeToUtf8Bytes(changed, ControlJson.Options);
        Assert.Equal("ControlStateDigestMismatch", Assert.Throws<ControlStoreException>(() => ControlState.Decode(unresigned)).Code);
        Assert.Equal("RecipeBootstrapInvalid", Assert.Throws<ControlStoreException>(() =>
            ControlState.Decode(RecodeLegacyV3(changed))).Code);
    }

    [Fact]
    public void LegacyV3SourceHashCanonicalAndGraphValidationRemainMandatory() {
        byte[] original = LegacyV3ControlBytes();
        Assert.Throws<ControlStoreException>(() => ControlState.Decode(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original) + " ")));
        LegacyControlFileV3Dto wire = JsonSerializer.Deserialize<LegacyControlFileV3Dto>(original, ControlJson.Options)!;
        Assert.Equal("RecipeDefinitionAbsent", Assert.Throws<ControlStoreException>(() =>
            ControlState.Decode(RecodeLegacyV3(wire with { Definitions = [] }))).Code);
        ControlState current = ControlState.Decode(original).WithGenerationAndReceipts(
            new ControlInstanceId(wire.Head.InstanceId), wire.Head.Generation + 1,
            ControlState.Decode(original).OperationReceipts);
        Assert.Throws<ControlStoreException>(() => ControlState.Decode(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(current.CanonicalBytes).Replace("\"rowId\":", "\"descriptorDigest\":null,\"rowId\":"))));
    }

    // Corruptions of the fixed source corpus, not a new writer posing as an old fixture.
    private static byte[] RecodeLegacyV3(LegacyControlFileV3Dto wire) {
        var body = new LegacyControlBodyV3Dto(wire.SchemaVersion, wire.Head.InstanceId, wire.Head.RefId,
            wire.Head.TimelineId, wire.Head.Generation, wire.Head.ActiveRecipeDigest,
            wire.Families, wire.Definitions, wire.Recipes, wire.OperationReceipts);
        wire = wire with { Head = wire.Head with { StateDigest = Hash("atelia.recap-grid.control-state.v3",
            JsonSerializer.SerializeToUtf8Bytes(body, ControlJson.Options)) } };
        return JsonSerializer.SerializeToUtf8Bytes(wire, ControlJson.Options);
    }
}
