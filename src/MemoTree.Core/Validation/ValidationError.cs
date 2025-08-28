using System;

namespace MemoTree.Core.Validation {
    /// <summary>
    /// 验证错误
    /// 表示验证过程中发现的错误信息
    /// </summary>
    public record ValidationError {
        /// <summary>
        /// 错误代码
        /// </summary>
        public string Code { get; init; } = string.Empty;

        /// <summary>
        /// 错误消息
        /// </summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>
        /// 属性名称
        /// </summary>
        public string PropertyName { get; init; } = string.Empty;

        /// <summary>
        /// 尝试设置的值
        /// </summary>
        public object? AttemptedValue {
            get; init;
        }

        /// <summary>
        /// 错误严重级别
        /// </summary>
        public ValidationSeverity Severity { get; init; } = ValidationSeverity.Error;

        /// <summary>
        /// 创建验证错误
        /// </summary>
        public static ValidationError Create(string code, string message, string? propertyName = null, object? attemptedValue = null) {
            return new ValidationError {
                Code = code,
                Message = message,
                PropertyName = propertyName ?? string.Empty,
                AttemptedValue = attemptedValue,
                Severity = ValidationSeverity.Error
            };
        }

        /// <summary>
        /// 创建属性验证错误
        /// </summary>
        public static ValidationError ForProperty(string propertyName, string message, object? attemptedValue = null) {
            return new ValidationError {
                Code = $"PROPERTY_{propertyName.ToUpperInvariant()}_INVALID",
                Message = message,
                PropertyName = propertyName,
                AttemptedValue = attemptedValue,
                Severity = ValidationSeverity.Error
            };
        }

        /// <summary>
        /// 创建长度验证错误
        /// </summary>
        public static ValidationError ForLength(string propertyName, int actualLength, int maxLength) {
            return new ValidationError {
                Code = "LENGTH_EXCEEDED",
                Message = $"{propertyName} length {actualLength} exceeds maximum allowed length {maxLength}",
                PropertyName = propertyName,
                AttemptedValue = actualLength,
                Severity = ValidationSeverity.Error
            };
        }

        /// <summary>
        /// 创建必填字段验证错误
        /// </summary>
        public static ValidationError ForRequired(string propertyName) {
            return new ValidationError {
                Code = "REQUIRED_FIELD_MISSING",
                Message = $"{propertyName} is required",
                PropertyName = propertyName,
                AttemptedValue = null,
                Severity = ValidationSeverity.Error
            };
        }

        /// <summary>
        /// 创建格式验证错误
        /// </summary>
        public static ValidationError ForFormat(string propertyName, string expectedFormat, object? attemptedValue = null) {
            return new ValidationError {
                Code = "INVALID_FORMAT",
                Message = $"{propertyName} has invalid format. Expected: {expectedFormat}",
                PropertyName = propertyName,
                AttemptedValue = attemptedValue,
                Severity = ValidationSeverity.Error
            };
        }

        /// <summary>
        /// 创建范围验证错误
        /// </summary>
        public static ValidationError ForRange(string propertyName, object attemptedValue, object minValue, object maxValue) {
            return new ValidationError {
                Code = "VALUE_OUT_OF_RANGE",
                Message = $"{propertyName} value {attemptedValue} is out of range [{minValue}, {maxValue}]",
                PropertyName = propertyName,
                AttemptedValue = attemptedValue,
                Severity = ValidationSeverity.Error
            };
        }

        /// <summary>
        /// 创建业务规则验证错误
        /// </summary>
        public static ValidationError ForBusinessRule(string ruleName, string message) {
            return new ValidationError {
                Code = $"BUSINESS_RULE_{ruleName.ToUpperInvariant()}",
                Message = message,
                PropertyName = string.Empty,
                AttemptedValue = null,
                Severity = ValidationSeverity.Error
            };
        }

        /// <summary>
        /// 转换为字符串表示
        /// </summary>
        public override string ToString() {
            var parts = new List<string> { $"[{Code}]", Message };

            if (!string.IsNullOrEmpty(PropertyName)) {
                parts.Add($"Property: {PropertyName}");
            }

            if (AttemptedValue != null) {
                parts.Add($"Value: {AttemptedValue}");
            }

            return string.Join(" | ", parts);
        }
    }

    /// <summary>
    /// 验证警告
    /// 表示验证过程中发现的警告信息
    /// </summary>
    public record ValidationWarning {
        /// <summary>
        /// 警告代码
        /// </summary>
        public string Code { get; init; } = string.Empty;

        /// <summary>
        /// 警告消息
        /// </summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>
        /// 属性名称
        /// </summary>
        public string PropertyName { get; init; } = string.Empty;

        /// <summary>
        /// 警告严重级别
        /// </summary>
        public ValidationSeverity Severity { get; init; } = ValidationSeverity.Warning;

        /// <summary>
        /// 创建验证警告
        /// </summary>
        public static ValidationWarning Create(string code, string message, string? propertyName = null) {
            return new ValidationWarning {
                Code = code,
                Message = message,
                PropertyName = propertyName ?? string.Empty,
                Severity = ValidationSeverity.Warning
            };
        }

        /// <summary>
        /// 创建性能警告
        /// </summary>
        public static ValidationWarning ForPerformance(string message) {
            return new ValidationWarning {
                Code = "PERFORMANCE_WARNING",
                Message = message,
                PropertyName = string.Empty,
                Severity = ValidationSeverity.Warning
            };
        }

        /// <summary>
        /// 创建最佳实践警告
        /// </summary>
        public static ValidationWarning ForBestPractice(string message) {
            return new ValidationWarning {
                Code = "BEST_PRACTICE_WARNING",
                Message = message,
                PropertyName = string.Empty,
                Severity = ValidationSeverity.Warning
            };
        }

        /// <summary>
        /// 转换为字符串表示
        /// </summary>
        public override string ToString() {
            var parts = new List<string> { $"[{Code}]", Message };

            if (!string.IsNullOrEmpty(PropertyName)) {
                parts.Add($"Property: {PropertyName}");
            }

            return string.Join(" | ", parts);
        }
    }

    /// <summary>
    /// 验证严重级别
    /// </summary>
    public enum ValidationSeverity {
        /// <summary>
        /// 信息
        /// </summary>
        Info = 0,

        /// <summary>
        /// 警告
        /// </summary>
        Warning = 1,

        /// <summary>
        /// 错误
        /// </summary>
        Error = 2,

        /// <summary>
        /// 严重错误
        /// </summary>
        Critical = 3
    }
}
