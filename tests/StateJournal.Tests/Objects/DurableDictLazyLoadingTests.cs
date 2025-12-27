using Atelia.StateJournal;
using FluentAssertions;
using Xunit;
using Ws = Atelia.StateJournal.Workspace;

namespace Atelia.StateJournal.Tests.Objects;

/// <summary>
/// DurableDict Lazy Loading 测试（对象引用透明加载）。
/// </summary>
/// <remarks>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[A-OBJREF-TRANSPARENT-LAZY-LOAD]</c></item>
///   <item><c>[A-OBJREF-BACKFILL-CURRENT]</c></item>
/// </list>
/// </remarks>
public class DurableDictLazyLoadingTests {

    /// <summary>
    /// TryGetValue 遇到 ObjectId 时执行透明 Lazy Load。
    /// </summary>
    /// <remarks>
    /// 对应条款：<c>[A-OBJREF-TRANSPARENT-LAZY-LOAD]</c>
    /// </remarks>
    [Fact]
    public void TryGetValue_ObjectId_TriggersLazyLoad() {
        // Arrange: 创建被引用对象（在单独的 Workspace 中）
        var targetObjectId = 100UL;
        var refWorkspace = new Ws(targetObjectId, null);  // nextObjectId = 100
        var referencedDict = refWorkspace.CreateDict();  // ObjectId = 100
        referencedDict.ObjectId.Should().Be(targetObjectId);

        // 设置 ObjectLoader
        AteliaResult<DurableObjectBase> loader(ulong id) =>
            id == targetObjectId
                ? AteliaResult<DurableObjectBase>.Success(referencedDict)
                : AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));

        var workspace = new Ws(loader);

        // committed 中存储 ObjectId（模拟 Materialize 后的状态）
        var committed = new Dictionary<ulong, object?> { { 1, new ObjectId(targetObjectId) } };
        var dictWithRef = new DurableDict(workspace, 50, committed);

        // Act
        var found = dictWithRef.TryGetValue(1, out var value);

        // Assert
        found.Should().BeTrue();
        value.Should().BeSameAs(referencedDict);  // 返回的是实例，不是 ObjectId
        value.Should().BeOfType<DurableDict>();
    }

    /// <summary>
    /// 索引器遇到 ObjectId 时执行透明 Lazy Load。
    /// </summary>
    /// <remarks>
    /// 对应条款：<c>[A-OBJREF-TRANSPARENT-LAZY-LOAD]</c>
    /// </remarks>
    [Fact]
    public void Indexer_ObjectId_TriggersLazyLoad() {
        // Arrange
        var targetObjectId = 200UL;
        var refWorkspace = new Ws(targetObjectId, null);
        var referencedDict = refWorkspace.CreateDict();
        referencedDict.ObjectId.Should().Be(targetObjectId);

        AteliaResult<DurableObjectBase> loader(ulong id) =>
            id == targetObjectId
                ? AteliaResult<DurableObjectBase>.Success(referencedDict)
                : AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));

        var workspace = new Ws(loader);
        var committed = new Dictionary<ulong, object?> { { 1, new ObjectId(targetObjectId) } };
        var dict = new DurableDict(workspace, 50, committed);

        // Act
        var value = dict[1];

        // Assert
        value.Should().BeSameAs(referencedDict);
    }

    /// <summary>
    /// Entries 枚举遇到 ObjectId 时执行透明 Lazy Load。
    /// </summary>
    /// <remarks>
    /// 对应条款：<c>[A-OBJREF-TRANSPARENT-LAZY-LOAD]</c>
    /// </remarks>
    [Fact]
    public void Entries_ObjectId_TriggersLazyLoad() {
        // Arrange
        var targetObjectId = 300UL;
        var refWorkspace = new Ws(targetObjectId, null);
        var referencedDict = refWorkspace.CreateDict();
        referencedDict.ObjectId.Should().Be(targetObjectId);

        AteliaResult<DurableObjectBase> loader(ulong id) =>
            id == targetObjectId
                ? AteliaResult<DurableObjectBase>.Success(referencedDict)
                : AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));

        var workspace = new Ws(loader);
        var committed = new Dictionary<ulong, object?> {
            { 1, "plain value" },
            { 2, new ObjectId(targetObjectId) }
        };
        var dict = new DurableDict(workspace, 50, committed);

        // Act
        var entries = dict.Entries.ToList();

        // Assert
        entries.Should().HaveCount(2);
        entries[0].Value.Should().Be("plain value");
        entries[1].Value.Should().BeSameAs(referencedDict);
    }

    /// <summary>
    /// Lazy Load 成功后回填到 _current。
    /// </summary>
    /// <remarks>
    /// 对应条款：<c>[A-OBJREF-BACKFILL-CURRENT]</c>
    /// </remarks>
    [Fact]
    public void LazyLoad_BackfillsToCurrent() {
        // Arrange
        var targetObjectId = 400UL;
        var loadCount = 0;
        var refWorkspace = new Ws(targetObjectId, null);
        var referencedDict = refWorkspace.CreateDict();
        referencedDict.ObjectId.Should().Be(targetObjectId);

        AteliaResult<DurableObjectBase> loader(ulong id) {
            loadCount++;
            return id == targetObjectId
                ? AteliaResult<DurableObjectBase>.Success(referencedDict)
                : AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));
        }

        var workspace = new Ws(loader);
        var committed = new Dictionary<ulong, object?> { { 1, new ObjectId(targetObjectId) } };
        var dict = new DurableDict(workspace, 50, committed);

        // Act: 第一次读取
        var value1 = dict[1];
        // Act: 第二次读取
        var value2 = dict[1];

        // Assert
        value1.Should().BeSameAs(referencedDict);
        value2.Should().BeSameAs(referencedDict);
        loadCount.Should().Be(1, "因为回填后第二次读取不再触发 LoadObject");
    }

    /// <summary>
    /// Lazy Load 回填不改变 dirty 状态。
    /// </summary>
    /// <remarks>
    /// 对应条款：<c>[A-OBJREF-BACKFILL-CURRENT]</c>
    /// </remarks>
    [Fact]
    public void LazyLoad_Backfill_DoesNotChangeDirtyState() {
        // Arrange
        var targetObjectId = 500UL;
        var refWorkspace = new Ws(targetObjectId, null);
        var referencedDict = refWorkspace.CreateDict();
        referencedDict.ObjectId.Should().Be(targetObjectId);

        AteliaResult<DurableObjectBase> loader(ulong id) =>
            id == targetObjectId
                ? AteliaResult<DurableObjectBase>.Success(referencedDict)
                : AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));

        var workspace = new Ws(loader);
        var committed = new Dictionary<ulong, object?> { { 1, new ObjectId(targetObjectId) } };
        var dict = new DurableDict(workspace, 50, committed);

        // 初始状态应该是 Clean
        dict.State.Should().Be(DurableObjectState.Clean);
        dict.HasChanges.Should().BeFalse();

        // Act: 读取触发 Lazy Load
        _ = dict[1];

        // Assert: 仍然是 Clean，没有 dirty
        dict.State.Should().Be(DurableObjectState.Clean);
        dict.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// Lazy Load 失败时抛出 InvalidOperationException。
    /// </summary>
    [Fact]
    public void LazyLoad_Failure_ThrowsInvalidOperationException() {
        // Arrange
        var targetObjectId = 600UL;

        // Loader 总是返回失败
        AteliaResult<DurableObjectBase> loader(ulong id) =>
            AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));

        var workspace = new Ws(loader);
        var committed = new Dictionary<ulong, object?> { { 1, new ObjectId(targetObjectId) } };
        var dict = new DurableDict(workspace, 50, committed);

        // Act
        Action act = () => { _ = dict[1]; };

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{targetObjectId}*");
    }

    /// <summary>
    /// 非 ObjectId 值不触发 Lazy Load。
    /// </summary>
    [Fact]
    public void TryGetValue_NonObjectId_NoLazyLoad() {
        // Arrange
        var loadCount = 0;

        AteliaResult<DurableObjectBase> loader(ulong id) {
            loadCount++;
            return AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));
        }

        var workspace = new Ws(loader);
        var committed = new Dictionary<ulong, object?> {
            { 1, "string value" },
            { 2, 42L },
            { 3, null }
        };
        var dict = new DurableDict(workspace, 50, committed);

        // Act
        dict.TryGetValue(1, out var v1);
        dict.TryGetValue(2, out var v2);
        dict.TryGetValue(3, out var v3);

        // Assert
        v1.Should().Be("string value");
        v2.Should().Be(42L);
        v3.Should().BeNull();
        loadCount.Should().Be(0, "非 ObjectId 值不触发 LoadObject");
    }

    /// <summary>
    /// ObjRef 等价判定：ObjectId 和实例在 dirty 判定时是等价的。
    /// </summary>
    /// <remarks>
    /// 确保 Lazy Load 回填后，即使 _committed 存的是 ObjectId，
    /// _current 存的是实例，UpdateDirtyKey 也能正确判断等价。
    /// </remarks>
    [Fact]
    public void ObjRefEquality_ObjectIdAndInstance_AreEqual() {
        // Arrange
        var targetObjectId = 700UL;
        var refWorkspace = new Ws(targetObjectId, null);
        var referencedDict = refWorkspace.CreateDict();
        referencedDict.ObjectId.Should().Be(targetObjectId);

        AteliaResult<DurableObjectBase> loader(ulong id) =>
            id == targetObjectId
                ? AteliaResult<DurableObjectBase>.Success(referencedDict)
                : AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));

        var workspace = new Ws(loader);
        var committed = new Dictionary<ulong, object?> { { 1, new ObjectId(targetObjectId) } };
        var dict = new DurableDict(workspace, 50, committed);

        // Act: 读取触发 Lazy Load，此时 _current[1] = referencedDict, _committed[1] = ObjectId(700)
        _ = dict[1];

        // Assert: 仍然不是 dirty（ObjectId 和实例语义等价）
        dict.HasChanges.Should().BeFalse();

        // Act: 用同一个实例重新 Set
        dict.Set(1, referencedDict);

        // Assert: 仍然不是 dirty
        dict.HasChanges.Should().BeFalse();
    }
}
