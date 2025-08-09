using System;
using System.Collections.Generic;

namespace MemoTree.Core.Exceptions
{
    /// <summary>
    /// MemoTree基础异常
    /// 所有MemoTree特定异常的基类
    /// </summary>
    public abstract class MemoTreeException : Exception
    {
        /// <summary>
        /// 异常代码，用于程序化处理
        /// </summary>
        public virtual string ErrorCode => GetType().Name;

        /// <summary>
        /// 异常上下文信息
        /// </summary>
        public Dictionary<string, object?> Context { get; } = new();

        protected MemoTreeException(string message) : base(message) { }
        
        protected MemoTreeException(string message, Exception innerException) 
            : base(message, innerException) { }


    }
}
