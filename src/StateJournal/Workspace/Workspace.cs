// Source: Atelia.StateJournal - 工作空间
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §Workspace

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
    private ulong _nextObjectId;

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
