using MemoTree.Core.Configuration;
using MemoTree.Core.Services;
using MemoTree.Core.Storage.Interfaces;
using MemoTree.Core.Storage.Hierarchy;
using MemoTree.Core.Storage.Versioned;
using MemoTree.Core.Types;
using MemoTree.Services.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace MemoTree.Services;

/// <summary>
/// 服务注册扩展方法
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 添加MemoTree服务 (MVP版本)
    /// </summary>
    public static IServiceCollection AddMemoTreeServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // 配置选项
        services.Configure<MemoTreeOptions>(options => configuration.GetSection("MemoTree").Bind(options));
        services.Configure<StorageOptions>(options => configuration.GetSection("Storage").Bind(options));

    // 路径管理服务（必须）：供存储与层级使用，并支持链接工作空间
    services.AddSingleton<IWorkspacePathService, WorkspacePathService>();

        // 层次结构存储：使用CowNodeHierarchyStorage实现持久化
        services.AddSingleton<INodeHierarchyStorage>(provider =>
        {
            var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CowNodeHierarchyStorage>>();
            var pathService = provider.GetRequiredService<IWorkspacePathService>();

            // 通过路径服务获取层级关系持久化目录（自动处理链接工作空间）
            var hierarchyDirectory = pathService.GetHierarchyDirectoryAsync().GetAwaiter().GetResult();

            // 创建版本化存储
            var hierarchyStorageTask = VersionedStorageFactory.CreateHierarchyStorageAsync(
                hierarchyDirectory,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<VersionedStorageImpl<NodeId, HierarchyInfo>>>());

            var hierarchyStorage = hierarchyStorageTask.GetAwaiter().GetResult();

            return new CowNodeHierarchyStorage(hierarchyStorage, logger);
        });

        // 节点复合存储：MVP使用 SimpleCognitiveNodeStorage（Content/Metadata基于文件，Hierarchy通过上面的实现）
        services.AddSingleton<ICognitiveNodeStorage, SimpleCognitiveNodeStorage>();

        // 视图状态存储
        services.AddSingleton<IViewStateStorage, FileViewStateStorage>();

        // 业务服务
        services.AddScoped<IMemoTreeService, MemoTreeService>();
        services.AddScoped<IMemoTreeEditor, MemoTreeEditor>();

        return services;
    }

    /// <summary>
    /// 添加MemoTree服务 (使用默认配置)
    /// </summary>
    public static IServiceCollection AddMemoTreeServices(
        this IServiceCollection services,
        string workspaceRoot)
    {
        // 配置选项
        services.Configure<MemoTreeOptions>(options =>
        {
            options.WorkspaceRoot = workspaceRoot;
            options.CogNodesDirectory = "CogNodes";
            options.RelationsDirectory = "Relations";
            // ViewsDirectory 在MVP版本中暂时不需要
            options.DefaultMaxContextTokens = 8000;
            options.MaxMemoTreeViewTokens = 150000;
            options.AutoSaveIntervalMinutes = 5;
            options.EnableVersionControl = true;
        });

        services.Configure<StorageOptions>(options =>
        {
            options.MetadataFileName = "meta.yaml";
            options.GistContentFileName = "gist.md";
            options.SummaryContentFileName = "summary.md";
            options.FullContentFileName = "full.md";
            options.ExternalLinksFileName = "external-links.json";
            options.HierarchyFileExtension = ".yaml";
            options.RelationsFileName = "relations.yaml";
            options.RelationTypesFileName = "relation-types.yaml";
            options.HashAlgorithm = "SHA256";
        });

        // 层次结构存储：使用CowNodeHierarchyStorage实现持久化
        services.AddSingleton<INodeHierarchyStorage>(provider =>
        {
            var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CowNodeHierarchyStorage>>();
            var pathService = provider.GetRequiredService<IWorkspacePathService>();

            // 获取层次关系存储目录
            var hierarchyDirectory = pathService.GetHierarchyDirectoryAsync().GetAwaiter().GetResult();

            // 创建版本化存储
            var hierarchyStorageTask = VersionedStorageFactory.CreateHierarchyStorageAsync(
                hierarchyDirectory,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<VersionedStorageImpl<NodeId, HierarchyInfo>>>());

            var hierarchyStorage = hierarchyStorageTask.GetAwaiter().GetResult();

            return new CowNodeHierarchyStorage(hierarchyStorage, logger);
        });

        // 路径管理服务
        services.AddSingleton<IWorkspacePathService, WorkspacePathService>();

        // 存储服务
        services.AddSingleton<ICognitiveNodeStorage, SimpleCognitiveNodeStorage>();
    services.AddSingleton<IViewStateStorage, MemoTree.Services.Storage.FileViewStateStorage>();

        // 业务服务
        services.AddScoped<IMemoTreeService, MemoTreeService>();
        services.AddScoped<IMemoTreeEditor, MemoTreeEditor>();

        return services;
    }
}
