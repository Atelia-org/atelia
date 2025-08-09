using Xunit;
using MemoTree.Core.Types;
using MemoTree.Core.Configuration;
using MemoTree.Core.Exceptions;
using MemoTree.Core.Validation;

namespace MemoTree.Tests
{
    /// <summary>
    /// Phase1基础功能测试
    /// 验证Phase1基础设施层的核心功能
    /// </summary>
    public class Phase1BasicFunctionalityTests
    {
        [Fact]
        public void NodeId_Creation_ShouldWork()
        {
            // Arrange & Act
            var nodeId = NodeId.Generate();

            // Assert
            Assert.NotEmpty(nodeId.Value);
            Assert.Equal(11, nodeId.Value.Length); // Base4096-CJK编码长度
        }

        [Fact]
        public void CognitiveNode_Creation_ShouldWork()
        {
            // Arrange & Act
            var node = CognitiveNode.Create(NodeType.Concept, "测试节点");

            // Assert
            Assert.NotNull(node);
            Assert.Equal("测试节点", node.Metadata.Title);
            Assert.Equal(NodeType.Concept, node.Metadata.Type);
        }

        [Fact]
        public void MemoTreeOptions_DefaultValues_ShouldBeValid()
        {
            // Arrange & Act
            var options = new MemoTreeOptions();

            // Assert
            Assert.Equal("./workspace", options.WorkspaceRoot);
            Assert.Equal("CogNodes", options.CogNodesDirectory);
            Assert.Equal(8000, options.DefaultMaxContextTokens);
            Assert.Equal(150_000, options.MaxMemoTreeViewTokens);
            Assert.True(options.EnableVersionControl);
            Assert.False(options.EnableRoslynIntegration);
            Assert.True(options.UseMvpFastFailMode);
        }

        [Fact]
        public void NodeNotFoundException_WithContext_ShouldWork()
        {
            // Arrange
            var nodeId = NodeId.Generate();

            // Act
            var exception = new NodeNotFoundException(nodeId, "测试附加信息");

            // Assert
            Assert.Equal(nodeId, exception.NodeId);
            Assert.Equal("NODE_NOT_FOUND", exception.ErrorCode);
            Assert.Contains(nodeId.Value, exception.Context["NodeId"]?.ToString());
            Assert.Contains("测试附加信息", exception.Context["AdditionalInfo"]?.ToString());
        }

        [Fact]
        public void ValidationResult_Success_ShouldWork()
        {
            // Arrange & Act
            var result = ValidationResult.Success();

            // Assert
            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void ValidationResult_WithErrors_ShouldWork()
        {
            // Arrange
            var error = ValidationError.Create("TEST_ERROR", "测试错误消息");

            // Act
            var result = ValidationResult.Failure(error);

            // Assert
            Assert.False(result.IsValid);
            Assert.Single(result.Errors);
            Assert.Equal("TEST_ERROR", result.Errors[0].Code);
            Assert.Equal("测试错误消息", result.Errors[0].Message);
        }

        [Fact]
        public void GuidEncoder_ToIdString_ShouldReturnConsistentResults()
        {
            // Arrange
            var guid = Guid.NewGuid();

            // Act
            var id1 = GuidEncoder.ToIdString(guid);
            var id2 = GuidEncoder.ToIdString(guid);

            // Assert
            Assert.Equal(id1, id2);
            Assert.Equal(11, id1.Length); // Base4096-CJK编码长度
        }

        [Fact]
        public void NodeConstraints_Values_ShouldBeReasonable()
        {
            // Assert
            Assert.True(NodeConstraints.MaxTitleLength > 0);
            Assert.True(NodeConstraints.MaxContentLength > 0);
            Assert.True(NodeConstraints.MaxChildNodeCount > 0);
            Assert.True(NodeConstraints.MaxNodeHierarchyDepth > 0);
            Assert.True(NodeConstraints.MaxTagCount > 0);
        }

        [Fact]
        public void SystemLimits_Values_ShouldBeReasonable()
        {
            // Assert
            Assert.True(SystemLimits.MaxTokensPerNode > 0);
            Assert.True(SystemLimits.MaxTokensPerView > SystemLimits.MaxTokensPerNode);
            Assert.True(SystemLimits.MaxFileSizeBytes > 0);
            Assert.True(SystemLimits.MaxConcurrentOperations > 0);
        }
    }
}
