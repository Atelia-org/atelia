using System.Collections.Generic;

namespace MemoTree.Core.Configuration {
    /// <summary>
    /// MemoTree系统配置选项
    /// 对应MVP设计草稿中定义的Workspace结构
    /// </summary>
    public class MemoTreeOptions {
        /// <summary>
        /// 工作空间根目录路径
        /// </summary>
        public string WorkspaceRoot { get; set; } = "./workspace";

        /// <summary>
        /// 认知节点存储目录名 (对应MVP设计中的CogNodes/)
        /// </summary>
        public string CogNodesDirectory { get; set; } = "CogNodes";

        /// <summary>
        /// 父子关系存储目录名 (对应MVP设计中的Hierarchy/)
        /// </summary>
        public string HierarchyDirectory { get; set; } = "Hierarchy";

        /// <summary>
        /// 语义关系数据存储目录名 (对应MVP设计中的Relations/)
        /// </summary>
        public string RelationsDirectory { get; set; } = "Relations";

        /// <summary>
        /// 单个认知节点的默认最大上下文Token数
        /// 用于限制单个CogNode内容的Token数量，不能超过SystemLimits.DefaultMaxContextTokens
        /// </summary>
        public int DefaultMaxContextTokens { get; set; } = 8000;

        /// <summary>
        /// 整个MemoTree视图的最大Token数
        /// 用于限制整个视图展开后的总Token数量，范围应在128K-200K之间
        /// </summary>
        public int MaxMemoTreeViewTokens { get; set; } = 150_000;

        /// <summary>
        /// 自动保存间隔（分钟）
        /// </summary>
        public int AutoSaveIntervalMinutes { get; set; } = 5;

        /// <summary>
        /// 是否启用Git版本控制
        /// </summary>
        public bool EnableVersionControl { get; set; } = true;

        /// <summary>
        /// 是否启用Roslyn集成 (Phase 4功能，当前阶段默认关闭)
        /// </summary>
        public bool EnableRoslynIntegration { get; set; } = false;

        /// <summary>
        /// MVP模式：使用Fast Fail异常处理策略
        /// true: 所有异常直接向上传播，保持故障现场完整性
        /// false: 使用完整的异常处理和恢复机制 (Phase 5功能)
        /// </summary>
        public bool UseMvpFastFailMode { get; set; } = true;

        /// <summary>
        /// 支持的文件扩展名
        /// </summary>
        public IList<string> SupportedFileExtensions {
            get; set;
        } = new List<string> {
            ".md", ".txt", ".cs", ".json", ".yaml", ".yml"
        };
    }
}
