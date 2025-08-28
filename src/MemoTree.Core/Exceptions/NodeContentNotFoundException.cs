using MemoTree.Core.Types;

namespace MemoTree.Core.Exceptions {
    /// <summary>
    /// 节点内容未找到异常
    /// 当请求的节点内容在指定LOD级别不存在时抛出
    /// </summary>
    public class NodeContentNotFoundException : MemoTreeException {
        public NodeId NodeId {
            get;
        }
        public LodLevel Level {
            get;
        }
        public override string ErrorCode => "NODE_CONTENT_NOT_FOUND";

        public NodeContentNotFoundException(NodeId nodeId, LodLevel level)
        : base($"Content for node '{nodeId}' at level '{level}' was not found.") {
            NodeId = nodeId;
            Level = level;
            MemoTreeExceptionExtensions.WithContext(
                MemoTreeExceptionExtensions.WithContext(this, "NodeId", nodeId.Value),
                "LodLevel", level.ToString()
            );
        }
    }
}
