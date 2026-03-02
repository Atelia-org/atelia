namespace Atelia.StateJournal.Internal;

/// <summary>将 key 划分为 false/true 两个子集的公共契约。</summary>
/// <remarks>
/// <c>FalseKeys</c> / <c>TrueKeys</c> 因返回各自的 <c>ref struct Enumerator</c> 而无法统一进接口——
/// 将它们纳入接口需要引入第二个类型参数，反而会破坏多态。枚举操作请通过具体类型访问。
/// </remarks>
internal interface IBoolDivision<TKey> where TKey : notnull {
    int Capacity { get; }
    int Count { get; }
    int FalseCount { get; }
    int TrueCount { get; }

    /// <summary>将 key 放入 false 子集。若 key 不存在则新增；若已在 true 子集则 O(1) 移动。</summary>
    void SetFalse(TKey key);

    /// <summary>将 key 放入 true 子集。若 key 不存在则新增；若已在 false 子集则 O(1) 移动。</summary>
    void SetTrue(TKey key);

    /// <summary>从任一子集中移除 key。key 不存在时为 no-op。</summary>
    void Remove(TKey key);

    /// <summary>清空所有条目，恢复到初始空状态（保留已分配的数组）。</summary>
    void Clear();
}
