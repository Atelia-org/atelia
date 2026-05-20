// DocGraph v0.1 - 文档图构建器
// 参考：api.md §3.1, spec.md §4 文档图构建约束

using System.Diagnostics;
using System.Text.RegularExpressions;
using Atelia.DocGraph.Core.Fix;
using Atelia.DocGraph.Utils;

namespace Atelia.DocGraph.Core;

/// <summary>
/// 文档图构建器接口。
/// </summary>
public interface IDocumentGraphBuilder {
    /// <summary>
    /// 扫描指定目录，构建文档图。
    /// </summary>
    /// <param name="wishDirectories">Wish 目录列表（默认：["wish"]）。</param>
    /// <returns>完整的文档关系图。</returns>
    DocumentGraph Build(IEnumerable<string>? wishDirectories = null);

    /// <summary>
    /// 验证文档关系完整性。
    /// </summary>
    /// <param name="graph">要验证的文档图。</param>
    /// <param name="fixOptions">修复选项（可选）。</param>
    /// <returns>验证结果。</returns>
    ValidationResult Validate(DocumentGraph graph, FixOptions? fixOptions = null);
}

/// <summary>
/// 文档图构建器实现。
/// </summary>
public partial class DocumentGraphBuilder : IDocumentGraphBuilder {
    // v0.2 (Wish Instance Directory): Root Nodes come only from wish/**/wish.md
    // Legacy single-file wishes (wishes/{active,biding,completed}/**/*.md) are no longer supported.
    private static readonly string[] DefaultWishDirectories = ["wish"];

    // Wish 文件名模式：wish-0001.md → W-0001
    // Legacy layout only; v0.2 wish instance layout uses frontmatter.wishId.
    [GeneratedRegex(@"^wish-(\d{4})\.md$", RegexOptions.IgnoreCase)]
    private static partial Regex WishFileNamePattern();

    // v0.2 wish instance directory name: W-0001-slug → W-0001
    [GeneratedRegex(@"^(W-\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex WishInstanceDirectoryPattern();

    private readonly string _workspaceRoot;

    /// <summary>
    /// 创建文档图构建器。
    /// </summary>
    /// <param name="workspaceRoot">工作区根目录绝对路径。</param>
    public DocumentGraphBuilder(string workspaceRoot) {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    /// <inheritdoc/>
    public DocumentGraph Build(IEnumerable<string>? wishDirectories = null) {
        var directories = wishDirectories?.ToArray() ?? DefaultWishDirectories;
        var allNodes = new Dictionary<string, DocumentNode>(StringComparer.Ordinal);
        var pendingPaths = new Queue<string>();

        // 1. 扫描 Wish 目录，收集 Root Nodes
        foreach (var dir in directories) {
            var absoluteDir = Path.GetFullPath(Path.Combine(_workspaceRoot, dir));
            if (!Directory.Exists(absoluteDir)) { continue; }

            // Normalize input directory to workspace-relative path-shape.
            // This prevents callers from bypassing wish root filtering via "./wish" or "wish/".
            var workspaceRelativeDir = PathNormalizer.ToWorkspaceRelative(absoluteDir, _workspaceRoot);
            if (workspaceRelativeDir == null) {
                // Defensive: never scan directories outside the workspace root.
                continue;
            }
            var isWishRootDirectory = workspaceRelativeDir.Equals("wish", StringComparison.OrdinalIgnoreCase);

            var mdFiles = Directory.GetFiles(absoluteDir, "*.md", SearchOption.AllDirectories);
            foreach (var filePath in mdFiles) {
                // 文件过滤：跳过隐藏文件和临时文件
                var fileName = Path.GetFileName(filePath);
                if (!ShouldProcessFile(fileName)) { continue; }

                var relativePath = PathNormalizer.ToWorkspaceRelative(filePath, _workspaceRoot);
                if (relativePath == null) { continue; }

                // v0.2 wish instance layout: only treat wish/<instanceDir>/wish.md as a Wish root.
                // Use path-shape (not only file name) to prevent nested wish.md or stray wish.md from being treated as roots.
                if (isWishRootDirectory
                    && !IsWishRootPath(relativePath)) { continue; }

                // 尝试解析 frontmatter
                if (!FrontmatterParser.TryParseFile(filePath, out var frontmatter, out _) || frontmatter == null) { continue; /* 跳过无 frontmatter 的文件 */ }

                // 创建 Wish 文档节点
                var node = CreateWishNode(relativePath, dir, frontmatter);

                // 过滤掉 status 为 "abandoned" 的 Wish 节点
                if (node.Status?.Equals("abandoned", StringComparison.OrdinalIgnoreCase) == true) { continue; /* 跳过已放弃的 Wish，不加入文档图 */ }

                if (!allNodes.ContainsKey(node.FilePath)) {
                    allNodes[node.FilePath] = node;

                    // 将 produce 目标加入待处理队列
                    // [P1-1修复] 先检查IsWithinWorkspace()，防止越界路径被折叠后误入队
                    foreach (var producePath in node.ProducePaths) {
                        // 先检查越界，再规范化
                        if (!PathNormalizer.IsWithinWorkspace(producePath, _workspaceRoot)) { continue; /* 跳过越界路径，Validate阶段会报告错误 */ }
                        var normalizedPath = PathNormalizer.Normalize(producePath);
                        if (normalizedPath != null && !allNodes.ContainsKey(normalizedPath)) {
                            pendingPaths.Enqueue(normalizedPath);
                        }
                    }
                }
            }
        }

        // 2. 递归追踪 produce 关系，构建闭包
        while (pendingPaths.Count > 0) {
            var path = pendingPaths.Dequeue();
            if (allNodes.ContainsKey(path)) { continue; }

            var absolutePath = Path.Combine(_workspaceRoot, path);
            if (!File.Exists(absolutePath)) {
                // 文件不存在，创建占位节点
                var placeholderNode = CreatePlaceholderNode(path);
                allNodes[path] = placeholderNode;
                continue;
            }

            // 尝试解析 frontmatter
            if (!FrontmatterParser.TryParseFile(absolutePath, out var frontmatter, out _) || frontmatter == null) {
                // 文件存在但无 frontmatter，创建最小节点
                var minimalNode = CreateProductNode(path, new Dictionary<string, object?>());
                allNodes[path] = minimalNode;
                continue;
            }

            // 创建产物文档节点
            var productNode = CreateProductNode(path, frontmatter);
            allNodes[path] = productNode;

            // 如果产物文档也有 produce 关系，继续追踪
            // [P1-1修复] 先检查IsWithinWorkspace()，防止越界路径被折叠后误入队
            foreach (var producePath in productNode.ProducePaths) {
                // 先检查越界，再规范化
                if (!PathNormalizer.IsWithinWorkspace(producePath, _workspaceRoot)) { continue; /* 跳过越界路径，Validate阶段会报告错误 */ }
                var normalizedPath = PathNormalizer.Normalize(producePath);
                if (normalizedPath != null && !allNodes.ContainsKey(normalizedPath)) {
                    pendingPaths.Enqueue(normalizedPath);
                }
            }
        }

        // 3. 建立双向关系
        foreach (var node in allNodes.Values) {
            // 处理 produce 关系
            foreach (var targetPath in node.ProducePaths) {
                // [P1-1修复] 建边阶段同样需要先检查IsWithinWorkspace()，防止越界路径被Normalize后误连到图中已有节点
                if (!PathNormalizer.IsWithinWorkspace(targetPath, _workspaceRoot)) { continue; /* 跳过越界路径，不建立边 */ }
                var normalizedPath = PathNormalizer.Normalize(targetPath);
                if (normalizedPath != null && allNodes.TryGetValue(normalizedPath, out var targetNode)) {
                    node.AddProducesRelation(targetNode);
                    targetNode.AddProducedByRelation(node);
                }
            }
        }

        return new DocumentGraph(allNodes.Values);
    }

    /// <inheritdoc/>
    public ValidationResult Validate(DocumentGraph graph, FixOptions? fixOptions = null) {
        var stopwatch = Stopwatch.StartNew();
        var issues = new List<ValidationIssue>();
        var fixActions = new List<IFixAction>();

        // 1. 验证必填字段
        foreach (var node in graph.AllNodes) {
            ValidateRequiredFields(node, issues);
        }

        // 2. 验证 produce 关系（包括frontmatter存在性和backlink）
        foreach (var node in graph.AllNodes.Where(n => n.ProducePaths.Count > 0)) {
            ValidateProduceRelations(node, graph, issues, fixActions);
        }

        // 3. 验证 produce_by 反向链接（检查声明的源是否真实存在）
        foreach (var node in graph.AllNodes.Where(n => n.Type == DocumentType.Product)) {
            ValidateProducedByDeclarations(node, graph, issues);
        }

        // 注意：不检测循环引用 [设计决策]
        // 原因：核心目标是"找到每一个与根文档集合关联的文档，收集其中的信息"
        // 环不影响这个目标，只要每个文档只被 visit 一次即可（类似计算所得税）

        stopwatch.Stop();

        // 4. 统计信息
        var statistics = new ScanStatistics {
            TotalFiles = graph.AllNodes.Count,
            WishDocuments = graph.RootNodes.Count,
            ProductDocuments = graph.AllNodes.Count - graph.RootNodes.Count,
            TotalRelations = graph.AllNodes.Sum(n => n.Produces.Count),
            ElapsedTime = stopwatch.Elapsed
        };

        // 5. 执行修复（如果启用）
        List<FixResult>? fixResults = null;
        if (fixOptions?.Enabled == true && fixActions.Count > 0) {
            fixResults = ExecuteFixes(fixActions, fixOptions, graph);
        }

        return new ValidationResult(statistics, issues, fixResults);
    }

    #region Helper Methods

    /// <summary>
    /// 检查文件是否应该被处理。
    /// 遵循 [A-DOCGRAPH-001]：文件过滤规则（仅处理 .md 后缀文件，跳过隐藏/临时文件）。
    /// </summary>
    /// <param name="fileName">文件名（不含路径）。</param>
    /// <returns>是否应该处理此文件。</returns>
    private static bool ShouldProcessFile(string fileName) {
        // 1. 跳过隐藏文件（以 . 开头）
        if (fileName.StartsWith('.')) { return false; }

        // 2. 跳过临时文件（以 ~ 结尾，如 vim 备份）
        if (fileName.EndsWith('~')) { return false; }

        // 3. 跳过 Emacs 自动保存文件（包含 #）
        if (fileName.Contains('#')) { return false; }

        return true;
    }

    /// <summary>
    /// 创建 Wish 文档节点。
    /// </summary>
    private DocumentNode CreateWishNode(string filePath, string wishDirectory, Dictionary<string, object?> frontmatter) {
        var fileName = Path.GetFileName(filePath);
        // v0.2: wish instances use wish.md as filename; docId must come from frontmatter.wishId
        var docId = FrontmatterParser.GetString(frontmatter, "wishId")
            ?? DeriveWishDocIdFromWishInstancePath(filePath)
            ?? DeriveWishDocId(fileName);
        // v0.2: wish instances derive status from frontmatter; legacy derives from directory
        var status = DeriveStatus(filePath, wishDirectory, frontmatter);
        var title = FrontmatterParser.GetString(frontmatter, "title") ?? docId;
        var producePaths = FrontmatterParser.GetStringArray(frontmatter, "produce");

        return new DocumentNode(
            filePath,
            docId,
            title,
            status,
            DocumentType.Wish,
            frontmatter,
            producePaths,
            []
        );
    }

    /// <summary>
    /// 创建产物文档节点。
    /// </summary>
    private static DocumentNode CreateProductNode(string filePath, Dictionary<string, object?> frontmatter) {
        var docId = FrontmatterParser.GetString(frontmatter, "docId")
            ?? Path.GetFileNameWithoutExtension(filePath);
        var title = FrontmatterParser.GetString(frontmatter, "title") ?? docId;
        var producePaths = FrontmatterParser.GetStringArray(frontmatter, "produce");
        var producedByPaths = FrontmatterParser.GetStringArray(frontmatter, "produce_by");

        return new DocumentNode(
            filePath,
            docId,
            title,
            null,
            DocumentType.Product,
            frontmatter,
            producePaths,
            producedByPaths
        );
    }

    /// <summary>
    /// 创建占位节点（文件不存在）。
    /// </summary>
    private static DocumentNode CreatePlaceholderNode(string filePath) {
        var docId = Path.GetFileNameWithoutExtension(filePath);
        return new DocumentNode(
            filePath,
            docId,
            $"[缺失] {docId}",
            null,
            DocumentType.Product,
            new Dictionary<string, object?>(),
            [],
            []
        );
    }

    /// <summary>
    /// 从文件名推导 Wish 文档 ID。
    /// 遵循 [A-DOCGRAPH-002]：wish-0001.md → W-0001
    /// </summary>
    private static string DeriveWishDocId(string fileName) {
        var match = WishFileNamePattern().Match(fileName);
        if (match.Success) { return $"W-{match.Groups[1].Value}"; }

        // 非标准文件名，使用文件名 stem
        return Path.GetFileNameWithoutExtension(fileName);
    }

    /// <summary>
    /// v0.2: 从 wish 实例路径推导 WishId（仅作为兜底）。
    /// 规则：wish/W-0001-slug/wish.md → W-0001
    /// </summary>
    private static string? DeriveWishDocIdFromWishInstancePath(string filePath) {
        // filePath is workspace-relative and uses '/' separators.
        var segments = filePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3) { return null; }

        if (!segments[0].Equals("wish", StringComparison.OrdinalIgnoreCase)) { return null; }

        if (!segments[^1].Equals("wish.md", StringComparison.OrdinalIgnoreCase)) { return null; }

        var instanceDirName = segments[^2];
        var match = WishInstanceDirectoryPattern().Match(instanceDirName);
        if (match.Success) { return match.Groups[1].Value.ToUpperInvariant(); }

        return null;
    }

    /// <summary>
    /// v0.2: 判断一个 workspace-relative 路径是否为 Wish Root：wish/&lt;W-XXXX-...&gt;/wish.md
    /// </summary>
    private static bool IsWishRootPath(string workspaceRelativePath) {
        var segments = workspaceRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3) { return false; }

        if (!segments[0].Equals("wish", StringComparison.OrdinalIgnoreCase)) { return false; }

        if (!segments[2].Equals("wish.md", StringComparison.OrdinalIgnoreCase)) { return false; }

        // Instance directory must start with W-XXXX
        return WishInstanceDirectoryPattern().IsMatch(segments[1]);
    }

    /// <summary>
    /// 从文件路径推导状态。
    /// 遵循 [A-DOCGRAPH-002]：active/ → "active", biding/ → "biding", completed/ → "completed"
    /// </summary>
    private static string DeriveStatus(string filePath, string wishDirectory, IReadOnlyDictionary<string, object?> frontmatter) {
        // New layout: ./wish/**/wish.md
        if (wishDirectory.Equals("wish", StringComparison.OrdinalIgnoreCase)
            || wishDirectory.EndsWith("/wish", StringComparison.OrdinalIgnoreCase)
            || filePath.EndsWith("/wish.md", StringComparison.OrdinalIgnoreCase)) {
            var status = FrontmatterParser.GetString(frontmatter, "status");
            if (!string.IsNullOrWhiteSpace(status)) { return status.Trim().ToLowerInvariant(); }
            return "unknown";
        }

        if (wishDirectory.Contains("active", StringComparison.OrdinalIgnoreCase)) { return "active"; }
        if (wishDirectory.Contains("biding", StringComparison.OrdinalIgnoreCase)) { return "biding"; }
        if (wishDirectory.Contains("completed", StringComparison.OrdinalIgnoreCase)) { return "completed"; }

        // 默认返回目录名
        return Path.GetFileName(wishDirectory.TrimEnd('/'));
    }

    /// <summary>
    /// 验证必填字段。
    /// 遵循 [S-FRONTMATTER-005]：
    /// - Wish文档：title, produce
    /// - 产物文档：docId, title, produce_by
    /// </summary>
    private void ValidateRequiredFields(DocumentNode node, List<ValidationIssue> issues) {
        // v0.2: wish/**/wish.md 必须包含 wishId（作为 docId）
        if (node.Type == DocumentType.Wish
            && node.FilePath.StartsWith("wish/", StringComparison.OrdinalIgnoreCase)
            && node.FilePath.EndsWith("/wish.md", StringComparison.OrdinalIgnoreCase)) {
            var wishId = FrontmatterParser.GetString(node.Frontmatter, "wishId");
            if (string.IsNullOrWhiteSpace(wishId)) {
                issues.Add(
                    new ValidationIssue(
                        IssueSeverity.Error,
                        "DOCGRAPH_WISH_ID_MISSING",
                        "Wish 文档缺少 wishId 字段",
                        node.FilePath,
                        $"Wish 文档 {node.FilePath} 缺少 wishId",
                        "请在 frontmatter 中添加 wishId 字段，如：wishId: \"W-0001\""
                    )
                );
            }
        }

        // 检查 title 字段
        // 注意：如果 title 与 docId 相同，说明 frontmatter 中没有显式设置 title
        var hasExplicitTitle = node.Frontmatter.ContainsKey("title")
            && node.Frontmatter["title"] != null
            && !string.IsNullOrWhiteSpace(node.Frontmatter["title"]?.ToString());

        if (!hasExplicitTitle || node.Title.StartsWith("[缺失]")) {
            issues.Add(
                new ValidationIssue(
                    IssueSeverity.Warning,
                    "DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING",
                    "文档缺少 title 字段或文件不存在",
                    node.FilePath,
                    $"文档 {node.DocId} 缺少标题",
                    "请在 frontmatter 中添加 title 字段，或确认文件存在"
                )
            );
        }

        // Wish 文档必须有 produce 关系
        // [设计决策] 降级为 Warning：单文档字段缺失不应阻断其他文档的收集
        if (node.Type == DocumentType.Wish && node.ProducePaths.Count == 0) {
            issues.Add(
                new ValidationIssue(
                    IssueSeverity.Warning,  // 从 Error 降级为 Warning
                    "DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING",
                    "Wish 文档必须包含 produce 字段",
                    node.FilePath,
                    $"Wish 文档 {node.DocId} 缺少 produce 字段",
                    "请在 frontmatter 中添加 produce 字段，如：produce: [\"path/to/api.md\"]"
                )
            );
        }

        // [P1-2修复] 产物文档核心字段验证
        // [设计决策] 降级为 Warning：单文档字段缺失不应阻断其他文档的收集
        if (node.Type == DocumentType.Product && !node.Title.StartsWith("[缺失]")) {
            // 检查 docId 字段存在性和类型
            if (!node.Frontmatter.ContainsKey("docId") || node.Frontmatter["docId"] == null) {
                issues.Add(
                    new ValidationIssue(
                        IssueSeverity.Warning,  // 从 Error 降级为 Warning
                        "DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING",
                        "产物文档缺少 docId 字段",
                        node.FilePath,
                        $"产物文档 {node.FilePath} 缺少 docId 字段",
                        "请在 frontmatter 中添加 docId 字段，用于唯一标识文档"
                    )
                );
            }
            else if (string.IsNullOrWhiteSpace(node.Frontmatter["docId"]?.ToString())) {
                // 空字符串视为字段缺失 [S-FRONTMATTER-006]
                issues.Add(
                    new ValidationIssue(
                        IssueSeverity.Warning,  // 从 Error 降级为 Warning
                        "DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING",
                        "产物文档 docId 字段为空",
                        node.FilePath,
                        $"产物文档 {node.FilePath} 的 docId 字段为空",
                        "请在 frontmatter 中设置非空的 docId 字段值"
                    )
                );
            }

            // 检查 produce_by 字段存在性、类型和元素规则
            if (!node.Frontmatter.ContainsKey("produce_by") || node.Frontmatter["produce_by"] == null) {
                issues.Add(
                    new ValidationIssue(
                        IssueSeverity.Warning,  // 从 Error 降级为 Warning
                        "DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING",
                        "产物文档缺少 produce_by 字段",
                        node.FilePath,
                        $"产物文档 {node.FilePath} 缺少 produce_by 字段",
                        "请在 frontmatter 中添加 produce_by 字段，声明产生此文档的源文档"
                    )
                );
            }
            else {
                // 验证 produce_by 类型必须为序列（数组）[S-FRONTMATTER-006]
                var produceByValue = node.Frontmatter["produce_by"];
                if (produceByValue is not IEnumerable<object> && produceByValue is not IList<object> && produceByValue is string) {
                    // 单字符串不是有效的数组类型
                    issues.Add(
                        new ValidationIssue(
                            IssueSeverity.Warning,  // 从 Error 降级为 Warning
                            "DOCGRAPH_FRONTMATTER_FIELD_TYPE_MISMATCH",
                            "produce_by 字段类型不匹配",
                            node.FilePath,
                            $"produce_by 应为 YAML 序列，实际为字符串",
                            "请将 produce_by 改为数组格式，如：produce_by: [\"path/to/source.md\"]"
                        )
                    );
                }
                else if (node.ProducedByPaths.Count == 0) {
                    // 空数组视为有效值，但元素全空不行
                    // 检查原始值是否为非空数组但所有元素都是空字符串
                    var rawArray = FrontmatterParser.GetStringArray(node.Frontmatter, "produce_by");
                    if (rawArray.Count == 0 && produceByValue is IEnumerable<object> enumerable && enumerable.Any()) {
                        // 有元素但全部被解析为空（可能都是空字符串）
                        issues.Add(
                            new ValidationIssue(
                                IssueSeverity.Warning,
                                "DOCGRAPH_FRONTMATTER_FIELD_VALUE_INVALID",
                                "produce_by 字段元素无效",
                                node.FilePath,
                                $"produce_by 数组中所有元素为空",
                                "请确保 produce_by 数组中至少有一个非空的源文档路径"
                            )
                        );
                    }
                }
            }
        }
    }

    /// <summary>
    /// 验证 produce 关系。
    /// 遵循 [A-DOCGRAPH-005]：
    /// 1. 目标文件必须存在
    /// 2. 目标文件必须有 frontmatter
    /// 3. 目标文件的 produce_by 必须包含源文档路径（backlink验证）
    /// </summary>
    private void ValidateProduceRelations(
        DocumentNode node,
        DocumentGraph graph,
        List<ValidationIssue> issues,
        List<IFixAction> fixActions
    ) {
        foreach (var targetPath in node.ProducePaths) {
            // 先检查路径是否越界（使用原始路径）
            if (!PathNormalizer.IsWithinWorkspace(targetPath, _workspaceRoot)) {
                issues.Add(
                    new ValidationIssue(
                        IssueSeverity.Error,
                        "DOCGRAPH_PATH_OUT_OF_WORKSPACE",
                        $"路径越界: {targetPath}",
                        node.FilePath,
                        "路径试图访问 workspace 外部",
                        "请检查路径是否正确，确保不使用 .. 越界引用"
                    )
                );
                continue;
            }

            var normalizedPath = PathNormalizer.Normalize(targetPath);
            if (normalizedPath == null) {
                issues.Add(
                    new ValidationIssue(
                        IssueSeverity.Error,
                        "DOCGRAPH_PATH_OUT_OF_WORKSPACE",
                        $"无效的路径: {targetPath}",
                        node.FilePath,
                        "路径格式无效或为空",
                        "请检查路径是否正确"
                    )
                );
                continue;
            }

            // 检查目标文件是否存在
            var absolutePath = Path.Combine(_workspaceRoot, normalizedPath);
            if (!File.Exists(absolutePath)) {
                issues.Add(
                    new ValidationIssue(
                        IssueSeverity.Error,
                        "DOCGRAPH_RELATION_DANGLING_LINK",
                        $"悬空引用：目标文件不存在",
                        node.FilePath,
                        $"produce 引用的文件 {normalizedPath} 不存在",
                        $"请创建文件 {normalizedPath}，或使用 --fix 自动创建模板",
                        targetFilePath: normalizedPath
                    )
                );

                // 添加修复动作
                fixActions.Add(new CreateMissingFileAction(normalizedPath, node.FilePath, node.DocId));
                continue;
            }

            // [A-DOCGRAPH-005] 检查目标文件是否有 frontmatter
            if (!graph.TryGetNode(normalizedPath, out var targetNode) || targetNode == null) { continue; /* 节点不在图中（不应发生，但防御性处理） */ }

            // 检查目标是否有有效的 frontmatter（通过检查是否为占位节点或frontmatter是否为空）
            // [设计决策] 降级为 Warning：单文档 frontmatter 缺失不应阻断其他文档的收集
            var hasFrontmatter = targetNode.Frontmatter.Count > 0 && !targetNode.Title.StartsWith("[缺失]");
            if (!hasFrontmatter) {
                issues.Add(
                    new ValidationIssue(
                        IssueSeverity.Warning,  // 从 Error 降级为 Warning
                        "DOCGRAPH_RELATION_DANGLING_LINK",
                        $"目标文件缺少 frontmatter",
                        node.FilePath,
                        $"produce 引用的文件 {normalizedPath} 存在但没有 frontmatter",
                        $"请在文件 {normalizedPath} 顶部添加 YAML frontmatter，至少包含 title 和 produce_by 字段",
                        targetFilePath: normalizedPath
                    )
                );
                continue;
            }

            // [A-DOCGRAPH-005] Backlink 验证：目标的 produce_by 必须包含源文档路径
            var hasBacklink = targetNode.ProducedByPaths
                .Select(p => PathNormalizer.Normalize(p))
                .Any(p => p != null && p.Equals(node.FilePath, StringComparison.Ordinal));

            if (!hasBacklink) {
                issues.Add(
                    new ValidationIssue(
                        IssueSeverity.Warning,
                        "DOCGRAPH_RELATION_MISSING_BACKLINK",
                        $"目标文件缺少 produce_by 反向链接",
                        node.FilePath,
                        $"文件 {normalizedPath} 的 produce_by 未包含 {node.FilePath}",
                        $"请在 {normalizedPath} 的 frontmatter 中添加：produce_by: [\"{node.FilePath}\"]",
                        targetFilePath: normalizedPath
                    )
                );
            }
        }
    }

    /// <summary>
    /// 验证 produce_by 声明的源文档是否存在。
    /// 注意：正向backlink验证（源的produce是否包含目标）已在ValidateProduceRelations中完成。
    /// 此方法验证反向声明的有效性。
    /// </summary>
    private void ValidateProducedByDeclarations(
        DocumentNode node,
        DocumentGraph graph,
        List<ValidationIssue> issues
    ) {
        // 检查 produce_by 中声明的源文档是否存在于图中
        foreach (var sourcePath in node.ProducedByPaths) {
            // [P1-1修复] 先检查路径是否越界
            if (!PathNormalizer.IsWithinWorkspace(sourcePath, _workspaceRoot)) {
                issues.Add(
                    new ValidationIssue(
                        IssueSeverity.Error,
                        "DOCGRAPH_PATH_OUT_OF_WORKSPACE",
                        $"produce_by 路径越界: {sourcePath}",
                        node.FilePath,
                        "produce_by 路径试图访问 workspace 外部",
                        "请检查 produce_by 路径是否正确，确保不使用 .. 越界引用",
                        targetFilePath: sourcePath
                    )
                );
                continue;
            }

            var normalizedPath = PathNormalizer.Normalize(sourcePath);
            if (normalizedPath == null) { continue; }

            if (!graph.ByPath.ContainsKey(normalizedPath)) {
                issues.Add(
                    new ValidationIssue(
                        IssueSeverity.Warning,
                        "DOCGRAPH_RELATION_DANGLING_BACKLINK",
                        $"produce_by 引用的源文件不在文档图中",
                        node.FilePath,
                        $"produce_by 声明的源文档 {normalizedPath} 不存在或不在扫描范围内",
                        "请检查 produce_by 路径是否正确，源文件是否在 wish/ 目录下",
                        targetFilePath: normalizedPath
                    )
                );
            }
        }
    }

    /// <summary>
    /// 执行修复动作。
    /// </summary>
    private List<FixResult> ExecuteFixes(
        List<IFixAction> fixActions,
        FixOptions options,
        DocumentGraph graph
    ) {
        var results = new List<FixResult>();
        var context = new FixContext(graph, options, _workspaceRoot);

        // 过滤可执行的动作
        var executableActions = fixActions.Where(a => a.CanExecute(context)).ToList();

        if (executableActions.Count == 0) { return results; }

        // Dry-run 模式：只预览，不执行
        if (options.DryRun) {
            foreach (var action in executableActions) {
                Console.WriteLine(action.Preview());
                Console.WriteLine();
            }
            return results;
        }

        // 执行修复
        foreach (var action in executableActions) {
            var result = action.Execute(_workspaceRoot);
            results.Add(result);
        }

        return results;
    }

    #endregion
}
