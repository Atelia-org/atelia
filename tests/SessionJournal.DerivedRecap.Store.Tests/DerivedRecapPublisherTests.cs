using System.Reflection;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapPublisherTests {
    [Fact]
    public void PublicPublishSurfaceCannotAcceptCallerSnapshot() {
        MethodInfo[] storeMethods =
            typeof(DerivedRecapStore).GetMethods(
                BindingFlags.Instance | BindingFlags.Public
            );
        Assert.DoesNotContain(
            storeMethods,
            static method =>
                method.Name.StartsWith(
                    "Publish",
                    StringComparison.Ordinal
                )
                && method.GetParameters().Any(
                    static parameter =>
                        parameter.ParameterType
                            == typeof(SessionCurrentLineageSnapshot)
                )
        );

        MethodInfo publicPublish = Assert.Single(
            typeof(DerivedRecapPublisher).GetMethods(
                BindingFlags.Instance | BindingFlags.Public
            ),
            static method =>
                method.Name == nameof(
                    DerivedRecapPublisher.PublishAsync
                )
        );
        Assert.DoesNotContain(
            publicPublish.GetParameters(),
            static parameter =>
                parameter.ParameterType
                    == typeof(SessionCurrentLineageSnapshot)
        );
    }

    [Fact]
    public async Task FinalRawGateRejectsRewindAfterCapture() {
        SessionJournalEngine? engine = null;
        EventAddress capturedHead = default;
        EventAddress rewindTarget = default;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    BeforePublishedPromotion: () => {
                        Assert.True(
                            engine!.MoveCurrentHeadForTest(
                                capturedHead,
                                rewindTarget
                            )
                        );
                    }
                )
            );
        engine = fixture.Engine;
        RefId refId = engine.BranchRefId;
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        capturedHead = lineage.CapturedHead;
        rewindTarget = lineage.HeadToRoot[2].Address;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            capturedHead,
            rewindTarget
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                refId,
                capturedHead,
                [plan]
            );
        await fixture.Store.CreateBuildingAsync(manifest);
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,

            capturedHead,
            DerivedRecapCodec.CreateBlock(
                plan,
                capturedHead,
                "recap"
            )
        );

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
                await fixture.Publisher.PublishAsync(capturedHead)
        );
        Assert.True(
            Directory.Exists(
                fixture.Store.GetBuildingPathForTest(capturedHead)
            )
        );
        Assert.False(
            Directory.Exists(
                fixture.Store.GetPublishedPathForTest(capturedHead)
            )
        );
    }

    [Fact]
    public async Task EngineBoundPublisherPublishesNormally() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();

        PublishedRecapDescriptor descriptor =
            await fixture.PublishAsync(
                lineage.CapturedHead,
                lineage.HeadToRoot[2].Address,
                content: "authority-bound"
            );

        Assert.Equal(
            lineage.CapturedHead,
            descriptor.SetAdmissionAnchor
        );
        Assert.IsType<DerivedRecapSelection.Selected>(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );
    }

    [Fact]
    public async Task TwoStoreInstancesSerializeOnPerRefLock() {
        var enteredGate =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
        using var releaseGate = new ManualResetEventSlim();
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    BeforePublishedPromotion: () => {
                        enteredGate.SetResult();
                        releaseGate.Wait();
                    }
                )
            );
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.HeadToRoot[2].Address
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            );
        await fixture.Store.CreateBuildingAsync(manifest);
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,

            anchor,
            DerivedRecapCodec.CreateBlock(plan, anchor, "recap")
        );
        Task<PublishedRecapDescriptor> publishing =
            fixture.Publisher.PublishAsync(anchor).AsTask();
        await enteredGate.Task.WaitAsync(TimeSpan.FromSeconds(10));

        DerivedRecapStore secondStore = DerivedRecapStore.Open(
            fixture.Path,
            fixture.Engine.BranchRefId
        );
        var secondPublisher = new DerivedRecapPublisher(
            secondStore,
            fixture.Engine
        );
        Task<RecapPublishability> contending =
            secondPublisher.CanPublishAsync(anchor).AsTask();
        try {
            Task early = await Task.WhenAny(
                contending,
                Task.Delay(TimeSpan.FromMilliseconds(200))
            );
            Assert.NotSame(contending, early);
        }
        finally {
            releaseGate.Set();
        }
        _ = await publishing;
        _ = await contending;
    }
}
