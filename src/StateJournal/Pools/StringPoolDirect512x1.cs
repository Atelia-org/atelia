using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Atelia.StateJournal.Pools;

internal sealed class StringPoolDirect512x1 : IMarkSweepPool<string> {
    private const int MaxPassthroughLength = 40;
    private const int IdentityCacheSetCount = 512;
    private const int IdentityCacheMask = IdentityCacheSetCount - 1;
    private const uint RefBitMask = 0x8000_0000u;
    private const uint EpochMask = ~RefBitMask;

    private readonly InternPool<string, OrdinalStaticEqualityComparer> _innerPool = new();
    private readonly Entry[] _identityEntries = new Entry[IdentityCacheSetCount];
    private uint _sweepEpoch;

    private struct Entry {
        public string? Key;
        public int ContentHash;
        public SlotHandle Handle;
        public uint EpochAndRefBit;
    }

    public int Count => _innerPool.Count;

    public int Capacity => _innerPool.Capacity;

    public string this[SlotHandle handle] {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _innerPool[handle];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginMark() => _innerPool.BeginMark();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkReachable(SlotHandle handle) => _innerPool.MarkReachable(handle);

    public int Sweep() {
        int freed = _innerPool.Sweep();
        unchecked { _sweepEpoch++; }
        return freed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(SlotHandle handle, out string value) => _innerPool.TryGetValue(handle, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Validate(SlotHandle handle) => _innerPool.Validate(handle);

    public SlotHandle Store(string value) {
        if (value.Length <= MaxPassthroughLength) {
            return _innerPool.Store(value);
        }

        int identityHash = RuntimeHelpers.GetHashCode(value);
        int index = identityHash & IdentityCacheMask;

        ref Entry entry = ref _identityEntries[index];
        if (ReferenceEquals(entry.Key, value)) {
            SetReferenced(ref entry);
            return RefreshHandle(value, ref entry);
        }

        int contentHash = OrdinalStaticEqualityComparer.GetHashCode(value);
        SlotHandle handle = _innerPool.StorePrehashed(value, contentHash);

        // 直接覆盖
        _identityEntries[index] = new Entry {
            Key = value,
            ContentHash = contentHash,
            Handle = handle,
            EpochAndRefBit = _sweepEpoch | RefBitMask
        };

        return handle;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SlotHandle RefreshHandle(string value, ref Entry entry) {
        if (GetEpoch(entry) == _sweepEpoch) {
            return entry.Handle;
        }

        Debug.Assert(entry.Key is not null);
        SlotHandle handle = _innerPool.StorePrehashed(value, entry.ContentHash);
        entry.Handle = handle;
        SetEpoch(ref entry, _sweepEpoch);
        return handle;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint GetEpoch(Entry entry) => entry.EpochAndRefBit & EpochMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetReferenced(ref Entry entry) => entry.EpochAndRefBit |= RefBitMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetEpoch(ref Entry entry, uint epoch) => entry.EpochAndRefBit = epoch | (entry.EpochAndRefBit & RefBitMask);
}
