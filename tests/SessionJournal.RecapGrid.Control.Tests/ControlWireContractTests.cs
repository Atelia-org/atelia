using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Control.Tests;

public sealed partial class ControlVerticalTests {
    [Fact]
    public void FutureSchemaRequiresCompleteStrictLeadingDiscriminator() {
        string[] unsupported = [
            "{\"schemaVersion\":3}",
            "{\"schemaVersion\":3,\"future\":{\"shape\":true}}"
        ];
        foreach (string json in unsupported) {
            Assert.Equal(
                3,
                Assert.Throws<ControlUnsupportedSchemaException>(() =>
                    ControlState.Decode(Encoding.UTF8.GetBytes(json))
                ).Version
            );
        }

        string[] invalid = [
            "{\"schemaVersion\":3",
            "{\"schemaVersion\":3,}",
            "{\"schemaVersion\":3}[]",
            "{\"schemaVersion\":3,\"schemaVersion\":3}",
            "{\"schemaVersion\":3,\"SchemaVersion\":3}",
            "{\"schemaVersion\":3,\"future\":1,\"future\":2}",
            "{\"schemaVersion\":3,\"future\":1,\"Future\":2}",
            "{\"SchemaVersion\":3}",
            "{\"\\u0073chemaVersion\":3}",
            "{\"future\":true,\"schemaVersion\":3}",
            "{\"schemaVersion\":\"3\"}",
            "{\"schemaVersion\":null}",
            "{\"schemaVersion\":3.0}",
            "{\"schemaVersion\":3e0}",
            "{\"schemaVersion\":2147483648}",
            "{\"schemaVersion\":2}"
        ];
        foreach (string json in invalid) {
            Assert.Throws<ControlStoreException>(() =>
                ControlState.Decode(Encoding.UTF8.GetBytes(json))
            );
        }
    }

    [Fact]
    public void EmptyWholeStateCanonicalBytesAreExactGolden() {
        ControlState state = ControlState.CreateEmpty(
            new RefId(1),
            new TimelineId("00112233445566778899aabbccddeeff"),
            new ControlInstanceId("0123456789abcdef0123456789abcdef"),
            generation: 7
        );
        const string Expected = "{\"schemaVersion\":2,\"head\":{"
            + "\"instanceId\":\"0123456789abcdef0123456789abcdef\","
            + "\"refId\":1,"
            + "\"timelineId\":\"00112233445566778899aabbccddeeff\","
            + "\"generation\":7,"
            + "\"stateDigest\":"
            + "\"fc85ff26c4a745957556fae2a7e803e869258a4bd2fbda603a664fe8e1688bf8\","
            + "\"activeRecipeDigest\":null},"
            + "\"families\":[],\"definitions\":[],\"recipes\":[],"
            + "\"operationReceipts\":[]}";

        Assert.Equal(Expected, Encoding.UTF8.GetString(
            state.CanonicalBytes
        ));
        Assert.Equal(
            Expected,
            Encoding.UTF8.GetString(
                ControlState.Decode(Encoding.UTF8.GetBytes(Expected))
                    .CanonicalBytes
            )
        );
    }

    [Fact]
    public void FutureSchemaIsTypedAcrossMutationReopenAndMaintenance() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef created = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        string backup = Path.Combine(path, "control-wire-backup");
        Assert.IsType<RecapGridControlBackupResult.Created>(
            RecapGridControlMaintenance.Backup(
                path,
                journal.BranchRefId,
                created,
                backup
            )
        );
        string statePath = ControlStatePath(
            path,
            journal.BranchRefId,
            created
        );
        byte[] future = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":3,\"future\":{\"shape\":true}}"
        );

        using (RecapGridControlHandle handle = Assert.IsType<
                   RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   values.Admission
               )).Handle) {
            File.WriteAllBytes(statePath, future);
            Assert.Equal(
                3,
                Assert.IsType<
                    RecapGridControlSnapshotResult.UnsupportedSchema
                >(handle.Reader.ReadSnapshot()).SchemaVersion
            );
            RecapGridControlPutResult.Invalid invalid = Assert.IsType<
                RecapGridControlPutResult.Invalid
            >(handle.Coordinator.PutFamilyDefinition(
                created,
                values.Family
            ));
            Assert.Equal("ControlUnsupportedSchema", invalid.Code);
            Assert.Equal(future, File.ReadAllBytes(statePath));
        }

        Assert.Equal(3, Assert.IsType<
            RecapGridControlCreateResult.ControlUnsupportedSchema
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).SchemaVersion);
        Assert.Equal(3, Assert.IsType<
            RecapGridControlOpenResult.UnsupportedSchema
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            values.Admission
        )).SchemaVersion);
        Assert.Equal(3, Assert.IsType<
            RecapGridControlReaderOpenResult.UnsupportedSchema
        >(RecapGridControlFactory.OpenReader(
            path,
            journal.BranchRefId
        )).SchemaVersion);
        Assert.Equal(3, Assert.IsType<
            RecapGridControlInspectResult.UnsupportedSchema
        >(RecapGridControlMaintenance.Inspect(
            path,
            journal.BranchRefId
        )).SchemaVersion);
        Assert.Equal(3, Assert.IsType<
            RecapGridControlInspectResult.UnsupportedSchema
        >(RecapGridControlMaintenance.Verify(
            path,
            journal.BranchRefId
        )).SchemaVersion);
        Assert.Equal(3, Assert.IsType<
            RecapGridControlExportResult.UnsupportedSchema
        >(RecapGridControlMaintenance.Export(
            path,
            journal.BranchRefId
        )).SchemaVersion);
        Assert.Equal(3, Assert.IsType<
            RecapGridControlBackupResult.ControlUnsupportedSchema
        >(RecapGridControlMaintenance.Backup(
            path,
            journal.BranchRefId,
            created,
            Path.Combine(path, "unsupported-backup")
        )).SchemaVersion);
        Assert.Equal(3, Assert.IsType<
            RecapGridControlAdminResult.ControlUnsupportedSchema
        >(RecapGridControlMaintenance.Restore(
            path,
            journal.BranchRefId,
            created,
            backup
        )).SchemaVersion);
        Assert.Equal(3, Assert.IsType<
            RecapGridControlAdminResult.ControlUnsupportedSchema
        >(RecapGridControlMaintenance.Reinitialize(
            path,
            journal.BranchRefId,
            created
        )).SchemaVersion);
        Assert.Equal(future, File.ReadAllBytes(statePath));
    }
}
