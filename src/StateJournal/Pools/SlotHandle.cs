using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Atelia.StateJournal.Pools;

/// <summary>
/// 强类型 Slot Handle：高 8 bit = Generation，低 24 bit = Index。
/// 打包在一个 <see cref="uint"/> 中，兼顾 ABA 校验与内存紧凑性。
/// </summary>
/// <remarks>
///
/// Generation 在 <see cref="SlotPool{T}.Free(SlotHandle)"/> 时递增（8-bit 自然回绕），
/// 用于检测对已释放 slot 的过期访问（Stale Handle Detection）。
///
///
/// 24-bit Index 支持最多 16,777,216 个 slot（约 16M）。
/// 若需更大地址空间，可后续调整为 7-bit Gen + 25-bit Index。
///
///
/// <b>相等性</b>：两个 Handle 当且仅当 Generation 与 Index 都相同时才相等。
/// 同一 slot 被释放后重新分配，旧 Handle 与新 Handle 不相等。
///
/// </remarks>
public readonly record struct SlotHandle {
    internal const int IndexBits = 24;
    internal const int GenerationBits = 8;
    internal const uint IndexMask = (1u << IndexBits) - 1; // 0x00FF_FFFF

    /// <summary>24-bit 寻址空间支持的最大 slot index (16,777,215)。</summary>
    public const int MaxIndex = (int)IndexMask;

    private readonly uint _packed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SlotHandle(byte generation, int index) {
        Debug.Assert((uint)index <= MaxIndex);
        _packed = ((uint)generation << IndexBits) | ((uint)index & IndexMask);
    }

    internal SlotHandle(uint packed) => _packed = packed;

    /// <summary>8-bit Generation 值，用于 ABA 校验。</summary>
    public byte Generation {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (byte)(_packed >> IndexBits);
    }

    /// <summary>24-bit Slot Index。</summary>
    public int Index {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (int)(_packed & IndexMask);
    }

    public uint Packed => _packed;

    /// <inheritdoc/>
    public override string ToString() => $"SlotHandle(gen={Generation}, idx={Index})";
}
