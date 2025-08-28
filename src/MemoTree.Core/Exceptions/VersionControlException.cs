using System;

namespace MemoTree.Core.Exceptions {
    /// <summary>
    /// 版本控制异常
    /// 版本控制操作失败时抛出的异常
    /// </summary>
    public class VersionControlException : MemoTreeException {
        public override string ErrorCode => "VERSION_CONTROL_ERROR";

        public VersionControlException(string message) : base(message) { }

        public VersionControlException(string message, Exception innerException)
        : base(message, innerException) { }

        /// <summary>
        /// 提交冲突异常
        /// </summary>
        public static VersionControlException CommitConflict(string conflictDetails) {
            var exception = new VersionControlException($"Commit conflict detected: {conflictDetails}");
            return MemoTreeExceptionExtensions.WithContext(exception, "ConflictDetails", conflictDetails);
        }

        /// <summary>
        /// 分支不存在异常
        /// </summary>
        public static VersionControlException BranchNotFound(string branchName) {
            var exception = new VersionControlException($"Branch '{branchName}' not found");
            return MemoTreeExceptionExtensions.WithContext(exception, "BranchName", branchName);
        }
    }
}
