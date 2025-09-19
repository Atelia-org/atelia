namespace MemoTree.Core.Exceptions {
    /// <summary>
    /// MemoTree异常扩展方法
    /// 提供类型安全的上下文信息添加功能
    /// </summary>
    public static class MemoTreeExceptionExtensions {
        /// <summary>
        /// 添加上下文信息 (类型安全版本)
        /// </summary>
        /// <typeparam name="T">异常类型，必须继承自MemoTreeException</typeparam>
        /// <param name="exception">异常实例</param>
        /// <param name="key">上下文键</param>
        /// <param name="value">上下文值</param>
        /// <returns>原异常实例，支持链式调用</returns>
        public static T WithContext<T>(this T exception, string key, object? value)
        where T : MemoTreeException {
            exception.Context[key] = value;
            return exception;
        }
    }
}
