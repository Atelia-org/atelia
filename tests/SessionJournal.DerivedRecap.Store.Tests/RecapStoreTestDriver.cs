using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

internal static class RecapStoreTestDriver {
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
                 index < maintain.CatchUpThrough.Count;
                 index++) {
                DerivedRecapBlock checkpoint =
                    index == maintain.CatchUpThrough.Count - 1
                        ? block
                        : DerivedRecapCodec.CreateBlock(
                            maintain,
                            maintain.CatchUpThrough[index],
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
