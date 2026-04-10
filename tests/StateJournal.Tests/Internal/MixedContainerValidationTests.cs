using System.Reflection;
using Atelia.StateJournal.Pools;
using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

public class MixedContainerValidationTests {
    [Fact]
    public void MixedDict_ValidateReconstructed_DoesNotDependOnSymbolRefCountCache() {
        var rev = new Revision(1);
        var dict = rev.CreateDict<int>();
        dict.Upsert<string>(1, "alpha");

        ForceZeroSymbolRefCount(dict);

        var error = ((DurableObject)dict).ValidateReconstructed(tracker: null, symbolPool: StringPool.Rebuild([]));
        var corruption = Assert.IsType<SjCorruptionError>(error);
        Assert.Contains("MixedDict", corruption.Message);
    }

    [Fact]
    public void MixedDeque_ValidateReconstructed_DoesNotDependOnSymbolRefCountCache() {
        var rev = new Revision(1);
        var deque = rev.CreateDeque();
        deque.PushBack<string>("alpha");

        ForceZeroSymbolRefCount(deque);

        var error = ((DurableObject)deque).ValidateReconstructed(tracker: null, symbolPool: StringPool.Rebuild([]));
        var corruption = Assert.IsType<SjCorruptionError>(error);
        Assert.Contains("MixedDeque", corruption.Message);
    }

    [Fact]
    public void MixedOrderedDict_ValidateReconstructed_DoesNotDependOnSymbolRefCountCache() {
        var rev = new Revision(1);
        var dict = rev.CreateOrderedDict<int>();
        dict.Upsert<string>(1, "alpha");

        ForceZeroSymbolRefCount(dict);

        var error = ((DurableObject)dict).ValidateReconstructed(tracker: null, symbolPool: StringPool.Rebuild([]));
        var corruption = Assert.IsType<SjCorruptionError>(error);
        Assert.Contains("MixedOrderedDict", corruption.Message);
    }

    private static void ForceZeroSymbolRefCount(object container) {
        var field = container.GetType().GetField("_symbolRefCount", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(container, 0);
    }
}
