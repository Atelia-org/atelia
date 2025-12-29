// Source: Atelia.StateJournal - 延迟加载引用
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §LazyRef

namespace Atelia.StateJournal;

/// <summary>
/// 延迟加载的对象引用，支持透明加载和回填缓存。
/// </summary>
/// <typeparam name="T">对象类型，必须继承 <see cref="DurableObjectBase"/>。</typeparam>
/// <remarks>
/// <para>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[A-OBJREF-BACKFILL-CURRENT]</c>: LazyRef 加载后回填缓存，后续访问不重复加载</item>
///   <item><c>[A-OBJREF-TRANSPARENT-LAZY-LOAD]</c>: 透明加载，API 返回的都是对象实例</item>
/// </list>
/// </para>
/// <para>
/// 内部存储状态：
/// <list type="bullet">
///   <item><c>null</c>: 未初始化</item>
///   <item><c>ulong</c>: 延迟加载状态，存储 ObjectId</item>
///   <item><c>T</c>: 已加载状态，存储对象实例</item>
/// </list>
/// </para>
/// </remarks>
public struct LazyRef<T> where T : DurableObjectBase {
    private object? _storage;  // null, ulong (ObjectId), 或 T 实例
    private readonly Workspace? _workspace;

    /// <summary>
    /// 从 ObjectId 创建延迟引用。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    /// <param name="workspace">工作空间（用于加载对象）。</param>
    public LazyRef(ulong objectId, Workspace workspace) {
        _storage = objectId;
        _workspace = workspace;
    }

    /// <summary>
    /// 从已加载的对象创建引用（无需延迟加载）。
    /// </summary>
    /// <param name="instance">已加载的对象实例。</param>
    public LazyRef(T instance) {
        _storage = instance;
        _workspace = null;
    }

    /// <summary>
    /// 获取引用的对象（触发延迟加载如果需要）。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// LazyRef 未初始化、无法加载对象、或内部状态异常时抛出。
    /// </exception>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[A-OBJREF-TRANSPARENT-LAZY-LOAD]</c>
    /// </para>
    /// </remarks>
    public T Value {
        get {
            return _storage switch {
                T instance => instance,
                ulong objectId => LoadAndCache(objectId),
                null => throw new InvalidOperationException("LazyRef is not initialized."),
                _ => throw new InvalidOperationException($"Invalid storage type: {_storage.GetType()}.")
            };
        }
    }

    /// <summary>
    /// 尝试获取引用的对象。
    /// </summary>
    /// <returns>成功返回对象，失败返回错误。</returns>
    /// <remarks>
    /// <para>
    /// 与 <see cref="Value"/> 不同，此方法不抛异常，而是返回 <see cref="AteliaResult{T}"/>。
    /// </para>
    /// </remarks>
    public AteliaResult<T> TryGetValue() {
        return _storage switch {
            T instance => AteliaResult<T>.Success(instance),
            ulong objectId => TryLoadAndCache(objectId),
            null => AteliaResult<T>.Failure(new LazyRefNotInitializedError()),
            _ => AteliaResult<T>.Failure(new LazyRefInvalidStorageError(_storage.GetType()))
        };
    }

    /// <summary>
    /// 引用的 ObjectId。
    /// </summary>
    /// <exception cref="InvalidOperationException">LazyRef 未初始化时抛出。</exception>
    /// <remarks>
    /// <para>
    /// ObjectId 在加载前即可获取，无需触发延迟加载。
    /// </para>
    /// </remarks>
    public ulong ObjectId => _storage switch {
        T instance => instance.ObjectId,
        ulong id => id,
        _ => throw new InvalidOperationException("LazyRef is not initialized.")
    };

    /// <summary>
    /// 是否已加载（缓存中有实例）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>true</c> 表示 <see cref="Value"/> 访问不会触发存储 I/O。
    /// </para>
    /// </remarks>
    public bool IsLoaded => _storage is T;

    /// <summary>
    /// 是否已初始化。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>false</c> 表示 LazyRef 是默认构造的空值，任何访问都会失败。
    /// </para>
    /// </remarks>
    public bool IsInitialized => _storage is not null;

    /// <summary>
    /// 加载对象并回填缓存。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    /// <returns>加载的对象实例。</returns>
    /// <exception cref="InvalidOperationException">
    /// workspace 为 null 或加载失败时抛出。
    /// </exception>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[A-OBJREF-BACKFILL-CURRENT]</c>
    /// </para>
    /// </remarks>
    private T LoadAndCache(ulong objectId) {
        if (_workspace is null) { throw new InvalidOperationException("Cannot load: workspace is null."); }

        var result = _workspace.LoadAs<T>(objectId);
        if (result.IsFailure) {
            throw new InvalidOperationException(
                $"Failed to load referenced object {objectId}: {result.Error!.Message}"
            );
        }

        _storage = result.Value;  // 回填 [A-OBJREF-BACKFILL-CURRENT]
        return result.Value!;
    }

    /// <summary>
    /// 尝试加载对象并回填缓存（不抛异常）。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    /// <returns>加载结果。</returns>
    private AteliaResult<T> TryLoadAndCache(ulong objectId) {
        if (_workspace is null) { return AteliaResult<T>.Failure(new LazyRefNoWorkspaceError()); }

        var result = _workspace.LoadAs<T>(objectId);
        if (result.IsSuccess) {
            _storage = result.Value;  // 回填 [A-OBJREF-BACKFILL-CURRENT]
        }
        return result;
    }
}
