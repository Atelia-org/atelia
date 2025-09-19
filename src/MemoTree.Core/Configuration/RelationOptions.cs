namespace MemoTree.Core.Configuration {
    /// <summary>
    /// 关系管理配置选项
    /// 专注于关系处理的行为逻辑，不包含路径信息
    /// 路径信息由MemoTreeOptions统一管理
    /// </summary>
    public class RelationOptions {
        /// <summary>
        /// 是否启用父子关系独立存储
        /// </summary>
        public bool EnableIndependentHierarchyStorage { get; set; } = true;

        /// <summary>
        /// 是否启用语义关系数据集中存储
        /// </summary>
        public bool EnableCentralizedRelationStorage { get; set; } = true;

        /// <summary>
        /// 最大关系深度
        /// </summary>
        public int MaxRelationDepth { get; set; } = 10;

        /// <summary>
        /// 关系图最大节点数
        /// </summary>
        public int MaxRelationGraphNodes { get; set; } = 1000;

        /// <summary>
        /// 是否启用关系验证
        /// </summary>
        public bool EnableRelationValidation { get; set; } = true;

        /// <summary>
        /// 是否自动清理孤立的语义关系
        /// </summary>
        public bool AutoCleanupOrphanedRelations { get; set; } = true;

        /// <summary>
        /// 最大内存中关系数量
        /// </summary>
        public int MaxInMemoryRelations { get; set; } = 10000;

        /// <summary>
        /// 是否启用关系索引优化
        /// </summary>
        public bool EnableRelationIndexing { get; set; } = true;

        /// <summary>
        /// 关系数据批量写入大小
        /// </summary>
        public int RelationBatchWriteSize { get; set; } = 100;
    }
}
