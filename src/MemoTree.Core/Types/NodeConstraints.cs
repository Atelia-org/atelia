namespace MemoTree.Core.Types {
    /// <summary>
    /// 节点约束定义
    /// 定义系统中各种数据的硬限制
    /// </summary>
    public static class NodeConstraints {
        /// <summary>
        /// 节点ID最大长度
        /// 基于Base4096-CJK编码(11字符)和Base64编码(22字符)的最大值
        /// </summary>
        public const int MaxNodeIdLength = 50;

        /// <summary>
        /// 关系ID最大长度
        /// 与节点ID使用相同的约束
        /// </summary>
        public const int MaxRelationIdLength = 50;

        /// <summary>
        /// 节点标题最大长度
        /// 适合在UI中显示的合理长度
        /// </summary>
        public const int MaxTitleLength = 200;

        /// <summary>
        /// 节点内容最大长度（字符数）
        /// 1MB的文本内容，适合大多数用例
        /// </summary>
        public const int MaxContentLength = 1_000_000;

        /// <summary>
        /// 最大标签数量
        /// 避免标签过多影响性能和可用性
        /// </summary>
        public const int MaxTagCount = 20;

        /// <summary>
        /// 标签最大长度
        /// 适合标签显示和搜索的合理长度
        /// </summary>
        public const int MaxTagLength = 50;

        /// <summary>
        /// 关系描述最大长度
        /// 用于描述关系的详细信息
        /// </summary>
        public const int MaxRelationDescriptionLength = 500;

        /// <summary>
        /// 自定义属性键名最大长度
        /// </summary>
        public const int MaxCustomPropertyKeyLength = 100;

        /// <summary>
        /// 自定义属性字符串值最大长度
        /// </summary>
        public const int MaxCustomPropertyStringValueLength = 10_000;

        /// <summary>
        /// 最大自定义属性数量
        /// 避免属性过多影响性能
        /// </summary>
        public const int MaxCustomPropertyCount = 50;

        /// <summary>
        /// 最大子节点数量
        /// 避免单个节点的子节点过多影响性能
        /// </summary>
        public const int MaxChildNodeCount = 1000;

        /// <summary>
        /// 最大节点层次深度
        /// 避免过深的层次结构影响性能和可用性
        /// </summary>
        public const int MaxNodeHierarchyDepth = 20;

        /// <summary>
        /// LOD级别内容长度限制
        /// 注意：Title始终显式，不受LOD级别限制，这里只限制正文内容
        /// </summary>
        public static class LodLimits {
            /// <summary>
            /// Gist级别内容最大长度 - 1-2句核心要点
            /// </summary>
            public const int GistMaxLength = 500;

            /// <summary>
            /// Summary级别内容最大长度 - 要点 + 关键细节摘要
            /// </summary>
            public const int SummaryMaxLength = 5_000;

            /// <summary>
            /// Full级别内容最大长度 - 所有正文内容
            /// </summary>
            public const int FullMaxLength = MaxContentLength;
        }

        /// <summary>
        /// 验证节点ID格式和长度
        /// </summary>
        public static bool IsValidNodeId(string nodeId) {
            return !string.IsNullOrWhiteSpace(nodeId) &&
            nodeId.Length <= MaxNodeIdLength &&
            NodeId.IsValidFormat(nodeId);
        }

        /// <summary>
        /// 验证关系ID格式和长度
        /// </summary>
        public static bool IsValidRelationId(string relationId) {
            return !string.IsNullOrWhiteSpace(relationId) &&
            relationId.Length <= MaxRelationIdLength &&
            RelationId.IsValidFormat(relationId);
        }

        /// <summary>
        /// 验证标题长度
        /// </summary>
        public static bool IsValidTitle(string title) {
            return !string.IsNullOrWhiteSpace(title) &&
            title.Length <= MaxTitleLength;
        }

        /// <summary>
        /// 验证内容长度
        /// </summary>
        public static bool IsValidContent(string content) {
            return content != null &&
            content.Length <= MaxContentLength;
        }

        /// <summary>
        /// 验证LOD级别内容长度
        /// 注意：这里验证的是正文内容，Title作为元数据单独验证
        /// </summary>
        public static bool IsValidLodContent(LodLevel level, string content) {
            if (content == null) { return false; }
            return level switch {
                LodLevel.Gist => content.Length <= LodLimits.GistMaxLength,
                LodLevel.Summary => content.Length <= LodLimits.SummaryMaxLength,
                LodLevel.Full => content.Length <= LodLimits.FullMaxLength,
                _ => false
            };
        }

        /// <summary>
        /// 验证标签
        /// </summary>
        public static bool IsValidTag(string tag) {
            return !string.IsNullOrWhiteSpace(tag) &&
            tag.Length <= MaxTagLength;
        }

        /// <summary>
        /// 验证标签集合
        /// </summary>
        public static bool IsValidTagCollection(IEnumerable<string> tags) {
            if (tags == null) { return true; }
            var tagList = tags.ToList();
            return tagList.Count <= MaxTagCount &&
            tagList.All(IsValidTag) &&
            tagList.Distinct().Count() == tagList.Count; // 无重复
        }

        /// <summary>
        /// 验证关系描述
        /// </summary>
        public static bool IsValidRelationDescription(string description) {
            return description != null &&
            description.Length <= MaxRelationDescriptionLength;
        }

        /// <summary>
        /// 验证自定义属性键名
        /// </summary>
        public static bool IsValidCustomPropertyKey(string key) {
            return !string.IsNullOrWhiteSpace(key) &&
            key.Length <= MaxCustomPropertyKeyLength;
        }

        /// <summary>
        /// 验证自定义属性字符串值
        /// </summary>
        public static bool IsValidCustomPropertyStringValue(string value) {
            return value != null &&
            value.Length <= MaxCustomPropertyStringValueLength;
        }

        /// <summary>
        /// 验证自定义属性集合
        /// </summary>
        public static bool IsValidCustomPropertyCollection(IReadOnlyDictionary<string, object> properties) {
            if (properties == null) { return true; }
            return properties.Count <= MaxCustomPropertyCount &&
            properties.Keys.All(IsValidCustomPropertyKey);
        }

        /// <summary>
        /// 验证子节点数量
        /// </summary>
        public static bool IsValidChildNodeCount(int count) {
            return count >= 0 && count <= MaxChildNodeCount;
        }

        /// <summary>
        /// 验证节点层次深度
        /// </summary>
        public static bool IsValidHierarchyDepth(int depth) {
            return depth >= 0 && depth <= MaxNodeHierarchyDepth;
        }
    }
}
