namespace MemoTree.Core.Configuration
{
    /// <summary>
    /// 检索配置选项
    /// </summary>
    public class RetrievalOptions
    {
        /// <summary>
        /// 是否启用全文搜索 (基于Lucene.Net)
        /// </summary>
        public bool EnableFullTextSearch { get; set; } = true;

        /// <summary>
        /// 是否启用语义搜索 (基于向量检索)
        /// </summary>
        public bool EnableSemanticSearch { get; set; } = false;

        /// <summary>
        /// 全文搜索索引目录
        /// </summary>
        public string FullTextIndexDirectory { get; set; } = "./indexes/fulltext";

        /// <summary>
        /// 语义搜索向量维度
        /// </summary>
        public int VectorDimension { get; set; } = 768;

        /// <summary>
        /// 语义搜索索引目录
        /// </summary>
        public string SemanticIndexDirectory { get; set; } = "./indexes/semantic";

        /// <summary>
        /// 搜索结果最大数量
        /// </summary>
        public int MaxSearchResults { get; set; } = 50;
    }
}
