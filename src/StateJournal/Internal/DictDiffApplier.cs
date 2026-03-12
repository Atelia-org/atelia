using System.Runtime.InteropServices;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

// ai:test `tests/StateJournal.Tests/Serialization/DictDiffApplierTests.cs`
internal static class DictDiffApplier {
    public static void Apply<TKey, TValue, KHelper, VHelper>(ref BinaryDiffReader reader, Dictionary<TKey, TValue?> target)
    where TKey : notnull where TValue : notnull
    where KHelper : ITypeHelper<TKey>
    where VHelper : ITypeHelper<TValue> {
        for (int i = reader.ReadCount(); --i >= 0;) {
            TKey key = KHelper.Read(ref reader, asKey: true) ?? throw new InvalidDataException("Diff stream contains a null key.");
            if (!target.Remove(key)) { throw new InvalidDataException($"Diff remove refers to missing key '{key}'."); }
        }
        for (int i = reader.ReadCount(); --i >= 0;) {
            TKey key = KHelper.Read(ref reader, asKey: true) ?? throw new InvalidDataException("Diff stream contains a null key.");
            ref TValue? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(target, key, out bool exists);
            VHelper.UpdateOrInit(ref reader, ref slot);
        }
    }
}
