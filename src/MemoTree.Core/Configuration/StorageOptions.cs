namespace MemoTree.Core.Configuration
{
    /// <summary>
    /// 存储配置选项
    /// </summary>
    public class StorageOptions
    {
        /// <summary>
        /// 元数据文件名
        /// </summary>
        public string MetadataFileName { get; set; } = "meta.yaml";

        /// <summary>
        /// 详细内容文件名 (对应MVP设计中的detail.md)
        /// </summary>
        public string DetailContentFileName { get; set; } = "detail.md";

        /// <summary>
        /// 摘要内容文件名 (对应MVP设计中的summary.md)
        /// </summary>
        public string SummaryContentFileName { get; set; } = "summary.md";

        /// <summary>
        /// 简介级内容文件名 (对应MVP设计中的brief.md和LodLevel.Brief)
        /// </summary>
        public string BriefContentFileName { get; set; } = "brief.md";

        /// <summary>
        /// 外部链接文件名
        /// </summary>
        public string ExternalLinksFileName { get; set; } = "external-links.json";

        /// <summary>
        /// 父子关系文件扩展名
        /// </summary>
        public string ParentChildrensFileExtension { get; set; } = ".yaml";

        /// <summary>
        /// 语义关系数据文件名
        /// </summary>
        public string RelationsFileName { get; set; } = "relations.yaml";

        /// <summary>
        /// 关系类型定义文件名
        /// </summary>
        public string RelationTypesFileName { get; set; } = "relation-types.yaml";

        /// <summary>
        /// 内容哈希算法
        /// </summary>
        public string HashAlgorithm { get; set; } = "SHA256";
    }
}
