namespace MemoTree.Core.Configuration {
    /// <summary>
    /// 存储配置选项
    /// </summary>
    public class StorageOptions {
        /// <summary>
        /// 元数据文件名
        /// </summary>
        public string MetadataFileName { get; set; } = "meta.yaml";

        /// <summary>
        /// 要点级内容文件名 (对应LodLevel.Gist)
        /// </summary>
        public string GistContentFileName { get; set; } = "gist.md";

        /// <summary>
        /// 摘要级内容文件名 (对应LodLevel.Summary)
        /// </summary>
        public string SummaryContentFileName { get; set; } = "summary.md";

        /// <summary>
        /// 完整级内容文件名 (对应LodLevel.Full)
        /// </summary>
        public string FullContentFileName { get; set; } = "full.md";

        /// <summary>
        /// 外部链接文件名
        /// </summary>
        public string ExternalLinksFileName { get; set; } = "external-links.json";

        /// <summary>
        /// 父子关系文件扩展名
        /// </summary>
        public string HierarchyFileExtension { get; set; } = ".yaml";

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
