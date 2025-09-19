using System;
using System.Collections.Generic;
using System.Linq;

namespace MemoTree.Core.Types {
    /// <summary>
    /// LOD内容记录，包含特定详细级别的内容
    /// </summary>
    public record LodContent {
        /// <summary>
        /// LOD级别
        /// </summary>
        public LodLevel Level {
            get; init;
        }

        /// <summary>
        /// 内容文本（Markdown格式）
        /// </summary>
        public string Content { get; init; } = string.Empty;

        /// <summary>
        /// 内容长度（字符数）
        /// </summary>
        public int Length => Content.Length;

        /// <summary>
        /// 是否为空内容
        /// </summary>
        public bool IsEmpty => string.IsNullOrWhiteSpace(Content);

        /// <summary>
        /// 最后更新时间
        /// </summary>
        public DateTime LastUpdated { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 创建新的LOD内容
        /// </summary>
        public static LodContent Create(LodLevel level, string content) {
            return new LodContent {
                Level = level,
                Content = content ?? string.Empty,
                LastUpdated = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 更新内容
        /// </summary>
        public LodContent WithContent(string newContent) {
            return this with {
                Content = newContent ?? string.Empty,
                LastUpdated = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 获取内容的摘要（前N个字符）
        /// </summary>
        public string GetSummary(int maxLength = 100) {
            if (string.IsNullOrWhiteSpace(Content)) { return string.Empty; }
            if (Content.Length <= maxLength) { return Content; }
            return Content.Substring(0, maxLength) + "...";
        }

        /// <summary>
        /// 验证内容是否符合LOD级别的要求
        /// </summary>
        public bool IsValidForLevel() {
            return Level switch {
                LodLevel.Gist => !string.IsNullOrWhiteSpace(Content) && Content.Length <= NodeConstraints.LodLimits.GistMaxLength,
                LodLevel.Summary => !string.IsNullOrWhiteSpace(Content) && Content.Length <= NodeConstraints.LodLimits.SummaryMaxLength,
                LodLevel.Full => !string.IsNullOrWhiteSpace(Content) && Content.Length <= NodeConstraints.LodLimits.FullMaxLength,
                _ => false
            };
        }
    }

    /// <summary>
    /// LOD内容集合，管理节点的多级内容
    /// </summary>
    public class LodContentCollection {
        private readonly Dictionary<LodLevel, LodContent> _contents = new();

        /// <summary>
        /// 获取指定级别的内容
        /// </summary>
        public LodContent? GetContent(LodLevel level) {
            return _contents.TryGetValue(level, out var content) ? content : null;
        }

        /// <summary>
        /// 设置指定级别的内容
        /// </summary>
        public void SetContent(LodLevel level, string content) {
            _contents[level] = LodContent.Create(level, content);
        }

        /// <summary>
        /// 设置LOD内容
        /// </summary>
        public void SetContent(LodContent content) {
            _contents[content.Level] = content;
        }

        /// <summary>
        /// 移除指定级别的内容
        /// </summary>
        public bool RemoveContent(LodLevel level) {
            return _contents.Remove(level);
        }

        /// <summary>
        /// 检查是否包含指定级别的内容
        /// </summary>
        public bool HasContent(LodLevel level) {
            return _contents.ContainsKey(level) && !_contents[level].IsEmpty;
        }

        /// <summary>
        /// 获取所有可用的LOD级别
        /// </summary>
        public IEnumerable<LodLevel> GetAvailableLevels() {
            return _contents.Keys.Where(level => HasContent(level)).OrderBy(level => (int)level);
        }

        /// <summary>
        /// 获取最高可用的LOD级别
        /// </summary>
        public LodLevel? GetHighestAvailableLevel() {
            var levels = GetAvailableLevels().ToList();
            return levels.Count > 0 ? levels.Max() : null;
        }

        /// <summary>
        /// 获取最低可用的LOD级别
        /// </summary>
        public LodLevel? GetLowestAvailableLevel() {
            var levels = GetAvailableLevels().ToList();
            return levels.Count > 0 ? levels.Min() : null;
        }

        /// <summary>
        /// 清空所有内容
        /// </summary>
        public void Clear() {
            _contents.Clear();
        }

        /// <summary>
        /// 获取内容总数
        /// </summary>
        public int Count => _contents.Count;
    }
}
