using System;
using System.Collections.Generic;
using System.Linq;

namespace MemoTree.Core.Types
{
    /// <summary>
    /// 认知节点元数据（父子关系和语义关系数据已分离）
    /// </summary>
    public record NodeMetadata
    {
        public NodeId Id { get; init; }
        public NodeType Type { get; init; }
        public string Title { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public DateTime LastModified { get; init; } = DateTime.UtcNow;
        public List<string> Tags { get; init; } = new List<string>();
        public Dictionary<LodLevel, string> ContentHashes { get; init; } =
            new Dictionary<LodLevel, string>();
        public bool IsDirty { get; init; } = false;

        /// <summary>
        /// 自定义属性字典
        ///
        /// MVP阶段类型约定：
        /// - 支持基本类型：string, int, long, double, bool, DateTime
        /// - 支持集合类型：string[], List&lt;string&gt;
        /// - 避免复杂对象，优先使用JSON字符串存储
        /// - 所有访问都应使用 CustomPropertiesExtensions 提供的安全方法
        ///
        /// 长期规划：Phase 5将升级为 IReadOnlyDictionary&lt;string, JsonElement&gt; 提供完整类型安全
        /// </summary>
        public Dictionary<string, object> CustomProperties { get; init; } =
            new Dictionary<string, object>();

        /// <summary>
        /// 创建新的节点元数据
        /// </summary>
        public static NodeMetadata Create(NodeType type, string title, IEnumerable<string>? tags = null)
        {
            return new NodeMetadata
            {
                Id = NodeId.Generate(),
                Type = type,
                Title = title ?? string.Empty,
                CreatedAt = DateTime.UtcNow,
                LastModified = DateTime.UtcNow,
                Tags = tags?.ToList() ?? new List<string>(),
                IsDirty = true
            };
        }

        /// <summary>
        /// 更新标题
        /// </summary>
        public NodeMetadata WithTitle(string newTitle)
        {
            return this with
            {
                Title = newTitle ?? string.Empty,
                LastModified = DateTime.UtcNow,
                IsDirty = true
            };
        }

        /// <summary>
        /// 更新标签
        /// </summary>
        public NodeMetadata WithTags(IEnumerable<string> newTags)
        {
            return this with
            {
                Tags = newTags?.ToList() ?? new List<string>(),
                LastModified = DateTime.UtcNow,
                IsDirty = true
            };
        }

        /// <summary>
        /// 添加标签
        /// </summary>
        public NodeMetadata AddTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag) || Tags.Contains(tag))
                return this;

            var newTags = Tags.ToList();
            newTags.Add(tag);

            return this with
            {
                Tags = newTags,
                LastModified = DateTime.UtcNow,
                IsDirty = true
            };
        }

        /// <summary>
        /// 移除标签
        /// </summary>
        public NodeMetadata RemoveTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag) || !Tags.Contains(tag))
                return this;

            var newTags = Tags.Where(t => t != tag).ToList();

            return this with
            {
                Tags = newTags,
                LastModified = DateTime.UtcNow,
                IsDirty = true
            };
        }

        /// <summary>
        /// 更新内容哈希
        /// </summary>
        public NodeMetadata WithContentHash(LodLevel level, string hash)
        {
            var newHashes = new Dictionary<LodLevel, string>(ContentHashes)
            {
                [level] = hash ?? string.Empty
            };

            return this with
            {
                ContentHashes = newHashes,
                LastModified = DateTime.UtcNow,
                IsDirty = true
            };
        }

        /// <summary>
        /// 更新自定义属性
        /// </summary>
        public NodeMetadata WithCustomProperties(Dictionary<string, object> newProperties)
        {
            return this with
            {
                CustomProperties = newProperties ?? new Dictionary<string, object>(),
                LastModified = DateTime.UtcNow,
                IsDirty = true
            };
        }

        /// <summary>
        /// 设置自定义属性
        /// </summary>
        public NodeMetadata SetCustomProperty(string key, object value)
        {
            if (string.IsNullOrWhiteSpace(key))
                return this;

            var newProperties = new Dictionary<string, object>(CustomProperties)
            {
                [key] = value
            };

            return this with
            {
                CustomProperties = newProperties,
                LastModified = DateTime.UtcNow,
                IsDirty = true
            };
        }

        /// <summary>
        /// 移除自定义属性
        /// </summary>
        public NodeMetadata RemoveCustomProperty(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || !CustomProperties.ContainsKey(key))
                return this;

            var newProperties = new Dictionary<string, object>(CustomProperties);
            newProperties.Remove(key);

            return this with
            {
                CustomProperties = newProperties,
                LastModified = DateTime.UtcNow,
                IsDirty = true
            };
        }

        /// <summary>
        /// 标记为已保存（清除脏标记）
        /// </summary>
        public NodeMetadata MarkAsSaved()
        {
            return this with { IsDirty = false };
        }

        /// <summary>
        /// 检查是否有指定级别的内容
        /// </summary>
        public bool HasContent(LodLevel level)
        {
            return ContentHashes.ContainsKey(level) && !string.IsNullOrEmpty(ContentHashes[level]);
        }

        /// <summary>
        /// 获取所有可用的LOD级别
        /// </summary>
        public IEnumerable<LodLevel> GetAvailableLevels()
        {
            return ContentHashes.Where(kvp => !string.IsNullOrEmpty(kvp.Value))
                                .Select(kvp => kvp.Key)
                                .OrderBy(level => (int)level);
        }
    }
}
