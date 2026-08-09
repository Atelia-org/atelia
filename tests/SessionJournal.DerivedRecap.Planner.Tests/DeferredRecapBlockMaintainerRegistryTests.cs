using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class DeferredRecapBlockMaintainerRegistryTests {
    private static readonly ContextHeaderBlockPath Target = new(
        ContextHeaderCarrier.System,
        "self-recap"
    );

    [Fact]
    public void ConstructionIsZeroTouchAndHitMissShareOneInner() {
        int factoryCalls = 0;
        var maintainer = new StubMaintainer("self", Target);
        var registry = new DeferredRecapBlockMaintainerRegistry(() => {
            factoryCalls++;
            return new RecapBlockMaintainerRegistry([maintainer]);
        });

        Assert.Equal(0, factoryCalls);

        Assert.True(registry.TryResolve(
            maintainer.Id,
            Target,
            maintainer.CapabilityFingerprint,
            out IRecapBlockMaintainer resolved
        ));
        Assert.Same(maintainer, resolved);
        Assert.False(registry.TryResolve(
            "missing",
            Target,
            RecapPlannerTestIdentity.CapabilityFingerprint,
            out IRecapBlockMaintainer? missing
        ));
        Assert.Null(missing);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task ConcurrentFirstLookupsPublishOneInner() {
        int factoryCalls = 0;
        var maintainer = new StubMaintainer("self", Target);
        var factoryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFactory = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var registry = new DeferredRecapBlockMaintainerRegistry(() => {
            Interlocked.Increment(ref factoryCalls);
            factoryEntered.TrySetResult();
            releaseFactory.Task.GetAwaiter().GetResult();
            return new RecapBlockMaintainerRegistry([maintainer]);
        });

        Task<bool>[] lookups = [
            .. Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
                registry.TryResolve(
                    maintainer.Id,
                    Target,
                    maintainer.CapabilityFingerprint,
                    out IRecapBlockMaintainer resolved
                ) && ReferenceEquals(maintainer, resolved)
            ))
        ];

        await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseFactory.TrySetResult();
        bool[] results = await Task.WhenAll(lookups);

        Assert.All(results, Assert.True);
        Assert.Equal(1, Volatile.Read(ref factoryCalls));
    }

    [Fact]
    public void FactoryExceptionIsCachedWithoutRetry() {
        int factoryCalls = 0;
        var expected = new IOException("factory unavailable");
        var registry = new DeferredRecapBlockMaintainerRegistry(() => {
            factoryCalls++;
            throw expected;
        });

        IOException first = Assert.Throws<IOException>(() =>
            registry.TryResolve(
                "self",
                Target,
                RecapPlannerTestIdentity.CapabilityFingerprint,
                out _
            )
        );
        IOException second = Assert.Throws<IOException>(() =>
            registry.TryResolve(
                "self",
                Target,
                RecapPlannerTestIdentity.CapabilityFingerprint,
                out _
            )
        );

        Assert.Same(expected, first);
        Assert.Same(expected, second);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public void NullInnerIsRejectedAndFailureIsCached() {
        int factoryCalls = 0;
        var registry = new DeferredRecapBlockMaintainerRegistry(() => {
            factoryCalls++;
            return null!;
        });

        InvalidOperationException first =
            Assert.Throws<InvalidOperationException>(() =>
                registry.TryResolve(
                    "self",
                    Target,
                    RecapPlannerTestIdentity.CapabilityFingerprint,
                    out _
                )
            );
        InvalidOperationException second =
            Assert.Throws<InvalidOperationException>(() =>
                registry.TryResolve(
                    "self",
                    Target,
                    RecapPlannerTestIdentity.CapabilityFingerprint,
                    out _
                )
            );

        Assert.Contains("returned null", first.Message);
        Assert.Same(first, second);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public void EmptyInnerIsValidAndReturnsMiss() {
        int factoryCalls = 0;
        var registry = new DeferredRecapBlockMaintainerRegistry(() => {
            factoryCalls++;
            return new RecapBlockMaintainerRegistry([]);
        });

        Assert.False(registry.TryResolve(
            "missing",
            Target,
            RecapPlannerTestIdentity.CapabilityFingerprint,
            out IRecapBlockMaintainer? maintainer
        ));
        Assert.Null(maintainer);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public void SameIdAndTargetResolveByExactFingerprint() {
        var oldCapability = new StubMaintainer(
            "self",
            Target,
            RecapPlannerTestIdentity.CapabilityFingerprint
        );
        var newCapability = new StubMaintainer(
            "self",
            Target,
            "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
        );
        var registry = new RecapBlockMaintainerRegistry([
            oldCapability,
            newCapability
        ]);

        Assert.True(registry.TryResolve(
            "self",
            Target,
            oldCapability.CapabilityFingerprint,
            out IRecapBlockMaintainer oldResolved
        ));
        Assert.Same(oldCapability, oldResolved);
        Assert.True(registry.TryResolve(
            "self",
            Target,
            newCapability.CapabilityFingerprint,
            out IRecapBlockMaintainer newResolved
        ));
        Assert.Same(newCapability, newResolved);
        Assert.False(registry.TryResolve(
            "self",
            Target,
            "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            out _
        ));
    }

    [Fact]
    public void RegistryRejectsMalformedCapabilityFingerprint() {
        var malformed = new StubMaintainer(
            "self",
            Target,
            "sha256:ABCDEF"
        );

        Assert.Throws<ArgumentException>(
            () => new RecapBlockMaintainerRegistry([malformed])
        );
    }

    private sealed class StubMaintainer(
        string id,
        ContextHeaderBlockPath target,
        string capabilityFingerprint =
            RecapPlannerTestIdentity.CapabilityFingerprint
    ) : IRecapBlockMaintainer {
        public string Id { get; } = id;
        public string CapabilityFingerprint { get; } =
            capabilityFingerprint;
        public ContextHeaderBlockPath Target { get; } = target;
        public object RuntimeGroupAffinity => this;

        public IRecapMaintenanceGroupExecution CreateGroupExecution(
            RecapMaintenanceEpochInput input
        ) => new StubGroupExecution(this, input);

        public ValueTask<RecapMaintenanceSuccess> MaintainAsync(
            IRecapMaintenanceGroupExecution groupExecution,
            IRecapMaintainerCallControl callControl,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed record StubGroupExecution(
        object RuntimeGroupAffinity,
        RecapMaintenanceEpochInput Input
    ) : IRecapMaintenanceGroupExecution;
}
