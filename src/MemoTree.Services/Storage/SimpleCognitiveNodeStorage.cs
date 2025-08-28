using MemoTree.Core.Storage.Interfaces;
using MemoTree.Core.Configuration;
using MemoTree.Core.Services;
using MemoTree.Services.Yaml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MemoTree.Services.Storage;

/// <summary>
/// 简化的认知节点存储实现 (MVP版本)
/// 基于文件系统的直接存储，不使用版本化存储
/// </summary>
public partial class SimpleCognitiveNodeStorage : ICognitiveNodeStorage {
    private readonly MemoTreeOptions _options;
    private readonly StorageOptions _storageOptions;
    private readonly IWorkspacePathService _pathService;
    private readonly ILogger<SimpleCognitiveNodeStorage> _logger;
    private readonly ISerializer _yamlSerializer;
    private readonly IDeserializer _yamlDeserializer;
    // Note: JSON options removed as YAML is used for metadata; keep IO consistent
    private readonly INodeHierarchyStorage _hierarchy;

    public SimpleCognitiveNodeStorage(
        IOptions<MemoTreeOptions> options,
        IOptions<StorageOptions> storageOptions,
        IWorkspacePathService pathService,
        INodeHierarchyStorage hierarchy,
        ILogger<SimpleCognitiveNodeStorage> logger
    ) {
        _options = options.Value;
        _storageOptions = storageOptions.Value;
        _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
        _hierarchy = hierarchy;
        _logger = logger;

        _yamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new NodeIdYamlConverter())
        .Build();

        _yamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new NodeIdYamlConverter())
        .Build();

        // no JSON options needed here
    }
}
