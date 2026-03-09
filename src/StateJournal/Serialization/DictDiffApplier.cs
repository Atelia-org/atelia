using System.Runtime.InteropServices;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal.Serialization;

// ai:test `tests/StateJournal.Tests/Serialization/DictDiffApplierTests.cs`
internal class DictDiffApplier {
    public void Apply<TKey, TValue, KHelper, VHelper>(ref BinaryDiffReader reader, Dictionary<TKey, TValue?> target)
    where TKey : notnull where TValue : notnull
    where KHelper : unmanaged, ITypeHelper<TKey>
    where VHelper : unmanaged, ITypeHelper<TValue> {
        {
            int removeCount = reader.ReadCount();
            for (int i = 0; i < removeCount; i++) {
                TKey key = KHelper.Read(ref reader, asKey: true) ?? throw new InvalidDataException("Diff stream contains a null key.");
                if (!target.Remove(key)) { throw new InvalidDataException($"Diff remove refers to missing key '{key}'."); }
            }
        }
        {
            int upsertCount = reader.ReadCount();
            for (int i = 0; i < upsertCount; i++) {
                TKey key = KHelper.Read(ref reader, asKey: true) ?? throw new InvalidDataException("Diff stream contains a null key.");
                ref TValue? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(target, key, out bool exists);
                VHelper.UpdateOrInit(ref reader, ref slot);
            }
        }
    }
}
