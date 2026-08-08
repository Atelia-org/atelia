using Atelia.Completion.Abstractions;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class DerivedRecapSerialEpochKernelTests {
    private const string Hash0 =
        "0000000000000000000000000000000000000000000000000000000000000000";
    private const string Capability =
        "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly EventAddress A1 = new(
        SizedPtr.FromPacked(0x0102_0304_0506_0708),
        1,
        AddressHint.None
    );
    private static readonly EventAddress A2 = new(
        SizedPtr.FromPacked(0x1112_1314_1516_1718),
        1,
        AddressHint.None
    );

    [Fact]
    public async Task TwoMembersShareExactInputAndKeepUsesStructuredPrior() {
        RecapEpochBlockDefinition[] roster = Roster();
        PriorRecapPackSnapshot prior = CreatePriorPack(roster);
        DerivedRecapEpochInput input = DerivedRecapV8Codec.CreateEpochInput(
            Boundary(A1),
            Boundary(A2),
            rawEventCount: 2,
            Hash0,
            [new ObservationMessage("B"), new ObservationMessage("D")],
            new RecapEpochPrevious.Prior(prior)
        );
        DerivedRecapEpochManifest manifest = Manifest(input, roster);
        RecapEpochStoreSnapshot snapshot = Snapshot(manifest, input);
        var self = new FakeMaintainer(
            roster[0],
            new RecapMaintenanceSuccess.Updated("A+B")
        );
        var world = new FakeMaintainer(
            roster[1],
            RecapMaintenanceSuccess.KeepUnchanged.Instance
        );
        var installed = new List<DerivedRecapFinalBlock>();

        SerialEpochKernelResult result =
            await DerivedRecapSerialEpochKernel.ExecuteAsync(
                snapshot,
                new RecapBlockMaintainerRegistry([self, world]),
                maxMaintainerCallsPerEpoch: 2,
                (inspection, block, _) => {
                    installed.Add(block);
                    return ValueTask.FromResult<WriteRecapEpochFinalResult>(
                        new WriteRecapEpochFinalResult.Installed(
                            $"installed:{inspection.Definition.Ordinal}"
                        )
                    );
                },
                CancellationToken.None
            );

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.StartedCallCount);
        Assert.Same(Assert.Single(self.Inputs), Assert.Single(world.Inputs));
        Assert.Same(result.RuntimeInput, self.Inputs[0]);
        Assert.Equal(2, result.RuntimeInput.HistoryMessages.Count);
        Assert.Contains("old-self", result.RuntimeInput.PriorContext.SystemPromptFragment);
        Assert.Contains("old-world", result.RuntimeInput.PriorContext.ObservationMessage);
        Assert.Equal(["A+B", "old-world"],
            installed.Select(static block => block.Content));
        Assert.NotEqual(
            prior.Blocks[1].SourceEpochBlockExecutionSha256,
            installed[1].EpochBlockExecutionSha256
        );
    }

    [Fact]
    public async Task CompleteRosterPreflightFailureMakesZeroCalls() {
        RecapEpochBlockDefinition[] roster = Roster();
        DerivedRecapEpochInput input = DerivedRecapV8Codec.CreateEpochInput(
            Boundary(A1),
            Boundary(A2),
            rawEventCount: 1,
            Hash0,
            [new ObservationMessage("A")],
            RecapEpochPrevious.Empty.Instance
        );
        DerivedRecapEpochManifest manifest = Manifest(input, roster);
        var self = new FakeMaintainer(
            roster[0],
            new RecapMaintenanceSuccess.Updated("self")
        );

        SerialEpochKernelResult result =
            await DerivedRecapSerialEpochKernel.ExecuteAsync(
                Snapshot(manifest, input),
                new RecapBlockMaintainerRegistry([self]),
                maxMaintainerCallsPerEpoch: 2,
                (_, _, _) => throw new InvalidOperationException(
                    "must not install"
                ),
                CancellationToken.None
            );

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.StartedCallCount);
        Assert.Empty(self.Inputs);
        Assert.Equal("MaintainerUnavailable", result.PrimaryFailure!.Code);
        Assert.All(
            result.Outcomes,
            outcome => Assert.IsType<SerialEpochBlockOutcome.Failed>(outcome)
        );
    }

    [Fact]
    public async Task PendingRosterCapPrecedesMissingBindingResult() {
        RecapEpochBlockDefinition[] roster = Roster();
        DerivedRecapEpochInput input = DerivedRecapV8Codec.CreateEpochInput(
            Boundary(A1),
            Boundary(A2),
            rawEventCount: 1,
            Hash0,
            [new ObservationMessage("A")],
            RecapEpochPrevious.Empty.Instance
        );
        DerivedRecapEpochManifest manifest = Manifest(input, roster);
        var self = new FakeMaintainer(
            roster[0],
            new RecapMaintenanceSuccess.Updated("self")
        );

        InvalidDataException exception = await Assert.ThrowsAsync<
            InvalidDataException
        >(async () => await DerivedRecapSerialEpochKernel.ExecuteAsync(
            Snapshot(manifest, input),
            new RecapBlockMaintainerRegistry([self]),
            maxMaintainerCallsPerEpoch: 1,
            (_, _, _) => throw new InvalidOperationException(
                "must not install"
            ),
            CancellationToken.None
        ));

        Assert.Contains("requires 2 calls", exception.Message);
        Assert.Empty(self.Inputs);
    }

    [Fact]
    public async Task UnavailableFinalSlotMakesZeroCalls() {
        RecapEpochBlockDefinition[] roster = Roster();
        DerivedRecapEpochInput input = DerivedRecapV8Codec.CreateEpochInput(
            Boundary(A1),
            Boundary(A2),
            rawEventCount: 1,
            Hash0,
            [new ObservationMessage("A")],
            RecapEpochPrevious.Empty.Instance
        );
        DerivedRecapEpochManifest manifest = Manifest(input, roster);
        RecapEpochStoreSnapshot initial = Snapshot(manifest, input);
        RecapEpochStoreSnapshot unavailable = initial with {
            Blocks = Array.AsReadOnly([
                initial.Blocks[0],
                initial.Blocks[1] with {
                    Final = new RecapEpochFinalHealth.Unavailable(
                        "read fault"
                    ),
                    WriteAuthority = null
                }
            ])
        };
        var self = new FakeMaintainer(
            roster[0],
            new RecapMaintenanceSuccess.Updated("self")
        );
        var world = new FakeMaintainer(
            roster[1],
            new RecapMaintenanceSuccess.Updated("world")
        );

        SerialEpochKernelResult result =
            await DerivedRecapSerialEpochKernel.ExecuteAsync(
                unavailable,
                new RecapBlockMaintainerRegistry([self, world]),
                maxMaintainerCallsPerEpoch: 2,
                (_, _, _) => throw new InvalidOperationException(
                    "must not install"
                ),
                CancellationToken.None
            );

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.StartedCallCount);
        Assert.Equal("FinalSlotUnavailable", result.PrimaryFailure!.Code);
        Assert.Empty(self.Inputs);
        Assert.Empty(world.Inputs);
    }

    [Fact]
    public async Task BootstrapKeepFailsButLaterMemberStillInstalls() {
        RecapEpochBlockDefinition[] roster = Roster();
        DerivedRecapEpochInput input = DerivedRecapV8Codec.CreateEpochInput(
            Boundary(A1),
            Boundary(A2),
            rawEventCount: 1,
            Hash0,
            [new ObservationMessage("A")],
            RecapEpochPrevious.Empty.Instance
        );
        DerivedRecapEpochManifest manifest = Manifest(input, roster);
        var self = new FakeMaintainer(
            roster[0],
            RecapMaintenanceSuccess.KeepUnchanged.Instance
        );
        var world = new FakeMaintainer(
            roster[1],
            new RecapMaintenanceSuccess.Updated("world")
        );
        var installed = new List<DerivedRecapFinalBlock>();

        SerialEpochKernelResult result =
            await DerivedRecapSerialEpochKernel.ExecuteAsync(
                Snapshot(manifest, input),
                new RecapBlockMaintainerRegistry([self, world]),
                maxMaintainerCallsPerEpoch: 2,
                (_, block, _) => {
                    installed.Add(block);
                    return ValueTask.FromResult<WriteRecapEpochFinalResult>(
                        new WriteRecapEpochFinalResult.Installed("installed")
                    );
                },
                CancellationToken.None
            );

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.StartedCallCount);
        Assert.Equal(roster[0].RecapBlockId, result.PrimaryFailure!.RecapBlockId);
        Assert.Single(self.Inputs);
        Assert.Single(world.Inputs);
        Assert.Equal("world", Assert.Single(installed).Content);
    }

    [Fact]
    public async Task AllHealthySkipsBindingAndRemoteWork() {
        RecapEpochBlockDefinition[] roster = Roster();
        DerivedRecapEpochInput input = DerivedRecapV8Codec.CreateEpochInput(
            Boundary(A1),
            Boundary(A2),
            rawEventCount: 1,
            Hash0,
            [new ObservationMessage("A")],
            RecapEpochPrevious.Empty.Instance
        );
        DerivedRecapEpochManifest manifest = Manifest(input, roster);
        RecapEpochStoreSnapshot missing = Snapshot(manifest, input);
        RecapEpochStoreSnapshot healthy = missing with {
            Blocks = Array.AsReadOnly([
                .. missing.Blocks.Select(block =>
                    block with {
                        Final = new RecapEpochFinalHealth.Healthy(
                            DerivedRecapV8Codec.CreateFinalBlock(
                                manifest,
                                block.Definition,
                                block.Definition.RecapBlockId.Value
                            ),
                            "healthy"
                        ),
                        WriteAuthority = null
                    })
            ])
        };
        int installs = 0;

        SerialEpochKernelResult result =
            await DerivedRecapSerialEpochKernel.ExecuteAsync(
                healthy,
                new RecapBlockMaintainerRegistry([]),
                maxMaintainerCallsPerEpoch: 1,
                (_, _, _) => {
                    installs++;
                    throw new InvalidOperationException(
                        "healthy finals must not install"
                    );
                },
                CancellationToken.None
            );

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.StartedCallCount);
        Assert.Equal(0, installs);
        Assert.All(
            result.Outcomes,
            outcome => Assert.IsType<
                SerialEpochBlockOutcome.ReusedHealthy
            >(outcome)
        );
    }

    private static PriorRecapPackSnapshot CreatePriorPack(
        IReadOnlyList<RecapEpochBlockDefinition> roster
    ) {
        DerivedRecapEpochInput sourceInput =
            DerivedRecapV8Codec.CreateEpochInput(
                Boundary(A2),
                Boundary(A1),
                rawEventCount: 1,
                Hash0,
                [new ObservationMessage("source")],
                RecapEpochPrevious.Empty.Instance
            );
        DerivedRecapEpochManifest sourceManifest =
            DerivedRecapV8Codec.CreateManifest(
                new RefId(7),
                A1,
                sourceInput.PayloadSha256,
                roster
            );
        DerivedRecapFinalBlock self = DerivedRecapV8Codec.CreateFinalBlock(
            sourceManifest,
            roster[0],
            "old-self"
        );
        DerivedRecapFinalBlock world = DerivedRecapV8Codec.CreateFinalBlock(
            sourceManifest,
            roster[1],
            "old-world"
        );
        PublishedRecapEpoch publication =
            DerivedRecapV8Codec.CreatePublication(
                sourceManifest,
                [self, world]
            );
        return DerivedRecapV8Codec.CreatePriorPack(
            new PublishedRecapEpochDescriptor(
                publication.RefId,
                publication.AdmissionAnchor,
                publication.EnvelopeSha256
            ),
            [
                DerivedRecapV8Codec.CreatePriorBlock(
                    self.RecapBlockId,
                    self.Target,
                    self.Content,
                    self.EpochBlockExecutionSha256,
                    self.PayloadSha256
                ),
                DerivedRecapV8Codec.CreatePriorBlock(
                    world.RecapBlockId,
                    world.Target,
                    world.Content,
                    world.EpochBlockExecutionSha256,
                    world.PayloadSha256
                )
            ]
        );
    }

    private static RecapEpochStoreSnapshot Snapshot(
        DerivedRecapEpochManifest manifest,
        DerivedRecapEpochInput input
    ) {
        var descriptor = new RecapEpochBuildingDescriptor(
            manifest.RefId,
            manifest.AdmissionAnchor,
            manifest.ManifestPayloadSha256
        );
        RecapEpochBlockInspection[] blocks = [
            .. manifest.Blocks.Select(definition =>
                new RecapEpochBlockInspection(
                    definition,
                    new RecapEpochFinalHealth.Missing("missing"),
                    new RecapEpochFinalWriteAuthority(
                        "/tmp/test-recap-v8",
                        RecapEpochFinalStage.Building,
                        descriptor,
                        definition.RecapBlockId,
                        "missing",
                        null
                    )
                ))
        ];
        return new RecapEpochStoreSnapshot(
            RecapEpochFinalStage.Building,
            descriptor,
            manifest,
            input,
            blocks,
            null,
            null
        );
    }

    private static DerivedRecapEpochManifest Manifest(
        DerivedRecapEpochInput input,
        IReadOnlyList<RecapEpochBlockDefinition> roster
    ) => DerivedRecapV8Codec.CreateManifest(
        new RefId(7),
        input.AdmissionBoundary.Address,
        input.PayloadSha256,
        roster
    );

    private static RecapEpochBlockDefinition[] Roster() => [
        new(
            new RecapBlockId("self"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "self"
            ),
            "self",
            Capability,
            1024,
            0
        ),
        new(
            new RecapBlockId("world"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.Observation,
                "world"
            ),
            "world",
            Capability,
            1024,
            1
        )
    ];

    private static RecapEpochBoundary Boundary(EventAddress address)
        => new(
            address,
            new SessionContextAnchorSetupReferences(
                new SessionContextSetupReference(address, 1, Hash0),
                new SessionContextSetupReference(
                    address,
                    1,
                    new string('1', 64)
                )
            )
        );

    private sealed class FakeMaintainer(
        RecapEpochBlockDefinition definition,
        RecapMaintenanceSuccess result
    ) : IRecapBlockMaintainer {
        public string Id => definition.MaintainerId;
        public ContextHeaderBlockPath Target => definition.Target;
        public string CapabilityFingerprint =>
            definition.MaintainerCapabilityFingerprint;
        public object RuntimeGroupAffinity { get; } = new();
        public List<RecapMaintenanceEpochInput> Inputs { get; } = [];

        public ValueTask<RecapMaintenanceSuccess> MaintainAsync(
            RecapMaintenanceEpochInput input,
            CancellationToken cancellationToken
        ) {
            Inputs.Add(input);
            return ValueTask.FromResult(result);
        }
    }
}
