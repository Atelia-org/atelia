using System;

namespace MemoTree.Core.Exceptions {
    /// <summary>
    /// 检索异常
    /// 检索操作失败时抛出的异常
    /// </summary>
    public class RetrievalException : MemoTreeException {
        public override string ErrorCode => "RETRIEVAL_ERROR";

        public RetrievalException(string message) : base(message) { }

        public RetrievalException(string message, Exception innerException)
        : base(message, innerException) { }

        /// <summary>
        /// 搜索查询无效异常
        /// </summary>
        public static RetrievalException InvalidQuery(string query, string reason) {
            var exception = new RetrievalException($"Invalid search query: {reason}");
            return MemoTreeExceptionExtensions.WithContext(
                MemoTreeExceptionExtensions.WithContext(exception, "Query", query),
                "Reason", reason
            );
        }

        /// <summary>
        /// 搜索结果过多异常
        /// </summary>
        public static RetrievalException TooManyResults(int resultCount, int maxAllowed) {
            var exception = new RetrievalException($"Search returned {resultCount} results, maximum allowed is {maxAllowed}");
            return MemoTreeExceptionExtensions.WithContext(
                MemoTreeExceptionExtensions.WithContext(exception, "ResultCount", resultCount),
                "MaxAllowed", maxAllowed
            );
        }
    }
}
