using MemoTree.Core.Validation;

namespace MemoTree.Core.Configuration {
    /// <summary>
    /// 泛型配置验证器接口
    /// </summary>
    /// <typeparam name="T">配置类型</typeparam>
    public interface IConfigurationValidator<T> {
        /// <summary>
        /// 验证配置
        /// </summary>
        /// <param name="configuration">配置实例</param>
        /// <returns>验证结果</returns>
        ValidationResult Validate(T configuration);
    }
}
