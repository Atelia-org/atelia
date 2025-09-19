using System;
using System.Collections.Generic;
using System.Linq;

namespace MemoTree.Core.Types {
    /// <summary>
    /// 完整的认知节点，包含元数据和所有LOD级别的内容
    /// </summary>
    public record CognitiveNode {
        public NodeMetadata Metadata { get; init; } = new();
        public IReadOnlyDictionary<LodLevel, NodeContent> Contents { get; init; } = new Dictionary<LodLevel, NodeContent>();

        /// <summary>
        /// 节点ID（从元数据获取）
        /// </summary>
        public NodeId Id => Metadata.Id;

        /// <summary>
        /// 节点类型（从元数据获取）
        /// </summary>
        public NodeType Type => Metadata.Type;

        /// <summary>
        /// 节点标题（从元数据获取）
        /// </summary>
        public string Title => Metadata.Title;

        /// <summary>
        /// 创建时间（从元数据获取）
        /// </summary>
        public DateTime CreatedAt => Metadata.CreatedAt;

        /// <summary>
        /// 最后修改时间（从元数据获取）
        /// </summary>
        public DateTime LastModified => Metadata.LastModified;

        /// <summary>
        /// 是否为脏数据（从元数据获取）
        /// </summary>
        public bool IsDirty => Metadata.IsDirty;

        /// <summary>
        /// 创建新的认知节点
        /// </summary>
        public static CognitiveNode Create(NodeType type, string title, IEnumerable<string>? tags = null) {
            var metadata = NodeMetadata.Create(type, title, tags);
            return new CognitiveNode {
                Metadata = metadata,
                Contents = new Dictionary<LodLevel, NodeContent>()
            };
        }

        /// <summary>
        /// 从现有元数据创建认知节点
        /// </summary>
        public static CognitiveNode FromMetadata(NodeMetadata metadata) {
            return new CognitiveNode {
                Metadata = metadata,
                Contents = new Dictionary<LodLevel, NodeContent>()
            };
        }

        /// <summary>
        /// 获取指定LOD级别的内容
        /// </summary>
        public NodeContent? GetContent(LodLevel level) =>
        Contents.TryGetValue(level, out var content) ? content : null;

        /// <summary>
        /// 检查是否有指定LOD级别的内容
        /// </summary>
        public bool HasContent(LodLevel level) => Contents.ContainsKey(level);

        /// <summary>
        /// 获取所有可用的LOD级别
        /// </summary>
        public IEnumerable<LodLevel> AvailableLevels => Contents.Keys.OrderBy(level => (int)level);

        /// <summary>
        /// 设置指定LOD级别的内容
        /// </summary>
        public CognitiveNode SetContent(LodLevel level, string content) {
            var nodeContent = NodeContent.Create(Id, level, content);
            var newContents = new Dictionary<LodLevel, NodeContent>(Contents) {
                [level] = nodeContent
            };

            // 更新元数据中的内容哈希
            var newMetadata = Metadata.WithContentHash(level, nodeContent.ContentHash);

            return this with {
                Metadata = newMetadata,
                Contents = newContents
            };
        }

        /// <summary>
        /// 设置节点内容对象
        /// </summary>
        public CognitiveNode SetContent(NodeContent content) {
            if (content.Id != Id) { throw new ArgumentException($"Content node ID {content.Id} does not match cognitive node ID {Id}"); }
            var newContents = new Dictionary<LodLevel, NodeContent>(Contents) {
                [content.Level] = content
            };

            // 更新元数据中的内容哈希
            var newMetadata = Metadata.WithContentHash(content.Level, content.ContentHash);

            return this with {
                Metadata = newMetadata,
                Contents = newContents
            };
        }

        /// <summary>
        /// 移除指定LOD级别的内容
        /// </summary>
        public CognitiveNode RemoveContent(LodLevel level) {
            if (!HasContent(level)) { return this; }
            var newContents = new Dictionary<LodLevel, NodeContent>(Contents);
            newContents.Remove(level);

            // 移除元数据中的内容哈希
            var newHashes = new Dictionary<LodLevel, string>(Metadata.ContentHashes);
            newHashes.Remove(level);
            var newMetadata = Metadata with {
                ContentHashes = newHashes
            };

            return this with {
                Metadata = newMetadata,
                Contents = newContents
            };
        }

        /// <summary>
        /// 更新元数据
        /// </summary>
        public CognitiveNode WithMetadata(NodeMetadata newMetadata) {
            if (newMetadata.Id != Id) { throw new ArgumentException($"Metadata node ID {newMetadata.Id} does not match cognitive node ID {Id}"); }
            return this with {
                Metadata = newMetadata
            };
        }

        /// <summary>
        /// 更新标题
        /// </summary>
        public CognitiveNode WithTitle(string newTitle) {
            return WithMetadata(Metadata.WithTitle(newTitle));
        }

        /// <summary>
        /// 更新标签
        /// </summary>
        public CognitiveNode WithTags(IEnumerable<string> newTags) {
            return WithMetadata(Metadata.WithTags(newTags));
        }

        /// <summary>
        /// 添加标签
        /// </summary>
        public CognitiveNode AddTag(string tag) {
            return WithMetadata(Metadata.AddTag(tag));
        }

        /// <summary>
        /// 移除标签
        /// </summary>
        public CognitiveNode RemoveTag(string tag) {
            return WithMetadata(Metadata.RemoveTag(tag));
        }

        /// <summary>
        /// 设置自定义属性
        /// </summary>
        public CognitiveNode SetCustomProperty(string key, object value) {
            return WithMetadata(Metadata.SetCustomProperty(key, value));
        }

        /// <summary>
        /// 移除自定义属性
        /// </summary>
        public CognitiveNode RemoveCustomProperty(string key) {
            return WithMetadata(Metadata.RemoveCustomProperty(key));
        }

        /// <summary>
        /// 获取最高可用的LOD级别内容
        /// </summary>
        public NodeContent? GetHighestLevelContent() {
            var highestLevel = AvailableLevels.LastOrDefault();
            return highestLevel != default ? GetContent(highestLevel) : null;
        }

        /// <summary>
        /// 获取最低可用的LOD级别内容
        /// </summary>
        public NodeContent? GetLowestLevelContent() {
            var lowestLevel = AvailableLevels.FirstOrDefault();
            return lowestLevel != default ? GetContent(lowestLevel) : null;
        }

        /// <summary>
        /// 获取指定级别或更低级别的内容
        /// </summary>
        public NodeContent? GetContentAtOrBelow(LodLevel maxLevel) {
            var availableLevel = AvailableLevels
            .Where(level => (int)level <= (int)maxLevel)
            .OrderByDescending(level => (int)level)
            .FirstOrDefault();

            return availableLevel != default ? GetContent(availableLevel) : null;
        }

        /// <summary>
        /// 获取指定级别或更高级别的内容
        /// </summary>
        public NodeContent? GetContentAtOrAbove(LodLevel minLevel) {
            var availableLevel = AvailableLevels
            .Where(level => (int)level >= (int)minLevel)
            .OrderBy(level => (int)level)
            .FirstOrDefault();

            return availableLevel != default ? GetContent(availableLevel) : null;
        }

        /// <summary>
        /// 标记为已保存（清除脏标记）
        /// </summary>
        public CognitiveNode MarkAsSaved() {
            return WithMetadata(Metadata.MarkAsSaved());
        }

        /// <summary>
        /// 验证节点数据的一致性
        /// </summary>
        public bool IsValid() {
            // 检查元数据和内容的一致性
            foreach (var (level, content) in Contents) {
                if (content.Id != Id) { return false; }
                if (content.Level != level) { return false; }
                if (!content.IsValidForLevel()) { return false; }
                if (Metadata.ContentHashes.TryGetValue(level, out var expectedHash) &&
                expectedHash != content.ContentHash) { return false; }
            }

            return true;
        }

        /// <summary>
        /// 获取节点的统计信息
        /// </summary>
        public NodeStatistics GetStatistics() {
            var totalCharacters = Contents.Values.Sum(c => c.Length);
            var totalWords = Contents.Values.Sum(c => c.GetStatistics().WordCount);

            return new NodeStatistics {
                NodeId = Id,
                ContentLevels = AvailableLevels.Count(),
                TotalCharacters = totalCharacters,
                TotalWords = totalWords,
                TagCount = Metadata.Tags.Count,
                CustomPropertyCount = Metadata.CustomProperties.Count,
                CreatedAt = CreatedAt,
                LastModified = LastModified
            };
        }
    }

    /// <summary>
    /// 节点统计信息
    /// </summary>
    public record NodeStatistics {
        public NodeId NodeId {
            get; init;
        }
        public int ContentLevels {
            get; init;
        }
        public int TotalCharacters {
            get; init;
        }
        public int TotalWords {
            get; init;
        }
        public int TagCount {
            get; init;
        }
        public int CustomPropertyCount {
            get; init;
        }
        public DateTime CreatedAt {
            get; init;
        }
        public DateTime LastModified {
            get; init;
        }
    }
}
