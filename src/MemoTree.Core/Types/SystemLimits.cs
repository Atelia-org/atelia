namespace MemoTree.Core.Types {
    /// <summary>
    /// 系统硬限制常量类
    /// 定义系统级别的不可配置上限，这些限制不能通过配置修改
    /// </summary>
    public static class SystemLimits {
        /// <summary>
        /// 单个认知节点的最大Token数量
        /// 基于当前LLM模型的上下文窗口限制
        /// </summary>
        public const int MaxTokensPerNode = 8_000;

        /// <summary>
        /// 整个MemoTree视图的最大Token数量
        /// 用于控制整体上下文大小，避免超出LLM处理能力
        /// </summary>
        public const int MaxTokensPerView = 200_000;

        /// <summary>
        /// 最大文件大小（字节）
        /// 单个节点内容文件的最大大小限制
        /// </summary>
        public const long MaxFileSizeBytes = 100 * 1024 * 1024; // 100MB

        /// <summary>
        /// 最大内存使用量（字节）
        /// 系统内存使用的硬限制，用于内存优先架构的保护
        /// </summary>
        public const long MaxMemoryUsageBytes = 8L * 1024 * 1024 * 1024; // 8GB

        /// <summary>
        /// 最大并发操作数
        /// 同时进行的异步操作数量限制
        /// </summary>
        public const int MaxConcurrentOperations = 100;

        /// <summary>
        /// 最大搜索结果数量
        /// 单次搜索返回的最大结果数，避免性能问题
        /// </summary>
        public const int MaxSearchResults = 1_000;

        /// <summary>
        /// 最大批量操作数量
        /// 单次批量操作处理的最大项目数
        /// </summary>
        public const int MaxBatchOperationSize = 100;

        /// <summary>
        /// 最大关系深度
        /// 关系图遍历的最大深度，避免无限循环
        /// </summary>
        public const int MaxRelationDepth = 10;

        /// <summary>
        /// 最大索引大小（字节）
        /// 搜索索引的最大大小限制
        /// </summary>
        public const long MaxIndexSizeBytes = 1L * 1024 * 1024 * 1024; // 1GB

        /// <summary>
        /// 最大缓存项目数量
        /// 内存缓存的最大项目数量
        /// </summary>
        public const int MaxCacheItems = 10_000;

        /// <summary>
        /// 最大事件队列长度
        /// 事件系统队列的最大长度
        /// </summary>
        public const int MaxEventQueueLength = 1_000;

        /// <summary>
        /// 最大重试次数
        /// 操作失败时的最大重试次数
        /// </summary>
        public const int MaxRetryAttempts = 3;

        /// <summary>
        /// 最大超时时间（毫秒）
        /// 操作的最大超时时间
        /// </summary>
        public const int MaxTimeoutMilliseconds = 30_000; // 30秒

        /// <summary>
        /// 数据库相关限制
        /// </summary>
        public static class Database {
            /// <summary>
            /// 最大连接池大小
            /// </summary>
            public const int MaxConnectionPoolSize = 100;

            /// <summary>
            /// 最大查询超时时间（秒）
            /// </summary>
            public const int MaxQueryTimeoutSeconds = 30;

            /// <summary>
            /// 最大事务超时时间（秒）
            /// </summary>
            public const int MaxTransactionTimeoutSeconds = 60;

            /// <summary>
            /// 最大批量插入大小
            /// </summary>
            public const int MaxBulkInsertSize = 1_000;
        }

        /// <summary>
        /// 网络相关限制
        /// </summary>
        public static class Network {
            /// <summary>
            /// 最大HTTP请求大小（字节）
            /// </summary>
            public const long MaxHttpRequestSizeBytes = 50 * 1024 * 1024; // 50MB

            /// <summary>
            /// 最大HTTP响应大小（字节）
            /// </summary>
            public const long MaxHttpResponseSizeBytes = 100 * 1024 * 1024; // 100MB

            /// <summary>
            /// 最大连接超时时间（毫秒）
            /// </summary>
            public const int MaxConnectionTimeoutMilliseconds = 10_000; // 10秒

            /// <summary>
            /// 最大读取超时时间（毫秒）
            /// </summary>
            public const int MaxReadTimeoutMilliseconds = 30_000; // 30秒
        }

        /// <summary>
        /// 安全相关限制
        /// </summary>
        public static class Security {
            /// <summary>
            /// 最大登录尝试次数
            /// </summary>
            public const int MaxLoginAttempts = 5;

            /// <summary>
            /// 最大会话超时时间（分钟）
            /// </summary>
            public const int MaxSessionTimeoutMinutes = 480; // 8小时

            /// <summary>
            /// 最大密码长度
            /// </summary>
            public const int MaxPasswordLength = 128;

            /// <summary>
            /// 最大用户名长度
            /// </summary>
            public const int MaxUsernameLength = 100;
        }

        /// <summary>
        /// 验证系统限制值是否在合理范围内
        /// </summary>
        public static bool IsValidTokenCount(int tokenCount) {
            return tokenCount >= 0 && tokenCount <= MaxTokensPerNode;
        }

        /// <summary>
        /// 验证视图Token数量是否在限制内
        /// </summary>
        public static bool IsValidViewTokenCount(int tokenCount) {
            return tokenCount >= 0 && tokenCount <= MaxTokensPerView;
        }

        /// <summary>
        /// 验证文件大小是否在限制内
        /// </summary>
        public static bool IsValidFileSize(long sizeBytes) {
            return sizeBytes >= 0 && sizeBytes <= MaxFileSizeBytes;
        }

        /// <summary>
        /// 验证内存使用量是否在限制内
        /// </summary>
        public static bool IsValidMemoryUsage(long memoryBytes) {
            return memoryBytes >= 0 && memoryBytes <= MaxMemoryUsageBytes;
        }

        /// <summary>
        /// 验证并发操作数是否在限制内
        /// </summary>
        public static bool IsValidConcurrentOperations(int operationCount) {
            return operationCount >= 0 && operationCount <= MaxConcurrentOperations;
        }

        /// <summary>
        /// 验证搜索结果数量是否在限制内
        /// </summary>
        public static bool IsValidSearchResultCount(int resultCount) {
            return resultCount >= 0 && resultCount <= MaxSearchResults;
        }

        /// <summary>
        /// 验证批量操作大小是否在限制内
        /// </summary>
        public static bool IsValidBatchSize(int batchSize) {
            return batchSize >= 0 && batchSize <= MaxBatchOperationSize;
        }

        /// <summary>
        /// 验证关系深度是否在限制内
        /// </summary>
        public static bool IsValidRelationDepth(int depth) {
            return depth >= 0 && depth <= MaxRelationDepth;
        }
    }
}
