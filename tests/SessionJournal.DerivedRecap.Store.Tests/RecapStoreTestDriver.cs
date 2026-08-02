using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

internal static class RecapStoreTestDriver {
    public static async ValueTask<PublishedRecapSet>
        RewritePublishedUncheckedAsync(
        DerivedRecapStore store,
        DerivedRecapSetManifest manifest,
        IReadOnlyList<DerivedRecapBlock> committedBlocks,
        IReadOnlyList<DerivedRecapBlock>? persistedBlocks = null
    ) {
        PublishedRecapSet publication =
            DerivedRecapCodec.CreatePublication(
                manifest,
                committedBlocks
            );
        string publishedPath = store.GetPublishedPathForTest(
            manifest.SetAdmissionAnchor
        );
        await File.WriteAllBytesAsync(
            Path.Combine(publishedPath, "publication.json"),
            DerivedRecapCodec.EncodePublication(publication)
        );
        foreach (DerivedRecapBlock block
                 in persistedBlocks ?? committedBlocks) {
            await File.WriteAllBytesAsync(
                Path.Combine(
                    publishedPath,
                    "blocks",
                    $"{block.RecapBlockId.Value}.json"
                ),
                DerivedRecapCodec.EncodeBlock(block)
            );
        }
        return publication;
    }

    public static async ValueTask InstallFinalAsync(
        DerivedRecapStore store,
        EventAddress admissionAnchor,
        DerivedRecapBlock block
    ) {
        BuildingReadResult.Available building =
            Assert.IsType<BuildingReadResult.Available>(
                await store.ReadBuildingAsync(admissionAnchor)
            );
        BuildingBlockInspection inspection =
            await store.InspectBuildingBlockAsync(
                building.Snapshot.Descriptor,
                block.RecapBlockId
            );
        if (inspection.Plan is MaintainRecapBlockPlan maintain) {
            int nextEndpoint = inspection.Checkpoint
                is RollingRecapCheckpointHealth.Healthy healthy
                    ? healthy.EndpointIndex + 1
                    : 0;
            for (int index = nextEndpoint;
                 index < maintain.CatchUpBoundaries.Count;
                 index++) {
                DerivedRecapBlock checkpoint =
                    index == maintain.CatchUpBoundaries.Count - 1
                        ? block
                        : DerivedRecapCodec.CreateBlock(
                            maintain,
                            maintain.CatchUpBoundaries[index].Address,
                            block.Content
                        );
                CheckpointWriteResult result =
                    await store.AdvanceRollingCheckpointAsync(
                        building.Snapshot.Descriptor,
                        block.RecapBlockId,
                        inspection.Checkpoint.StateToken,
                        checkpoint
                    );
                _ = Assert.IsType<CheckpointWriteResult.Updated>(
                    result
                );
                inspection =
                    await store.InspectBuildingBlockAsync(
                        building.Snapshot.Descriptor,
                        block.RecapBlockId
                    );
            }
        }
        _ = await store.EnsureFinalBlockAsync(
            building.Snapshot.Descriptor,
            block.RecapBlockId,
            inspection.Final.StateToken,
            block
        );
    }
}
