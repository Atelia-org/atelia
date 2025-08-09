using System.Threading;
using System.Threading.Tasks;

namespace MemoTree.Core.Validation
{
    /// <summary>
    /// 配置验证器接口
    /// 确保配置值不超过系统硬限制
    /// </summary>
    public interface IConfigurationValidator
    {
        /// <summary>
        /// 验证配置对象
        /// </summary>
        /// <typeparam name="T">配置类型</typeparam>
        /// <param name="configuration">要验证的配置对象</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateConfigurationAsync<T>(T configuration, CancellationToken cancellationToken = default) where T : class;

        /// <summary>
        /// 验证配置值
        /// </summary>
        /// <param name="configurationKey">配置键</param>
        /// <param name="value">配置值</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateConfigurationValueAsync(string configurationKey, object? value, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证整数配置值
        /// </summary>
        /// <param name="configurationKey">配置键</param>
        /// <param name="value">配置值</param>
        /// <param name="minValue">最小值</param>
        /// <param name="maxValue">最大值</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateIntegerValue(string configurationKey, int value, int minValue, int maxValue);

        /// <summary>
        /// 验证长整数配置值
        /// </summary>
        /// <param name="configurationKey">配置键</param>
        /// <param name="value">配置值</param>
        /// <param name="minValue">最小值</param>
        /// <param name="maxValue">最大值</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateLongValue(string configurationKey, long value, long minValue, long maxValue);

        /// <summary>
        /// 验证字符串配置值
        /// </summary>
        /// <param name="configurationKey">配置键</param>
        /// <param name="value">配置值</param>
        /// <param name="maxLength">最大长度</param>
        /// <param name="allowEmpty">是否允许空值</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateStringValue(string configurationKey, string? value, int maxLength, bool allowEmpty = true);

        /// <summary>
        /// 验证路径配置值
        /// </summary>
        /// <param name="configurationKey">配置键</param>
        /// <param name="path">路径值</param>
        /// <param name="mustExist">路径是否必须存在</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidatePathValue(string configurationKey, string? path, bool mustExist = false);

        /// <summary>
        /// 验证URL配置值
        /// </summary>
        /// <param name="configurationKey">配置键</param>
        /// <param name="url">URL值</param>
        /// <param name="allowedSchemes">允许的协议</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateUrlValue(string configurationKey, string? url, string[]? allowedSchemes = null);

        /// <summary>
        /// 验证枚举配置值
        /// </summary>
        /// <typeparam name="TEnum">枚举类型</typeparam>
        /// <param name="configurationKey">配置键</param>
        /// <param name="value">枚举值</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateEnumValue<TEnum>(string configurationKey, TEnum value) where TEnum : struct, Enum;

        /// <summary>
        /// 验证布尔配置值
        /// </summary>
        /// <param name="configurationKey">配置键</param>
        /// <param name="value">布尔值</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateBooleanValue(string configurationKey, bool value);

        /// <summary>
        /// 验证时间间隔配置值
        /// </summary>
        /// <param name="configurationKey">配置键</param>
        /// <param name="value">时间间隔值</param>
        /// <param name="minValue">最小值</param>
        /// <param name="maxValue">最大值</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateTimeSpanValue(string configurationKey, TimeSpan value, TimeSpan minValue, TimeSpan maxValue);
    }

    /// <summary>
    /// 泛型配置验证器接口
    /// 提供类型安全的配置验证
    /// </summary>
    /// <typeparam name="T">配置类型</typeparam>
    public interface IConfigurationValidator<T> where T : class
    {
        /// <summary>
        /// 验证配置对象
        /// </summary>
        /// <param name="configuration">要验证的配置对象</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateAsync(T configuration, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证配置对象（同步版本）
        /// </summary>
        /// <param name="configuration">要验证的配置对象</param>
        /// <returns>验证结果</returns>
        ValidationResult Validate(T configuration);
    }

    /// <summary>
    /// MemoTree专用配置验证器接口
    /// 验证各配置模块
    /// </summary>
    public interface IMemoTreeConfigurationValidator
    {
        /// <summary>
        /// 验证主配置
        /// </summary>
        /// <param name="options">主配置选项</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateMemoTreeOptionsAsync(object options, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证存储配置
        /// </summary>
        /// <param name="options">存储配置选项</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateStorageOptionsAsync(object options, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证关系配置
        /// </summary>
        /// <param name="options">关系配置选项</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateRelationOptionsAsync(object options, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证检索配置
        /// </summary>
        /// <param name="options">检索配置选项</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateRetrievalOptionsAsync(object options, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证视图配置
        /// </summary>
        /// <param name="options">视图配置选项</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateViewOptionsAsync(object options, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证所有配置模块
        /// </summary>
        /// <param name="allOptions">所有配置选项</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateAllOptionsAsync(object allOptions, CancellationToken cancellationToken = default);
    }
}
