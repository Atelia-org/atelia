using System.Collections.Concurrent;
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
        var runtimeGroup = new object();
        var self = new FakeMaintainer(
            roster[0],
            new RecapMaintenanceSuccess.Updated("A+B"),
            runtimeGroup
        );
        var world = new FakeMaintainer(
            roster[1],
            RecapMaintenanceSuccess.KeepUnchanged.Instance,
            runtimeGroup
        );
        var installed = new List<DerivedRecapFinalBlock>();

        SerialEpochKernelResult result =
            await DerivedRecapSerialEpochKernel.ExecuteAsync(
                snapshot,
                new RecapBlockMaintainerRegistry([self, world]),
                maxMaintainerCallsPerEpoch: 2,
                maxMaintainerCallsForOperation: 2,
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
        Assert.Equal(1, self.CreateGroupExecutionCallCount);
        Assert.Equal(0, world.CreateGroupExecutionCallCount);
        Assert.Same(Assert.Single(self.Inputs), Assert.Single(world.Inputs));
        Assert.Same(result.RuntimeInput, self.Inputs[0]);
        Assert.Same(
            Assert.Single(self.GroupExecutions),
            Assert.Single(world.GroupExecutions)
        );
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
                maxMaintainerCallsForOperation: 2,
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
            maxMaintainerCallsForOperation: 2,
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
                maxMaintainerCallsForOperation: 2,
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
                maxMaintainerCallsForOperation: 2,
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
                maxMaintainerCallsForOperation: 1,
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

    [Fact]
    public async Task GroupLeadersOverlapAndFollowersWaitForOwnLeader() {
        RecapEpochBlockDefinition[] roster = Roster(4);
        DerivedRecapEpochInput input = CreateInput();
        DerivedRecapEpochManifest manifest = Manifest(input, roster);
        var groupA = new object();
        var groupB = new object();
        var leaderAStarted = NewSignal();
        var leaderBStarted = NewSignal();
        var followerAStarted = NewSignal();
        var followerBStarted = NewSignal();
        var releaseA = NewSignal();
        var releaseB = NewSignal();
        var maintainers = new IRecapBlockMaintainer[] {
            new ControlledMaintainer(
                roster[0],
                groupA,
                async (_, cancellationToken) => {
                    leaderAStarted.TrySetResult();
                    await releaseA.Task.WaitAsync(cancellationToken);
                    return new RecapMaintenanceSuccess.Updated("a-leader");
                }
            ),
            new ControlledMaintainer(
                roster[1],
                groupB,
                async (_, cancellationToken) => {
                    leaderBStarted.TrySetResult();
                    await releaseB.Task.WaitAsync(cancellationToken);
                    return new RecapMaintenanceSuccess.Updated("b-leader");
                }
            ),
            new ControlledMaintainer(
                roster[2],
                groupA,
                (_, _) => {
                    followerAStarted.TrySetResult();
                    return ValueTask.FromResult<RecapMaintenanceSuccess>(
                        new RecapMaintenanceSuccess.Updated("a-follower")
                    );
                }
            ),
            new ControlledMaintainer(
                roster[3],
                groupB,
                (_, _) => {
                    followerBStarted.TrySetResult();
                    return ValueTask.FromResult<RecapMaintenanceSuccess>(
                        new RecapMaintenanceSuccess.Updated("b-follower")
                    );
                }
            )
        };
        var installed = new ConcurrentQueue<RecapBlockId>();

        Task<SerialEpochKernelResult> operation =
            DerivedRecapSerialEpochKernel.ExecuteAsync(
                Snapshot(manifest, input),
                new RecapBlockMaintainerRegistry(maintainers),
                maxMaintainerCallsPerEpoch: 4,
                maxMaintainerCallsForOperation: 4,
                (_, block, _) => {
                    installed.Enqueue(block.RecapBlockId);
                    return ValueTask.FromResult<WriteRecapEpochFinalResult>(
                        new WriteRecapEpochFinalResult.Installed("ok")
                    );
                },
                CancellationToken.None
            ).AsTask();

        await WaitForSignalAsync(leaderAStarted.Task, operation);
        await WaitForSignalAsync(leaderBStarted.Task, operation);
        Assert.False(followerAStarted.Task.IsCompleted);
        Assert.False(followerBStarted.Task.IsCompleted);

        releaseA.TrySetResult();
        await followerAStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(followerBStarted.Task.IsCompleted);

        releaseB.TrySetResult();
        SerialEpochKernelResult result = await operation;

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.StartedCallCount);
        Assert.Equal(4, installed.Count);
        Assert.Equal(
            [
                RecapMaintainerCallRole.Leader,
                RecapMaintainerCallRole.Leader,
                RecapMaintainerCallRole.Follower,
                RecapMaintainerCallRole.Follower
            ],
            maintainers.Cast<ControlledMaintainer>()
                .SelectMany(static maintainer => maintainer.Roles)
                .OrderBy(static role => role)
        );
    }

    [Fact]
    public async Task FollowerCannotRaceLeaderThatHasNotRequestedLaneYet() {
        RecapEpochBlockDefinition[] roster = Roster(3);
        DerivedRecapEpochInput input = CreateInput();
        var groupA = new object();
        var groupB = new object();
        var delayedLeaderHasPermission = NewSignal();
        var allowDelayedAdmission = NewSignal();
        var followerStarted = NewSignal();
        var leaderA = new ControlledMaintainer(
            roster[0],
            groupA,
            (_, _) => ValueTask.FromResult<RecapMaintenanceSuccess>(
                new RecapMaintenanceSuccess.Updated("leader-a")
            )
        );
        var leaderB = new ControlledMaintainer(
            roster[1],
            groupB,
            (_, _) => ValueTask.FromResult<RecapMaintenanceSuccess>(
                new RecapMaintenanceSuccess.Updated("leader-b")
            ),
            beforeAdmission: async cancellationToken => {
                delayedLeaderHasPermission.TrySetResult();
                await allowDelayedAdmission.Task.WaitAsync(
                    cancellationToken
                );
            }
        );
        var followerA = new ControlledMaintainer(
            roster[2],
            groupA,
            (_, _) => {
                followerStarted.TrySetResult();
                return ValueTask.FromResult<RecapMaintenanceSuccess>(
                    new RecapMaintenanceSuccess.Updated("follower-a")
                );
            }
        );
        Task<SerialEpochKernelResult> operation =
            DerivedRecapSerialEpochKernel.ExecuteAsync(
                Snapshot(Manifest(input, roster), input),
                new RecapBlockMaintainerRegistry([
                    leaderA,
                    leaderB,
                    followerA
                ]),
                maxMaintainerCallsPerEpoch: 3,
                maxMaintainerCallsForOperation: 3,
                (_, _, _) => ValueTask.FromResult<
                    WriteRecapEpochFinalResult
                >(new WriteRecapEpochFinalResult.Installed("ok")),
                CancellationToken.None
            ).AsTask();

        await WaitForSignalAsync(
            delayedLeaderHasPermission.Task,
            operation
        );
        await Task.Yield();
        Assert.False(followerStarted.Task.IsCompleted);

        allowDelayedAdmission.TrySetResult();
        await WaitForSignalAsync(followerStarted.Task, operation);
        Assert.True((await operation).Succeeded);
    }

    [Fact]
    public async Task ProviderTimeoutFailsLeaderButReleasesFollower() {
        RecapEpochBlockDefinition[] roster = Roster();
        DerivedRecapEpochInput input = CreateInput();
        DerivedRecapEpochManifest manifest = Manifest(input, roster);
        var group = new object();
        var leader = new ControlledMaintainer(
            roster[0],
            group,
            (_, _) => throw new TaskCanceledException("provider timeout")
        );
        var follower = new ControlledMaintainer(
            roster[1],
            group,
            (_, _) => ValueTask.FromResult<RecapMaintenanceSuccess>(
                new RecapMaintenanceSuccess.Updated("follower")
            )
        );
        var installed = new ConcurrentQueue<RecapBlockId>();

        SerialEpochKernelResult result =
            await DerivedRecapSerialEpochKernel.ExecuteAsync(
                Snapshot(manifest, input),
                new RecapBlockMaintainerRegistry([leader, follower]),
                maxMaintainerCallsPerEpoch: 2,
                maxMaintainerCallsForOperation: 2,
                (_, block, _) => {
                    installed.Enqueue(block.RecapBlockId);
                    return ValueTask.FromResult<WriteRecapEpochFinalResult>(
                        new WriteRecapEpochFinalResult.Installed("ok")
                    );
                },
                CancellationToken.None
            );

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.StartedCallCount);
        Assert.Equal("MaintainerFailed", result.PrimaryFailure!.Code);
        Assert.Equal(roster[0].RecapBlockId,
            result.PrimaryFailure.RecapBlockId);
        Assert.Equal([RecapMaintainerCallRole.Leader], leader.Roles);
        Assert.Equal([RecapMaintainerCallRole.Follower], follower.Roles);
        Assert.Equal(roster[1].RecapBlockId, Assert.Single(installed));
    }

    [Fact]
    public async Task FollowerStartsAfterMaintenanceBeforeLeaderFinalWrite() {
        RecapEpochBlockDefinition[] roster = Roster();
        DerivedRecapEpochInput input = CreateInput();
        DerivedRecapEpochManifest manifest = Manifest(input, roster);
        var group = new object();
        var followerStarted = NewSignal();
        var releaseLeaderWrite = NewSignal();
        var leader = new ControlledMaintainer(
            roster[0],
            group,
            (_, _) => ValueTask.FromResult<RecapMaintenanceSuccess>(
                new RecapMaintenanceSuccess.Updated("leader")
            )
        );
        var follower = new ControlledMaintainer(
            roster[1],
            group,
            (_, _) => {
                followerStarted.TrySetResult();
                return ValueTask.FromResult<RecapMaintenanceSuccess>(
                    new RecapMaintenanceSuccess.Updated("follower")
                );
            }
        );

        Task<SerialEpochKernelResult> operation =
            DerivedRecapSerialEpochKernel.ExecuteAsync(
                Snapshot(manifest, input),
                new RecapBlockMaintainerRegistry([leader, follower]),
                maxMaintainerCallsPerEpoch: 2,
                maxMaintainerCallsForOperation: 2,
                async (_, block, cancellationToken) => {
                    if (block.RecapBlockId == roster[0].RecapBlockId) {
                        await releaseLeaderWrite.Task.WaitAsync(
                            cancellationToken
                        );
                    }
                    return new WriteRecapEpochFinalResult.Installed("ok");
                },
                CancellationToken.None
            ).AsTask();

        await WaitForSignalAsync(followerStarted.Task, operation);
        Assert.False(operation.IsCompleted);
        releaseLeaderWrite.TrySetResult();
        Assert.True((await operation).Succeeded);
    }

    [Fact]
    public async Task CallerCancellationStartsNoFollowerAndDrainsLeader() {
        RecapEpochBlockDefinition[] roster = Roster();
        DerivedRecapEpochInput input = CreateInput();
        DerivedRecapEpochManifest manifest = Manifest(input, roster);
        var group = new object();
        var leaderStarted = NewSignal();
        var leaderSettled = NewSignal();
        var followerStarted = NewSignal();
        var leader = new ControlledMaintainer(
            roster[0],
            group,
            async (_, cancellationToken) => {
                leaderStarted.TrySetResult();
                try {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                }
                finally {
                    leaderSettled.TrySetResult();
                }
            }
        );
        var follower = new ControlledMaintainer(
            roster[1],
            group,
            (_, _) => {
                followerStarted.TrySetResult();
                return ValueTask.FromResult<RecapMaintenanceSuccess>(
                    new RecapMaintenanceSuccess.Updated("follower")
                );
            }
        );
        using var cancellation = new CancellationTokenSource();
        Task operation = DerivedRecapSerialEpochKernel.ExecuteAsync(
            Snapshot(manifest, input),
            new RecapBlockMaintainerRegistry([leader, follower]),
            maxMaintainerCallsPerEpoch: 2,
            maxMaintainerCallsForOperation: 2,
            (_, _, _) => throw new InvalidOperationException(
                "cancelled work must not install"
            ),
            cancellation.Token
        ).AsTask();

        await leaderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await operation
        );

        Assert.True(leaderSettled.Task.IsCompleted);
        Assert.False(followerStarted.Task.IsCompleted);
        Assert.Empty(follower.Roles);
    }

    [Fact]
    public async Task PrimaryFailureUsesManifestOrdinalAfterAllWorkDrains() {
        RecapEpochBlockDefinition[] roster = Roster(3);
        DerivedRecapEpochInput input = CreateInput();
        DerivedRecapEpochManifest manifest = Manifest(input, roster);
        var slowStarted = NewSignal();
        var fastFailed = NewSignal();
        var releaseSlow = NewSignal();
        var slowFirst = new ControlledMaintainer(
            roster[0],
            new object(),
            async (_, cancellationToken) => {
                slowStarted.TrySetResult();
                await releaseSlow.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("slow ordinal zero");
            }
        );
        var fastSecond = new ControlledMaintainer(
            roster[1],
            new object(),
            (_, _) => {
                fastFailed.TrySetResult();
                throw new InvalidOperationException("fast ordinal one");
            }
        );
        var successfulThird = new ControlledMaintainer(
            roster[2],
            new object(),
            (_, _) => ValueTask.FromResult<RecapMaintenanceSuccess>(
                new RecapMaintenanceSuccess.Updated("successful sibling")
            )
        );
        var installed = new ConcurrentQueue<RecapBlockId>();
        Task<SerialEpochKernelResult> operation =
            DerivedRecapSerialEpochKernel.ExecuteAsync(
                Snapshot(manifest, input),
                new RecapBlockMaintainerRegistry([
                    slowFirst,
                    fastSecond,
                    successfulThird
                ]),
                maxMaintainerCallsPerEpoch: 3,
                maxMaintainerCallsForOperation: 3,
                (_, block, _) => {
                    installed.Enqueue(block.RecapBlockId);
                    return ValueTask.FromResult<WriteRecapEpochFinalResult>(
                        new WriteRecapEpochFinalResult.Installed("ok")
                    );
                },
                CancellationToken.None
            ).AsTask();

        await WaitForSignalAsync(slowStarted.Task, operation);
        await WaitForSignalAsync(fastFailed.Task, operation);
        releaseSlow.TrySetResult();
        SerialEpochKernelResult result = await operation;

        Assert.False(result.Succeeded);
        Assert.Equal(roster[0].RecapBlockId,
            result.PrimaryFailure!.RecapBlockId);
        Assert.Equal(roster[2].RecapBlockId, Assert.Single(installed));
        Assert.Equal(3, result.StartedCallCount);
    }

    [Fact]
    public async Task GroupExecutionPreflightFailureMakesZeroCalls() {
        RecapEpochBlockDefinition[] roster = Roster();
        DerivedRecapEpochInput input = CreateInput();
        DerivedRecapEpochManifest manifest = Manifest(input, roster);
        var failing = new ControlledMaintainer(
            roster[0],
            new object(),
            (_, _) => throw new InvalidOperationException("must not call"),
            createFailure: new InvalidOperationException("prefix failed")
        );
        var sibling = new ControlledMaintainer(
            roster[1],
            new object(),
            (_, _) => throw new InvalidOperationException("must not call")
        );

        SerialEpochKernelResult result =
            await DerivedRecapSerialEpochKernel.ExecuteAsync(
                Snapshot(manifest, input),
                new RecapBlockMaintainerRegistry([failing, sibling]),
                maxMaintainerCallsPerEpoch: 2,
                maxMaintainerCallsForOperation: 2,
                (_, _, _) => throw new InvalidOperationException(
                    "must not install"
                ),
                CancellationToken.None
            );

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.StartedCallCount);
        Assert.Equal("GroupExecutionUnavailable",
            result.PrimaryFailure!.Code);
        Assert.Empty(failing.Roles);
        Assert.Empty(sibling.Roles);
    }

    [Fact]
    public async Task RemainingOperationCapFailsBeforeGroupProjection() {
        RecapEpochBlockDefinition[] roster = Roster();
        DerivedRecapEpochInput input = CreateInput();
        var first = new ControlledMaintainer(
            roster[0],
            new object(),
            (_, _) => throw new InvalidOperationException("must not call")
        );
        var second = new ControlledMaintainer(
            roster[1],
            new object(),
            (_, _) => throw new InvalidOperationException("must not call")
        );

        InvalidDataException error = await Assert.ThrowsAsync<
            InvalidDataException
        >(async () => await DerivedRecapSerialEpochKernel.ExecuteAsync(
            Snapshot(Manifest(input, roster), input),
            new RecapBlockMaintainerRegistry([first, second]),
            maxMaintainerCallsPerEpoch: 2,
            maxMaintainerCallsForOperation: 1,
            (_, _, _) => throw new InvalidOperationException(
                "must not install"
            ),
            CancellationToken.None
        ));

        Assert.Contains("remaining operation limit", error.Message);
        Assert.Equal(0, first.CreateGroupExecutionCallCount);
        Assert.Equal(0, second.CreateGroupExecutionCallCount);
    }

    [Fact]
    public async Task ValueEqualAffinitiesRemainDistinctLeaderGroups() {
        RecapEpochBlockDefinition[] roster = Roster();
        DerivedRecapEpochInput input = CreateInput();
        var firstAffinity = new EqualAffinity("same");
        var secondAffinity = new EqualAffinity("same");
        var first = new ControlledMaintainer(
            roster[0],
            firstAffinity,
            (_, _) => ValueTask.FromResult<RecapMaintenanceSuccess>(
                new RecapMaintenanceSuccess.Updated("first")
            )
        );
        var second = new ControlledMaintainer(
            roster[1],
            secondAffinity,
            (_, _) => ValueTask.FromResult<RecapMaintenanceSuccess>(
                new RecapMaintenanceSuccess.Updated("second")
            )
        );

        SerialEpochKernelResult result =
            await DerivedRecapSerialEpochKernel.ExecuteAsync(
                Snapshot(Manifest(input, roster), input),
                new RecapBlockMaintainerRegistry([first, second]),
                maxMaintainerCallsPerEpoch: 2,
                maxMaintainerCallsForOperation: 2,
                (_, _, _) => ValueTask.FromResult<
                    WriteRecapEpochFinalResult
                >(new WriteRecapEpochFinalResult.Installed("ok")),
                CancellationToken.None
            );

        Assert.Equal(firstAffinity, secondAffinity);
        Assert.NotSame(firstAffinity, secondAffinity);
        Assert.True(result.Succeeded);
        Assert.Equal([RecapMaintainerCallRole.Leader], first.Roles);
        Assert.Equal([RecapMaintainerCallRole.Leader], second.Roles);
        Assert.Equal(1, first.CreateGroupExecutionCallCount);
        Assert.Equal(1, second.CreateGroupExecutionCallCount);
    }

    private static DerivedRecapEpochInput CreateInput() =>
        DerivedRecapV8Codec.CreateEpochInput(
            Boundary(A1),
            Boundary(A2),
            rawEventCount: 1,
            Hash0,
            [new ObservationMessage("history")],
            RecapEpochPrevious.Empty.Instance
        );

    private static TaskCompletionSource NewSignal() => new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private static async Task WaitForSignalAsync(
        Task signal,
        Task<SerialEpochKernelResult> operation
    ) {
        Task timeout = Task.Delay(TimeSpan.FromSeconds(5));
        Task completed = await Task.WhenAny(signal, operation, timeout);
        if (completed == signal) {
            await signal;
            return;
        }
        if (completed == operation) {
            SerialEpochKernelResult result = await operation;
            Assert.Fail(
                "Kernel completed before the expected concurrency signal: "
                + (result.PrimaryFailure?.Code ?? "success")
                + "."
            );
        }
        throw new TimeoutException(
            "Kernel did not reach the expected concurrency signal."
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

    private static RecapEpochBlockDefinition[] Roster(int count = 2) => [
        .. Enumerable.Range(0, count).Select(index => {
            string id = index switch {
                0 => "self",
                1 => "world",
                _ => $"world-{index}"
            };
            ContextHeaderCarrier carrier = index == 0
                ? ContextHeaderCarrier.System
                : ContextHeaderCarrier.Observation;
            return new RecapEpochBlockDefinition(
                new RecapBlockId(id),
                new ContextHeaderBlockPath(carrier, id),
                id,
                Capability,
                1024,
                index
            );
        })
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
        RecapMaintenanceSuccess result,
        object? runtimeGroupAffinity = null
    ) : IRecapBlockMaintainer {
        public string Id => definition.MaintainerId;
        public ContextHeaderBlockPath Target => definition.Target;
        public string CapabilityFingerprint =>
            definition.MaintainerCapabilityFingerprint;
        public object RuntimeGroupAffinity { get; } =
            runtimeGroupAffinity ?? new object();
        public List<RecapMaintenanceEpochInput> Inputs { get; } = [];
        public List<IRecapMaintenanceGroupExecution> GroupExecutions {
            get;
        } = [];
        public int CreateGroupExecutionCallCount { get; private set; }

        public IRecapMaintenanceGroupExecution CreateGroupExecution(
            RecapMaintenanceEpochInput input
        ) {
            CreateGroupExecutionCallCount++;
            return new FakeGroupExecution(RuntimeGroupAffinity, input);
        }

        public async ValueTask<RecapMaintenanceSuccess> MaintainAsync(
            IRecapMaintenanceGroupExecution groupExecution,
            IRecapMaintainerCallControl callControl,
            CancellationToken cancellationToken
        ) {
            await callControl.WaitForDispatchPermissionAsync(
                cancellationToken
            );
            callControl.MarkLaneAdmissionRequested();
            callControl.MarkDispatchStarted();
            GroupExecutions.Add(groupExecution);
            Inputs.Add(groupExecution.Input);
            return result;
        }
    }

    private sealed record FakeGroupExecution(
        object RuntimeGroupAffinity,
        RecapMaintenanceEpochInput Input
    ) : IRecapMaintenanceGroupExecution;

    private sealed class ControlledMaintainer(
        RecapEpochBlockDefinition definition,
        object runtimeGroupAffinity,
        Func<
            RecapMaintainerCallRole,
            CancellationToken,
            ValueTask<RecapMaintenanceSuccess>
        > maintain,
        Func<CancellationToken, ValueTask>? beforeAdmission = null,
        Exception? createFailure = null
    ) : IRecapBlockMaintainer {
        public string Id => definition.MaintainerId;

        public ContextHeaderBlockPath Target => definition.Target;

        public string CapabilityFingerprint =>
            definition.MaintainerCapabilityFingerprint;

        public object RuntimeGroupAffinity { get; } =
            runtimeGroupAffinity;

        public int CreateGroupExecutionCallCount { get; private set; }

        public ConcurrentQueue<RecapMaintainerCallRole> Roles {
            get;
        } = new();

        public IRecapMaintenanceGroupExecution CreateGroupExecution(
            RecapMaintenanceEpochInput input
        ) {
            CreateGroupExecutionCallCount++;
            if (createFailure is not null) {
                throw createFailure;
            }
            return new FakeGroupExecution(RuntimeGroupAffinity, input);
        }

        public async ValueTask<RecapMaintenanceSuccess> MaintainAsync(
            IRecapMaintenanceGroupExecution groupExecution,
            IRecapMaintainerCallControl callControl,
            CancellationToken cancellationToken
        ) {
            Assert.Same(RuntimeGroupAffinity,
                groupExecution.RuntimeGroupAffinity);
            await callControl.WaitForDispatchPermissionAsync(
                cancellationToken
            );
            if (beforeAdmission is not null) {
                await beforeAdmission(cancellationToken);
            }
            callControl.MarkLaneAdmissionRequested();
            callControl.MarkDispatchStarted();
            Roles.Enqueue(callControl.Role);
            return await maintain(callControl.Role, cancellationToken);
        }
    }

    private sealed record EqualAffinity(string Value);
}
