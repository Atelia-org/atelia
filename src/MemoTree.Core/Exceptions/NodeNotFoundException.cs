using MemoTree.Core.Types;

namespace MemoTree.Core.Exceptions {
    /// <summary>
    /// 节点未找到异常
    /// 当请求的节点不存在时抛出
    /// </summary>
    public class NodeNotFoundException : MemoTreeException {
        public NodeId NodeId {
            get;
        }
        public override string ErrorCode => "NODE_NOT_FOUND";

        public NodeNotFoundException(NodeId nodeId)
        : base($"Node with ID '{nodeId}' was not found.") {
            NodeId = nodeId;
            MemoTreeExceptionExtensions.WithContext(this, "NodeId", nodeId.Value);
        }

        public NodeNotFoundException(NodeId nodeId, string additionalInfo)
        : base($"Node with ID '{nodeId}' was not found. {additionalInfo}") {
            NodeId = nodeId;
            MemoTreeExceptionExtensions.WithContext(
                MemoTreeExceptionExtensions.WithContext(this, "NodeId", nodeId.Value),
                "AdditionalInfo", additionalInfo
            );
        }
    }
}
