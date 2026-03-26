namespace Atelia.StateJournal;

/// <summary>
/// LocalId 空间耗尽异常。当高水位标记到达 <c>uint.MaxValue</c> 且所有空洞已用完时抛出。
/// </summary>
/// <remarks>
/// 调用方应捕获此异常并通过重建 <see cref="Internal.LocalIdAllocator"/> 或迁移到新的 <see cref="Revision"/> 来回收已删除对象遗留的 ID 空间。
/// </remarks>
public class LocalIdExhaustedException : InvalidOperationException {
    public LocalIdExhaustedException()
        : base("LocalId space exhausted: high-water mark has wrapped past uint.MaxValue. Rebuild the allocator or migrate to a fresh Revision to reclaim freed IDs.") { }
}
