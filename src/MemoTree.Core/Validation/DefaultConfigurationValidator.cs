using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MemoTree.Core.Types;

namespace MemoTree.Core.Validation {
    /// <summary>
    /// 默认配置验证器实现
    /// 提供标准的配置验证逻辑
    /// </summary>
    public class DefaultConfigurationValidator : IConfigurationValidator {
        /// <summary>
        /// 验证配置对象
        /// </summary>
        public Task<ValidationResult> ValidateConfigurationAsync<T>(T configuration, CancellationToken cancellationToken = default) where T : class {
            if (configuration == null) {
                return Task.FromResult(
                    ValidationResult.Failure(
                        ValidationError.ForRequired("Configuration")
                )
                );
            }

            var builder = new ValidationResultBuilder()
            .ForObjectType(typeof(T).Name);

            // 使用反射验证配置属性
            var properties = typeof(T).GetProperties();
            foreach (var property in properties) {
                var value = property.GetValue(configuration);
                var propertyResult = ValidateConfigurationValueAsync(property.Name, value, cancellationToken).Result;

                if (!propertyResult.IsValid) {
                    foreach (var error in propertyResult.Errors) {
                        builder.AddError(error);
                    }
                }
            }

            return Task.FromResult(builder.Build());
        }

        /// <summary>
        /// 验证配置值
        /// </summary>
        public Task<ValidationResult> ValidateConfigurationValueAsync(string configurationKey, object? value, CancellationToken cancellationToken = default) {
            var builder = new ValidationResultBuilder()
            .ForObjectType("ConfigurationValue")
            .ForObjectId(configurationKey);

            if (value == null) {
                // 大多数配置值允许为null，这里只是记录
                return Task.FromResult(builder.Build());
            }

            // 根据值类型进行验证
            var result = value switch {
                int intValue => ValidateIntegerValue(configurationKey, intValue, int.MinValue, int.MaxValue),
                long longValue => ValidateLongValue(configurationKey, longValue, long.MinValue, long.MaxValue),
                string stringValue => ValidateStringValue(configurationKey, stringValue, 1000),
                bool boolValue => ValidateBooleanValue(configurationKey, boolValue),
                TimeSpan timeSpanValue => ValidateTimeSpanValue(configurationKey, timeSpanValue, TimeSpan.Zero, TimeSpan.MaxValue),
                _ => ValidationResult.Success()
            };

            return Task.FromResult(result);
        }

        /// <summary>
        /// 验证整数配置值
        /// </summary>
        public ValidationResult ValidateIntegerValue(string configurationKey, int value, int minValue, int maxValue) {
            var builder = new ValidationResultBuilder()
            .ForObjectType("IntegerConfiguration")
            .ForObjectId(configurationKey);

            builder.AddErrorIf(
                value < minValue || value > maxValue,
                ValidationError.ForRange(configurationKey, value, minValue, maxValue)
            );

            // 检查是否超过系统限制
            if (IsTokenRelatedConfiguration(configurationKey)) {
                builder.AddErrorIf(
                    value > SystemLimits.MaxTokensPerNode,
                    ValidationError.Create(
                        "SYSTEM_LIMIT_EXCEEDED",
                        $"{configurationKey} value {value} exceeds system limit {SystemLimits.MaxTokensPerNode}",
                        configurationKey, value
                    )
                );
            }

            return builder.Build();
        }

        /// <summary>
        /// 验证长整数配置值
        /// </summary>
        public ValidationResult ValidateLongValue(string configurationKey, long value, long minValue, long maxValue) {
            var builder = new ValidationResultBuilder()
            .ForObjectType("LongConfiguration")
            .ForObjectId(configurationKey);

            builder.AddErrorIf(
                value < minValue || value > maxValue,
                ValidationError.ForRange(configurationKey, value, minValue, maxValue)
            );

            // 检查是否超过系统限制
            if (IsMemoryRelatedConfiguration(configurationKey)) {
                builder.AddErrorIf(
                    value > SystemLimits.MaxMemoryUsageBytes,
                    ValidationError.Create(
                        "SYSTEM_LIMIT_EXCEEDED",
                        $"{configurationKey} value {value} exceeds system limit {SystemLimits.MaxMemoryUsageBytes}",
                        configurationKey, value
                    )
                );
            }

            if (IsFileSizeRelatedConfiguration(configurationKey)) {
                builder.AddErrorIf(
                    value > SystemLimits.MaxFileSizeBytes,
                    ValidationError.Create(
                        "SYSTEM_LIMIT_EXCEEDED",
                        $"{configurationKey} value {value} exceeds system limit {SystemLimits.MaxFileSizeBytes}",
                        configurationKey, value
                    )
                );
            }

            return builder.Build();
        }

        /// <summary>
        /// 验证字符串配置值
        /// </summary>
        public ValidationResult ValidateStringValue(string configurationKey, string? value, int maxLength, bool allowEmpty = true) {
            var builder = new ValidationResultBuilder()
            .ForObjectType("StringConfiguration")
            .ForObjectId(configurationKey);

            if (!allowEmpty) {
                builder.AddErrorIf(
                    string.IsNullOrWhiteSpace(value),
                    ValidationError.ForRequired(configurationKey)
                );
            }

            if (value != null) {
                builder.AddErrorIf(
                    value.Length > maxLength,
                    ValidationError.ForLength(configurationKey, value.Length, maxLength)
                );
            }

            return builder.Build();
        }

        /// <summary>
        /// 验证路径配置值
        /// </summary>
        public ValidationResult ValidatePathValue(string configurationKey, string? path, bool mustExist = false) {
            var builder = new ValidationResultBuilder()
            .ForObjectType("PathConfiguration")
            .ForObjectId(configurationKey);

            if (string.IsNullOrWhiteSpace(path)) {
                builder.AddError(ValidationError.ForRequired(configurationKey));
                return builder.Build();
            }

            // 验证路径格式
            try {
                var fullPath = Path.GetFullPath(path);

                if (mustExist) {
                    builder.AddErrorIf(
                        !Directory.Exists(fullPath) && !File.Exists(fullPath),
                        ValidationError.Create(
                            "PATH_NOT_EXISTS",
                            $"Path {path} does not exist", configurationKey, path
                        )
                    );
                }
            } catch (Exception ex) {
                builder.AddError(
                    ValidationError.Create(
                        "INVALID_PATH",
                        $"Invalid path format: {ex.Message}", configurationKey, path
                )
                );
            }

            return builder.Build();
        }

        /// <summary>
        /// 验证URL配置值
        /// </summary>
        public ValidationResult ValidateUrlValue(string configurationKey, string? url, string[]? allowedSchemes = null) {
            var builder = new ValidationResultBuilder()
            .ForObjectType("UrlConfiguration")
            .ForObjectId(configurationKey);

            if (string.IsNullOrWhiteSpace(url)) {
                builder.AddError(ValidationError.ForRequired(configurationKey));
                return builder.Build();
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) {
                builder.AddError(ValidationError.ForFormat(configurationKey, "Valid absolute URL", url));
                return builder.Build();
            }

            if (allowedSchemes != null && allowedSchemes.Length > 0) {
                builder.AddErrorIf(
                    !allowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase),
                    ValidationError.Create(
                        "INVALID_SCHEME",
                        $"URL scheme {uri.Scheme} is not allowed. Allowed schemes: {string.Join(", ", allowedSchemes)}",
                        configurationKey, url
                    )
                );
            }

            return builder.Build();
        }

        /// <summary>
        /// 验证枚举配置值
        /// </summary>
        public ValidationResult ValidateEnumValue<TEnum>(string configurationKey, TEnum value) where TEnum : struct, Enum {
            var builder = new ValidationResultBuilder()
            .ForObjectType("EnumConfiguration")
            .ForObjectId(configurationKey);

            builder.AddErrorIf(
                !Enum.IsDefined(typeof(TEnum), value),
                ValidationError.Create(
                    "INVALID_ENUM_VALUE",
                    $"Invalid enum value {value} for type {typeof(TEnum).Name}",
                    configurationKey, value
                )
            );

            return builder.Build();
        }

        /// <summary>
        /// 验证布尔配置值
        /// </summary>
        public ValidationResult ValidateBooleanValue(string configurationKey, bool value) {
            // 布尔值总是有效的
            return ValidationResult.Success("BooleanConfiguration", configurationKey);
        }

        /// <summary>
        /// 验证时间间隔配置值
        /// </summary>
        public ValidationResult ValidateTimeSpanValue(string configurationKey, TimeSpan value, TimeSpan minValue, TimeSpan maxValue) {
            var builder = new ValidationResultBuilder()
            .ForObjectType("TimeSpanConfiguration")
            .ForObjectId(configurationKey);

            builder.AddErrorIf(
                value < minValue || value > maxValue,
                ValidationError.ForRange(configurationKey, value, minValue, maxValue)
            );

            builder.AddErrorIf(
                value.TotalMilliseconds > SystemLimits.MaxTimeoutMilliseconds,
                ValidationError.Create(
                    "SYSTEM_LIMIT_EXCEEDED",
                    $"{configurationKey} timeout {value.TotalMilliseconds}ms exceeds system limit {SystemLimits.MaxTimeoutMilliseconds}ms",
                    configurationKey, value
                )
            );

            return builder.Build();
        }

        /// <summary>
        /// 检查是否为Token相关配置
        /// </summary>
        private static bool IsTokenRelatedConfiguration(string configurationKey) {
            var tokenKeys = new[] { "token", "maxtoken", "tokencount", "tokenlimit" };
            return tokenKeys.Any(key => configurationKey.Contains(key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 检查是否为内存相关配置
        /// </summary>
        private static bool IsMemoryRelatedConfiguration(string configurationKey) {
            var memoryKeys = new[] { "memory", "maxmemory", "memoryusage", "memorylimit", "cachesize" };
            return memoryKeys.Any(key => configurationKey.Contains(key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 检查是否为文件大小相关配置
        /// </summary>
        private static bool IsFileSizeRelatedConfiguration(string configurationKey) {
            var fileSizeKeys = new[] { "filesize", "maxfilesize", "sizelimit", "maxsize" };
            return fileSizeKeys.Any(key => configurationKey.Contains(key, StringComparison.OrdinalIgnoreCase));
        }
    }
}
