// Source: Atelia.StateJournal - 工作空间
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §Workspace

using System.Buffers;
using System.Buffers.Binary;

namespace Atelia.StateJournal;

/// <summary>
/// 对象加载器委托（用于依赖注入存储层）。
/// </summary>
/// <param name="objectId">要加载的对象 ID。</param>
/// <returns>加载结果，成功时包含对象，失败时包含错误。</returns>
public delegate AteliaResult<IDurableObject> ObjectLoaderDelegate(ulong objectId);

/// <summary>
/// StateJournal 的工作空间，管理对象的创建、加载和提交。
/// </summary>
/// <remarks>
/// <para>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[S-OBJECTID-RESERVED-RANGE]</c>: Allocator MUST NOT 分配 ObjectId in 0..15</item>
///   <item><c>[S-CREATEOBJECT-IMMEDIATE-ALLOC]</c>: CreateObject MUST 立即分配 ObjectId</item>
///   <item><c>[S-NEW-OBJECT-AUTO-DIRTY]</c>: 新建对象 MUST 在创建时立即加入 Dirty Set</item>
///   <item><c>[S-OBJECTID-MONOTONIC-BOUNDARY]</c>: ObjectId 对"已提交对象集合"MUST 单调递增</item>
///   <item><c>[A-LOADOBJECT-RETURN-RESULT]</c>: LoadObject MUST 返回 AteliaResult&lt;T&gt;</item>
/// </list>
/// </para>
/// </remarks>
public class Workspace : IDisposable {
    private readonly IdentityMap _identityMap = new();
    private readonly DirtySet _dirtySet = new();
    private readonly ObjectLoaderDelegate? _objectLoader;
    private readonly VersionIndex _versionIndex;
    private ulong _nextObjectId;
    private ulong _versionIndexPtr;  // 当前 VersionIndex 的位置
    private ulong _epochSeq;         // 当前 epoch 序号
    private ulong _dataTail;         // data file 当前尾部

    // MVP: 简化版，不含存储层
    // Phase 5 会添加 IRbfFramer/IRbfScanner

    /// <summary>
    /// 创建 Workspace（NextObjectId 从 16 开始）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Well-Known ObjectId 保留区：
    /// <list type="bullet">
    ///   <item><c>0</c>: VersionIndex — 系统级索引对象</item>
    ///   <item><c>1..15</c>: 保留给未来 Well-Known 对象</item>
    ///   <item><c>16..</c>: 用户对象分配区</item>
    /// </list>
    /// </para>
    /// </remarks>
    public Workspace() : this(objectLoader: null) { }

    /// <summary>
    /// 创建带对象加载器的 Workspace。
    /// </summary>
    /// <param name="objectLoader">对象加载器委托，用于从存储加载对象。可以为 null（MVP 测试场景）。</param>
    /// <remarks>
    /// <para>
    /// MVP 阶段：通过委托注入存储加载逻辑，Phase 5 会实现完整的存储层。
    /// </para>
    /// </remarks>
    public Workspace(ObjectLoaderDelegate? objectLoader) {
        _nextObjectId = 16;  // [S-OBJECTID-RESERVED-RANGE]
        _objectLoader = objectLoader;
        _versionIndex = new VersionIndex();
        _versionIndexPtr = 0;
        _epochSeq = 0;
        _dataTail = 0;
    }

    /// <summary>
    /// 创建 Workspace（从指定 NextObjectId 恢复，用于 Recovery）。
    /// </summary>
    /// <param name="nextObjectId">下一个可分配的 ObjectId。</param>
    /// <param name="objectLoader">对象加载器委托，用于从存储加载对象。可以为 null。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="nextObjectId"/> 小于 16（落入保留区）。
    /// </exception>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[S-OBJECTID-RESERVED-RANGE]</c>
    /// </para>
    /// </remarks>
    internal Workspace(ulong nextObjectId, ObjectLoaderDelegate? objectLoader = null) {
        if (nextObjectId < 16) {
            throw new ArgumentOutOfRangeException(
                nameof(nextObjectId),
                nextObjectId,
                "NextObjectId must be >= 16 (reserved range)."
            );
        }
        _nextObjectId = nextObjectId;
        _objectLoader = objectLoader;
        _versionIndex = new VersionIndex();
        _versionIndexPtr = 0;
        _epochSeq = 0;
        _dataTail = 0;
    }

    /// <summary>
    /// 从恢复信息创建 Workspace。
    /// </summary>
    /// <param name="info">恢复信息，通常由 <see cref="WorkspaceRecovery.Recover"/> 返回。</param>
    /// <param name="objectLoader">对象加载器委托，用于从存储加载对象。可以为 null。</param>
    /// <returns>恢复状态后的 Workspace。</returns>
    /// <remarks>
    /// <para>
    /// 对应条款：
    /// <list type="bullet">
    ///   <item><c>[R-META-AHEAD-BACKTRACK]</c>: 恢复到最后一个有效的 commit point</item>
    ///   <item><c>[R-DATATAIL-TRUNCATE-SAFETY]</c>: data file 截断到 DataTail 是安全的</item>
    /// </list>
    /// </para>
    /// </remarks>
    public static Workspace Open(RecoveryInfo info, ObjectLoaderDelegate? objectLoader = null) {
        return new Workspace(info.NextObjectId, objectLoader) {
            _epochSeq = info.EpochSeq,
            _dataTail = info.DataTail,
            _versionIndexPtr = info.VersionIndexPtr,
        };
    }

    /// <summary>
    /// 创建新的持久化对象。
    /// </summary>
    /// <typeparam name="T">对象类型，必须实现 <see cref="IDurableObject"/> 且有接受 <c>ulong objectId</c> 的构造函数。</typeparam>
    /// <returns>新创建的对象。</returns>
    /// <remarks>
    /// <para>
    /// 对应条款：
    /// <list type="bullet">
    ///   <item><c>[S-CREATEOBJECT-IMMEDIATE-ALLOC]</c>: 立即分配 ObjectId</item>
    ///   <item><c>[S-NEW-OBJECT-AUTO-DIRTY]</c>: 自动加入 Dirty Set</item>
    ///   <item><c>[S-OBJECTID-MONOTONIC-BOUNDARY]</c>: ObjectId 单调递增</item>
    /// </list>
    /// </para>
    /// <para>
    /// 流程：
    /// <list type="number">
    ///   <item>分配 ObjectId（单调递增）</item>
    ///   <item>创建对象（TransientDirty 状态）</item>
    ///   <item>加入 Identity Map（WeakRef）与 Dirty Set（强引用）</item>
    /// </list>
    /// </para>
    /// </remarks>
    public T CreateObject<T>() where T : IDurableObject {
        // 1. 分配 ObjectId [S-OBJECTID-MONOTONIC-BOUNDARY]
        var objectId = _nextObjectId++;

        // 2. 创建对象（TransientDirty 状态）
        var obj = CreateInstance<T>(objectId);

        // 3. 加入 Identity Map 和 Dirty Set [S-NEW-OBJECT-AUTO-DIRTY]
        _identityMap.Add(obj);
        _dirtySet.Add(obj);

        return obj;
    }

    /// <summary>
    /// 加载指定 ObjectId 的对象。
    /// </summary>
    /// <typeparam name="T">对象类型，必须实现 <see cref="IDurableObject"/>。</typeparam>
    /// <param name="objectId">对象 ID。</param>
    /// <returns>成功返回对象，失败返回错误。</returns>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[A-LOADOBJECT-RETURN-RESULT]</c>
    /// </para>
    /// <para>
    /// 加载流程：
    /// <list type="number">
    ///   <item>查 Identity Map → 命中且 alive → 返回</item>
    ///   <item>未命中或 dead → 从存储加载</item>
    ///   <item>加入 Identity Map（WeakRef），不加入 DirtySet（Clean 状态）</item>
    /// </list>
    /// </para>
    /// </remarks>
    public AteliaResult<T> LoadObject<T>(ulong objectId) where T : class, IDurableObject {
        // 1. 查 Identity Map
        if (_identityMap.TryGet(objectId, out var cached)) {
            if (cached is T typedObj) { return AteliaResult<T>.Success(typedObj); }

            // 类型不匹配
            return AteliaResult<T>.Failure(
                new ObjectTypeMismatchError(
                    objectId, typeof(T), cached.GetType()
                )
            );
        }

        // 2. 尝试从存储加载（MVP: 使用注入的 Loader）
        var loadResult = _objectLoader?.Invoke(objectId);
        if (loadResult is null) {
            // 无 loader 配置（MVP 测试场景）
            return AteliaResult<T>.Failure(new ObjectNotFoundError(objectId));
        }

        if (loadResult.Value.IsFailure) { return AteliaResult<T>.Failure(loadResult.Value.Error!); }

        var obj = loadResult.Value.Value!;
        if (obj is not T typedLoaded) {
            return AteliaResult<T>.Failure(
                new ObjectTypeMismatchError(
                    objectId, typeof(T), obj.GetType()
                )
            );
        }

        // 3. 加入 Identity Map（不加入 DirtySet，因为是 Clean 状态）
        _identityMap.Add(obj);

        return AteliaResult<T>.Success(typedLoaded);
    }

    /// <summary>
    /// 创建带类型约束的对象实例。
    /// </summary>
    /// <typeparam name="T">对象类型。</typeparam>
    /// <param name="objectId">对象 ID。</param>
    /// <returns>新创建的对象。</returns>
    /// <exception cref="InvalidOperationException">无法创建对象实例。</exception>
    private static T CreateInstance<T>(ulong objectId) where T : IDurableObject {
        // 使用 Activator 创建带 objectId 参数的实例
        var instance = Activator.CreateInstance(typeof(T), objectId);
        if (instance is not T obj) {
            throw new InvalidOperationException(
                $"Failed to create instance of {typeof(T).Name} with objectId {objectId}."
            );
        }
        return obj;
    }

    /// <summary>
    /// 下一个可分配的 ObjectId（用于 Commit 持久化）。
    /// </summary>
    public ulong NextObjectId => _nextObjectId;

    /// <summary>
    /// 脏对象数量。
    /// </summary>
    public int DirtyCount => _dirtySet.Count;

    /// <summary>
    /// Identity Map 中的对象数量（包括可能已失效的 WeakReference）。
    /// </summary>
    public int CachedCount => _identityMap.Count;

    /// <summary>
    /// 当前 Epoch 序号。
    /// </summary>
    public ulong EpochSeq => _epochSeq;

    /// <summary>
    /// 当前 Data Tail。
    /// </summary>
    public ulong DataTail => _dataTail;

    /// <summary>
    /// 当前 VersionIndex 指针。
    /// </summary>
    public ulong VersionIndexPtr => _versionIndexPtr;

    // ========================================================================
    // Two-Phase Commit API
    // ========================================================================

    /// <summary>
    /// 准备提交所有脏对象（Two-Phase Commit: Phase 1）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase 1: 写 Data
    /// <list type="number">
    ///   <item>遍历 DirtySet</item>
    ///   <item>对每个脏对象调用 WritePendingDiff</item>
    ///   <item>更新 VersionIndex</item>
    /// </list>
    /// </para>
    /// <para>
    /// 返回 CommitContext 供 Phase 2 使用（写 Meta）。
    /// </para>
    /// <para>
    /// 对应条款：
    /// <list type="bullet">
    ///   <item><c>[A-COMMITALL-DIRTY-ITERATION]</c>: 遍历所有脏对象</item>
    ///   <item><c>[A-COMMITALL-WRITE-DIFF]</c>: 调用 WritePendingDiff 序列化</item>
    ///   <item><c>[A-COMMITALL-UPDATE-VERSIONINDEX]</c>: 更新 VersionIndex 映射</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <returns>提交上下文，包含写入的记录和元数据。</returns>
    public CommitContext PrepareCommit() {
        var context = new CommitContext {
            EpochSeq = _epochSeq + 1,
            DataTail = _dataTail,
        };

        // 遍历所有脏对象 [A-COMMITALL-DIRTY-ITERATION]
        foreach (var obj in _dirtySet.GetAll()) {
            if (!obj.HasChanges) { continue; /* 可能已经被其他操作清理 */ }

            // 序列化 diff [A-COMMITALL-WRITE-DIFF]
            var buffer = new ArrayBufferWriter<byte>();

            // 写入 PrevVersionPtr（8 bytes LE）
            var prevPtr = GetPrevVersionPtr(obj.ObjectId);
            var ptrSpan = buffer.GetSpan(8);
            BinaryPrimitives.WriteUInt64LittleEndian(ptrSpan, prevPtr);
            buffer.Advance(8);

            // 写入 DiffPayload
            obj.WritePendingDiff(buffer);

            // 确定 FrameTag
            var frameTag = GetFrameTag(obj);

            // 写入记录并获取位置
            var position = context.WriteObjectVersion(obj.ObjectId, buffer.WrittenSpan, frameTag.Value);

            // 更新 VersionIndex [A-COMMITALL-UPDATE-VERSIONINDEX]
            _versionIndex.SetObjectVersionPtr(obj.ObjectId, position);
        }

        // 如果 VersionIndex 有变更，也需要写入
        if (_versionIndex.HasChanges) {
            var buffer = new ArrayBufferWriter<byte>();

            // 写入 PrevVersionPtr
            var prevPtr = _versionIndexPtr;
            var ptrSpan = buffer.GetSpan(8);
            BinaryPrimitives.WriteUInt64LittleEndian(ptrSpan, prevPtr);
            buffer.Advance(8);

            _versionIndex.WritePendingDiff(buffer);

            var frameTag = StateJournalFrameTag.DictVersion;
            var position = context.WriteObjectVersion(VersionIndex.WellKnownObjectId, buffer.WrittenSpan, frameTag.Value);

            // 记录新的 VersionIndex 位置
            context.VersionIndexPtr = position;
        }
        else {
            context.VersionIndexPtr = _versionIndexPtr;
        }

        return context;
    }

    /// <summary>
    /// 完成提交：清理内存状态，对象转为 Clean。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 此方法应在 meta file fsync 成功后调用（Two-Phase Commit: Phase 2 完成后）。
    /// </para>
    /// <para>
    /// 对应条款：<c>[R-COMMIT-POINT-META-FSYNC]</c>: Commit Point = Meta fsync 完成时刻
    /// </para>
    /// </remarks>
    /// <param name="context">PrepareCommit 返回的提交上下文。</param>
    public void FinalizeCommit(CommitContext context) {
        // 1. 更新内部状态
        _epochSeq = context.EpochSeq;
        _dataTail = context.DataTail;
        _versionIndexPtr = context.VersionIndexPtr;

        // 2. 对所有脏对象调用 OnCommitSucceeded
        foreach (var obj in _dirtySet.GetAll().ToList()) {  // ToList 避免迭代时修改
            obj.OnCommitSucceeded();
        }

        // 3. 对 VersionIndex 调用 OnCommitSucceeded
        _versionIndex.OnCommitSucceeded();

        // 4. 清空 DirtySet
        _dirtySet.Clear();
    }

    /// <summary>
    /// 执行完整的提交流程（PrepareCommit + FinalizeCommit）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// MVP 版本：不含实际 I/O，用于测试验证。
    /// 生产版本需要在 PrepareCommit 和 FinalizeCommit 之间执行 fsync。
    /// </para>
    /// <para>
    /// 对应条款：<c>[R-COMMIT-FSYNC-ORDER]</c>: 先 fsync data，再 fsync meta
    /// </para>
    /// </remarks>
    /// <returns>提交上下文，包含写入的记录和元数据。</returns>
    public CommitContext Commit() {
        var context = PrepareCommit();
        FinalizeCommit(context);
        return context;
    }

    /// <summary>
    /// 尝试获取对象的版本指针。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    /// <param name="versionPtr">输出的版本指针。</param>
    /// <returns>如果找到则返回 true。</returns>
    public bool TryGetVersionPtr(ulong objectId, out ulong versionPtr)
        => _versionIndex.TryGetObjectVersionPtr(objectId, out versionPtr);

    /// <summary>
    /// 获取对象的前一个版本指针。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    /// <returns>前一个版本的位置，如果是基础版本则返回 0。</returns>
    private ulong GetPrevVersionPtr(ulong objectId) {
        if (_versionIndex.TryGetObjectVersionPtr(objectId, out var ptr)) { return ptr; }
        return 0;  // Base version
    }

    /// <summary>
    /// 获取对象的 FrameTag。
    /// </summary>
    /// <param name="obj">持久化对象。</param>
    /// <returns>FrameTag。</returns>
    private static Atelia.Rbf.FrameTag GetFrameTag(IDurableObject obj) {
        // MVP: 只支持 DurableDict
        _ = obj;  // Suppress unused parameter warning
        return StateJournalFrameTag.DictVersion;
    }

    /// <summary>
    /// 释放工作空间资源。
    /// </summary>
    /// <remarks>
    /// <para>
    /// MVP 阶段简单实现：清空 Dirty Set。
    /// </para>
    /// </remarks>
    public void Dispose() {
        // 清理资源（MVP 阶段简单实现）
        _dirtySet.Clear();
    }
}
