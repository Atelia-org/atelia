using MemoTree.Core.Validation;

namespace MemoTree.Core.Configuration {
    /// <summary>
    /// 专用的MemoTree配置验证器接口
    /// </summary>
    public interface IMemoTreeConfigurationValidator {
        /// <summary>
        /// 验证MemoTree主配置
        /// </summary>
        /// <param name="options">MemoTree配置选项</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateMemoTreeOptions(MemoTreeOptions options);

        /// <summary>
        /// 验证关系配置
        /// </summary>
        /// <param name="options">关系配置选项</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateRelationOptions(RelationOptions options);

        /// <summary>
        /// 验证Token限制
        /// </summary>
        /// <param name="nodeTokens">单个节点Token数</param>
        /// <param name="viewTokens">整个视图Token数</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateTokenLimits(int nodeTokens, int viewTokens);
    }
}
