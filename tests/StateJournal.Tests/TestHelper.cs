// Test helper utilities for StateJournal tests

using Atelia.StateJournal;
using WorkspaceClass = Atelia.StateJournal.Workspace;

namespace Atelia.StateJournal.Tests;

/// <summary>
/// 测试辅助类：提供创建测试对象的工厂方法。
/// </summary>
internal static class TestHelper {
    /// <summary>
    /// 全局递增的 NextObjectId 基准，用于确保不同 Workspace 创建的对象有不同的 ObjectId。
    /// </summary>
    private static ulong _nextObjectIdBase = 16;

    /// <summary>
    /// 获取下一个唯一的 NextObjectId 基准。
    /// </summary>
    private static ulong GetNextObjectIdBase() {
        return System.Threading.Interlocked.Add(ref _nextObjectIdBase, 1000);
    }

    /// <summary>
    /// 创建一个用于测试的 DurableDict（通过临时 Workspace）。
    /// </summary>
    /// <returns>DurableDict 实例和其所属的 Workspace。</returns>
    public static (DurableDict Dict, WorkspaceClass Workspace) CreateDurableDict() {
        var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        return (dict, workspace);
    }

    /// <summary>
    /// 创建一个用于测试的 DurableDict，使用唯一的 ObjectId 范围（用于需要释放 Workspace 的测试）。
    /// </summary>
    /// <returns>DurableDict 实例和其所属的 Workspace。</returns>
    public static (DurableDict Dict, WorkspaceClass Workspace) CreateDurableDictWithUniqueId() {
        var nextId = GetNextObjectIdBase();
        var workspace = new WorkspaceClass(nextId);
        var dict = workspace.CreateObject<DurableDict>();
        return (dict, workspace);
    }

    /// <summary>
    /// 创建一个仅用于测试的独立 DurableDict。
    /// </summary>
    /// <remarks>
    /// 此方法返回 DurableDict，隐藏了 Workspace 的管理（通过闭包保持活跃）。
    /// 仅在不需要 Workspace 交互的简单测试中使用。
    /// </remarks>
    public static DurableDict CreateStandaloneDict() {
        var (dict, _) = CreateDurableDict();
        return dict;
    }

    /// <summary>
    /// 创建一个带预填充数据的 DurableDict（用于模拟从 committed 状态加载）。
    /// </summary>
    /// <param name="entries">预填充的键值对。</param>
    /// <returns>DurableDict 实例和其所属的 Workspace。</returns>
    public static (DurableDict Dict, WorkspaceClass Workspace) CreateDurableDictWithData(
        params (ulong Key, object? Value)[] entries
    ) {
        var (dict, workspace) = CreateDurableDict();
        foreach (var (key, value) in entries) {
            dict.Set(key, value);
        }
        return (dict, workspace);
    }

    /// <summary>
    /// 创建多个测试用的 DurableDict（共享同一个 Workspace）。
    /// </summary>
    /// <param name="count">要创建的数量。</param>
    /// <returns>DurableDict 列表和其所属的 Workspace。</returns>
    public static (List<DurableDict> Dicts, WorkspaceClass Workspace) CreateMultipleDurableDict(int count) {
        var workspace = new WorkspaceClass();
        var dicts = new List<DurableDict>();
        for (int i = 0; i < count; i++) {
            dicts.Add(workspace.CreateObject<DurableDict>());
        }
        return (dicts, workspace);
    }

    /// <summary>
    /// 创建一个 Clean 状态的 DurableDict（模拟从存储加载）。
    /// </summary>
    /// <param name="entries">已提交的键值对。</param>
    /// <returns>DurableDict 实例和其所属的 Workspace。</returns>
    public static (DurableDict Dict, WorkspaceClass Workspace) CreateCleanDurableDict(
        params (ulong Key, object? Value)[] entries
    ) {
        var (dict, workspace) = CreateDurableDict();
        foreach (var (key, value) in entries) {
            dict.Set(key, value);
        }
        // Commit to make it Clean
        workspace.Commit();
        return (dict, workspace);
    }

    /// <summary>
    /// 创建带 ObjectLoader 的 Workspace 和从 committed 数据创建的 DurableDict。
    /// 用于 Lazy Loading 测试。
    /// </summary>
    /// <param name="loader">对象加载器委托。</param>
    /// <param name="committed">已提交的键值对（可包含 ObjectId 引用）。</param>
    /// <returns>DurableDict 实例和其所属的 Workspace。</returns>
    public static (DurableDict Dict, WorkspaceClass Workspace) CreateDictWithLoaderAndCommitted(
        ObjectLoaderDelegate loader,
        Dictionary<ulong, object?> committed
    ) {
        var workspace = new WorkspaceClass(loader);
        // 使用 internal 构造函数直接创建 Clean 状态的 DurableDict
        var dict = new DurableDict(workspace, 50UL, committed);
        return (dict, workspace);
    }

    /// <summary>
    /// 在指定 Workspace 中创建 DurableDict（用于 Lazy Loading 测试中的被引用对象）。
    /// </summary>
    /// <param name="workspace">对象所属的 Workspace。</param>
    /// <returns>新创建的 DurableDict。</returns>
    public static DurableDict CreateDictInWorkspace(WorkspaceClass workspace) {
        return workspace.CreateObject<DurableDict>();
    }
}
