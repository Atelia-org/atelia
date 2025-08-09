using System;
using System.Security.Cryptography;
using System.Text;

namespace MemoTree.Core.Types
{
    /// <summary>
    /// 节点内容数据
    /// </summary>
    public record NodeContent
    {
        public NodeId Id { get; init; }
        public LodLevel Level { get; init; }
        public string Content { get; init; } = string.Empty;
        public string ContentHash { get; init; } = string.Empty;
        public DateTime LastModified { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 内容长度（字符数）
        /// </summary>
        public int Length => Content.Length;

        /// <summary>
        /// 是否为空内容
        /// </summary>
        public bool IsEmpty => string.IsNullOrWhiteSpace(Content);

        /// <summary>
        /// 创建新的节点内容
        /// </summary>
        public static NodeContent Create(NodeId nodeId, LodLevel level, string content)
        {
            var contentText = content ?? string.Empty;
            return new NodeContent
            {
                Id = nodeId,
                Level = level,
                Content = contentText,
                ContentHash = ComputeHash(contentText),
                LastModified = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 更新内容
        /// </summary>
        public NodeContent WithContent(string newContent)
        {
            var contentText = newContent ?? string.Empty;
            return this with
            {
                Content = contentText,
                ContentHash = ComputeHash(contentText),
                LastModified = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 验证内容哈希是否匹配
        /// </summary>
        public bool IsHashValid()
        {
            return ContentHash == ComputeHash(Content);
        }

        /// <summary>
        /// 重新计算并更新内容哈希
        /// </summary>
        public NodeContent RefreshHash()
        {
            return this with
            {
                ContentHash = ComputeHash(Content),
                LastModified = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 获取内容的摘要（前N个字符）
        /// </summary>
        public string GetSummary(int maxLength = 100)
        {
            if (string.IsNullOrWhiteSpace(Content))
                return string.Empty;

            if (Content.Length <= maxLength)
                return Content;

            return Content.Substring(0, maxLength) + "...";
        }

        /// <summary>
        /// 验证内容是否符合LOD级别的要求
        /// </summary>
        public bool IsValidForLevel()
        {
            return Level switch
            {
                LodLevel.Gist => !string.IsNullOrWhiteSpace(Content) && Content.Length <= NodeConstraints.LodLimits.GistMaxLength,
                LodLevel.Summary => !string.IsNullOrWhiteSpace(Content) && Content.Length <= NodeConstraints.LodLimits.SummaryMaxLength,
                LodLevel.Full => !string.IsNullOrWhiteSpace(Content) && Content.Length <= NodeConstraints.LodLimits.FullMaxLength,
                _ => false
            };
        }

        /// <summary>
        /// 计算内容的SHA256哈希值
        /// </summary>
        private static string ComputeHash(string content)
        {
            if (string.IsNullOrEmpty(content))
                return string.Empty;

            using var sha256 = SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            var hashBytes = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hashBytes);
        }

        /// <summary>
        /// 比较两个内容是否相同（基于哈希值）
        /// </summary>
        public bool HasSameContent(NodeContent other)
        {
            if (other == null) return false;
            return ContentHash == other.ContentHash && !string.IsNullOrEmpty(ContentHash);
        }

        /// <summary>
        /// 检查内容是否已更改（与给定哈希值比较）
        /// </summary>
        public bool HasChangedSince(string previousHash)
        {
            return ContentHash != previousHash;
        }

        /// <summary>
        /// 获取内容的统计信息
        /// </summary>
        public ContentStatistics GetStatistics()
        {
            if (string.IsNullOrWhiteSpace(Content))
            {
                return new ContentStatistics
                {
                    CharacterCount = 0,
                    WordCount = 0,
                    LineCount = 0,
                    ParagraphCount = 0
                };
            }

            var lines = Content.Split('\n');
            var words = Content.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var paragraphs = Content.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);

            return new ContentStatistics
            {
                CharacterCount = Content.Length,
                WordCount = words.Length,
                LineCount = lines.Length,
                ParagraphCount = paragraphs.Length
            };
        }
    }

    /// <summary>
    /// 内容统计信息
    /// </summary>
    public record ContentStatistics
    {
        public int CharacterCount { get; init; }
        public int WordCount { get; init; }
        public int LineCount { get; init; }
        public int ParagraphCount { get; init; }
    }
}
