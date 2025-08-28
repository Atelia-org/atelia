namespace MemoTree.Core.Exceptions {
    /// <summary>
    /// 异常处理策略枚举
    /// MVP阶段：仅支持Rethrow模式(Fast Fail策略)
    /// Phase 5：将支持完整的异常处理策略
    /// </summary>
    public enum ExceptionHandlingStrategy {
        /// <summary>
        /// 重新抛出异常 (MVP阶段默认且唯一策略)
        /// </summary>
        Rethrow,

        /// <summary>
        /// 记录日志并继续 (Phase 5功能)
        /// </summary>
        LogAndContinue,

        /// <summary>
        /// 记录日志并返回默认值 (Phase 5功能)
        /// </summary>
        LogAndReturnDefault,

        /// <summary>
        /// 重试操作
        /// </summary>
        Retry
    }
}
