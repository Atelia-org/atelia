using System;

namespace MemoTree.Core.Exceptions
{
    /// <summary>
    /// 存储异常
    /// 存储操作失败时抛出的异常
    /// </summary>
    public class StorageException : MemoTreeException
    {
        public override string ErrorCode => "STORAGE_ERROR";

        public StorageException(string message) : base(message) { }
        
        public StorageException(string message, Exception innerException) 
            : base(message, innerException) { }

        /// <summary>
        /// 存储连接异常
        /// </summary>
        public static StorageException ConnectionFailed(string connectionString, Exception innerException)
        {
            var exception = new StorageException("Failed to connect to storage", innerException);
            return MemoTreeExceptionExtensions.WithContext(exception, "ConnectionString", connectionString);
        }

        /// <summary>
        /// 存储操作超时异常
        /// </summary>
        public static StorageException OperationTimeout(string operation, TimeSpan timeout)
        {
            var exception = new StorageException($"Storage operation '{operation}' timed out after {timeout}");
            return MemoTreeExceptionExtensions.WithContext(
                MemoTreeExceptionExtensions.WithContext(exception, "Operation", operation),
                "Timeout", timeout);
        }
    }
}
