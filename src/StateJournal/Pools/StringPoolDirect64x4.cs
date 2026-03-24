using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Atelia.StateJournal.Pools;

internal sealed class StringPoolDirect64x4 : IMarkSweepPool<string> {
    private const int MaxPassthroughLength = 40;
    private const int IdentityCacheWays = 4;
    private const int IdentityCacheSetCount = 64;
    private const int IdentityCacheMask = IdentityCacheSetCount - 1;
    private const uint RefBitMask = 0x8000_0000u;
    private const uint EpochMask = ~RefBitMask;

    private readonly InternPool<string, OrdinalStaticEqualityComparer> _innerPool = new();
    private readonly Entry[] _identityEntries = new Entry[IdentityCacheSetCount * IdentityCacheWays];
    private readonly byte[] _setHands = new byte[IdentityCacheSetCount];
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

        if (TryGetIdentityCache(value, out SlotHandle handle)) {
            return handle;
        }

        int identityHash = RuntimeHelpers.GetHashCode(value);
        int contentHash = OrdinalStaticEqualityComparer.GetHashCode(value);
        handle = _innerPool.StorePrehashed(value, contentHash);
        InsertIdentityCache(value, identityHash, contentHash, handle);
        return handle;
    }

    private bool TryGetIdentityCache(string value, out SlotHandle handle) {
        int baseIndex = GetSetBaseIndex(value);

        {
            ref Entry e0 = ref _identityEntries[baseIndex];
            if (ReferenceEquals(e0.Key, value)) {
                SetReferenced(ref e0);
                handle = RefreshHandle(value, ref e0);
                return true;
            }
        }
        {
            ref Entry e1 = ref _identityEntries[baseIndex + 1];
            if (ReferenceEquals(e1.Key, value)) {
                SetReferenced(ref e1);
                handle = RefreshHandle(value, ref e1);
                return true;
            }
        }
        {
            ref Entry e2 = ref _identityEntries[baseIndex + 2];
            if (ReferenceEquals(e2.Key, value)) {
                SetReferenced(ref e2);
                handle = RefreshHandle(value, ref e2);
                return true;
            }
        }
        {
            ref Entry e3 = ref _identityEntries[baseIndex + 3];
            if (ReferenceEquals(e3.Key, value)) {
                SetReferenced(ref e3);
                handle = RefreshHandle(value, ref e3);
                return true;
            }
        }

        handle = default;
        return false;
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

    private void InsertIdentityCache(string value, int identityHash, int contentHash, SlotHandle handle) {
        int setIndex = identityHash & IdentityCacheMask;
        int baseIndex = setIndex * IdentityCacheWays;

        for (int i = 0; i < IdentityCacheWays; i++) {
            int slotIndex = baseIndex + i;
            ref Entry entry = ref _identityEntries[slotIndex];
            if (entry.Key is null) {
                _identityEntries[slotIndex] = CreateEntry(value, contentHash, handle);
                _setHands[setIndex] = (byte)((i + 1) & (IdentityCacheWays - 1));
                return;
            }
        }

        for (int i = 0; i < IdentityCacheWays; i++) {
            int offset = (_setHands[setIndex] + i) & (IdentityCacheWays - 1);
            int slotIndex = baseIndex + offset;
            ref Entry entry = ref _identityEntries[slotIndex];
            if (GetEpoch(entry) != _sweepEpoch) {
                _identityEntries[slotIndex] = CreateEntry(value, contentHash, handle);
                _setHands[setIndex] = (byte)((offset + 1) & (IdentityCacheWays - 1));
                return;
            }
        }

        while (true) {
            ref byte hand = ref _setHands[setIndex];
            int slotIndex = baseIndex + hand;
            ref Entry entry = ref _identityEntries[slotIndex];
            if (!IsReferenced(entry)) {
                _identityEntries[slotIndex] = CreateEntry(value, contentHash, handle);
                hand = (byte)((hand + 1) & (IdentityCacheWays - 1));
                return;
            }

            ClearReferenced(ref entry);
            hand = (byte)((hand + 1) & (IdentityCacheWays - 1));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetSetBaseIndex(string value) => GetSetBaseIndexFromIdentityHash(RuntimeHelpers.GetHashCode(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetSetBaseIndexFromIdentityHash(int identityHash) => (identityHash & IdentityCacheMask) * IdentityCacheWays;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entry CreateEntry(string value, int contentHash, SlotHandle handle) => new() {
        Key = value,
        ContentHash = contentHash,
        Handle = handle,
        EpochAndRefBit = _sweepEpoch | RefBitMask,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint GetEpoch(Entry entry) => entry.EpochAndRefBit & EpochMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsReferenced(Entry entry) => (entry.EpochAndRefBit & RefBitMask) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetReferenced(ref Entry entry) => entry.EpochAndRefBit |= RefBitMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClearReferenced(ref Entry entry) => entry.EpochAndRefBit &= EpochMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetEpoch(ref Entry entry, uint epoch) => entry.EpochAndRefBit = epoch | (entry.EpochAndRefBit & RefBitMask);
}
