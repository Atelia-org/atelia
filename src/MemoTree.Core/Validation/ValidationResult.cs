using System;
using System.Collections.Generic;
using System.Linq;

namespace MemoTree.Core.Validation
{
    /// <summary>
    /// 验证结果
    /// 包含验证状态、错误信息和警告信息
    /// </summary>
    public record ValidationResult
    {
        /// <summary>
        /// 验证是否成功
        /// </summary>
        public bool IsValid { get; init; }

        /// <summary>
        /// 错误信息列表
        /// </summary>
        public IReadOnlyList<ValidationError> Errors { get; init; } = Array.Empty<ValidationError>();

        /// <summary>
        /// 警告信息列表
        /// </summary>
        public IReadOnlyList<ValidationWarning> Warnings { get; init; } = Array.Empty<ValidationWarning>();

        /// <summary>
        /// 验证的对象类型
        /// </summary>
        public string? ObjectType { get; init; }

        /// <summary>
        /// 验证的对象标识符
        /// </summary>
        public string? ObjectId { get; init; }

        /// <summary>
        /// 是否有警告
        /// </summary>
        public bool HasWarnings => Warnings.Count > 0;

        /// <summary>
        /// 错误数量
        /// </summary>
        public int ErrorCount => Errors.Count;

        /// <summary>
        /// 警告数量
        /// </summary>
        public int WarningCount => Warnings.Count;

        /// <summary>
        /// 创建成功的验证结果
        /// </summary>
        public static ValidationResult Success(string? objectType = null, string? objectId = null)
        {
            return new ValidationResult
            {
                IsValid = true,
                ObjectType = objectType,
                ObjectId = objectId
            };
        }

        /// <summary>
        /// 创建失败的验证结果
        /// </summary>
        public static ValidationResult Failure(ValidationError error, string? objectType = null, string? objectId = null)
        {
            return new ValidationResult
            {
                IsValid = false,
                Errors = new[] { error },
                ObjectType = objectType,
                ObjectId = objectId
            };
        }

        /// <summary>
        /// 创建失败的验证结果（多个错误）
        /// </summary>
        public static ValidationResult Failure(IEnumerable<ValidationError> errors, string? objectType = null, string? objectId = null)
        {
            return new ValidationResult
            {
                IsValid = false,
                Errors = errors.ToList(),
                ObjectType = objectType,
                ObjectId = objectId
            };
        }

        /// <summary>
        /// 创建失败的验证结果（参数形式）
        /// </summary>
        public static ValidationResult Failure(params ValidationError[] errors)
        {
            return new ValidationResult
            {
                IsValid = false,
                Errors = errors
            };
        }

        /// <summary>
        /// 创建带警告的成功验证结果
        /// </summary>
        public static ValidationResult SuccessWithWarnings(IEnumerable<ValidationWarning> warnings, string? objectType = null, string? objectId = null)
        {
            return new ValidationResult
            {
                IsValid = true,
                Warnings = warnings.ToList(),
                ObjectType = objectType,
                ObjectId = objectId
            };
        }

        /// <summary>
        /// 添加错误
        /// </summary>
        public ValidationResult AddError(ValidationError error)
        {
            var newErrors = new List<ValidationError>(Errors) { error };
            return this with
            {
                IsValid = false,
                Errors = newErrors
            };
        }

        /// <summary>
        /// 添加多个错误
        /// </summary>
        public ValidationResult AddErrors(IEnumerable<ValidationError> errors)
        {
            var newErrors = new List<ValidationError>(Errors);
            newErrors.AddRange(errors);
            return this with
            {
                IsValid = false,
                Errors = newErrors
            };
        }

        /// <summary>
        /// 添加警告
        /// </summary>
        public ValidationResult AddWarning(ValidationWarning warning)
        {
            var newWarnings = new List<ValidationWarning>(Warnings) { warning };
            return this with
            {
                Warnings = newWarnings
            };
        }

        /// <summary>
        /// 添加多个警告
        /// </summary>
        public ValidationResult AddWarnings(IEnumerable<ValidationWarning> warnings)
        {
            var newWarnings = new List<ValidationWarning>(Warnings);
            newWarnings.AddRange(warnings);
            return this with
            {
                Warnings = newWarnings
            };
        }

        /// <summary>
        /// 合并验证结果
        /// </summary>
        public ValidationResult Merge(ValidationResult other)
        {
            if (other == null) return this;

            var newErrors = new List<ValidationError>(Errors);
            newErrors.AddRange(other.Errors);

            var newWarnings = new List<ValidationWarning>(Warnings);
            newWarnings.AddRange(other.Warnings);

            return new ValidationResult
            {
                IsValid = IsValid && other.IsValid,
                Errors = newErrors,
                Warnings = newWarnings,
                ObjectType = ObjectType ?? other.ObjectType,
                ObjectId = ObjectId ?? other.ObjectId
            };
        }

        /// <summary>
        /// 获取格式化的错误消息
        /// </summary>
        public string GetErrorMessage()
        {
            if (IsValid) return string.Empty;

            var messages = new List<string>();
            
            if (!string.IsNullOrEmpty(ObjectType) || !string.IsNullOrEmpty(ObjectId))
            {
                var identifier = string.IsNullOrEmpty(ObjectId) ? ObjectType : $"{ObjectType}({ObjectId})";
                messages.Add($"Validation failed for {identifier}:");
            }
            else
            {
                messages.Add("Validation failed:");
            }

            messages.AddRange(Errors.Select(e => $"  - {e}"));

            return string.Join(Environment.NewLine, messages);
        }

        /// <summary>
        /// 获取格式化的警告消息
        /// </summary>
        public string GetWarningMessage()
        {
            if (!HasWarnings) return string.Empty;

            var messages = new List<string>();
            
            if (!string.IsNullOrEmpty(ObjectType) || !string.IsNullOrEmpty(ObjectId))
            {
                var identifier = string.IsNullOrEmpty(ObjectId) ? ObjectType : $"{ObjectType}({ObjectId})";
                messages.Add($"Validation warnings for {identifier}:");
            }
            else
            {
                messages.Add("Validation warnings:");
            }

            messages.AddRange(Warnings.Select(w => $"  - {w}"));

            return string.Join(Environment.NewLine, messages);
        }

        /// <summary>
        /// 获取完整的验证报告
        /// </summary>
        public string GetFullReport()
        {
            var parts = new List<string>();

            if (!IsValid)
                parts.Add(GetErrorMessage());

            if (HasWarnings)
                parts.Add(GetWarningMessage());

            return string.Join(Environment.NewLine + Environment.NewLine, parts);
        }

        /// <summary>
        /// 转换为字符串表示
        /// </summary>
        public override string ToString()
        {
            if (IsValid && !HasWarnings)
                return "Valid";

            if (IsValid && HasWarnings)
                return $"Valid with {WarningCount} warning(s)";

            return $"Invalid with {ErrorCount} error(s)" + 
                   (HasWarnings ? $" and {WarningCount} warning(s)" : "");
        }
    }

    /// <summary>
    /// 验证结果构建器
    /// 用于构建复杂的验证结果
    /// </summary>
    public class ValidationResultBuilder
    {
        private readonly List<ValidationError> _errors = new();
        private readonly List<ValidationWarning> _warnings = new();
        private string? _objectType;
        private string? _objectId;

        /// <summary>
        /// 设置对象类型
        /// </summary>
        public ValidationResultBuilder ForObjectType(string objectType)
        {
            _objectType = objectType;
            return this;
        }

        /// <summary>
        /// 设置对象ID
        /// </summary>
        public ValidationResultBuilder ForObjectId(string objectId)
        {
            _objectId = objectId;
            return this;
        }

        /// <summary>
        /// 添加错误
        /// </summary>
        public ValidationResultBuilder AddError(ValidationError error)
        {
            _errors.Add(error);
            return this;
        }

        /// <summary>
        /// 添加警告
        /// </summary>
        public ValidationResultBuilder AddWarning(ValidationWarning warning)
        {
            _warnings.Add(warning);
            return this;
        }

        /// <summary>
        /// 条件性添加错误
        /// </summary>
        public ValidationResultBuilder AddErrorIf(bool condition, ValidationError error)
        {
            if (condition) _errors.Add(error);
            return this;
        }

        /// <summary>
        /// 条件性添加警告
        /// </summary>
        public ValidationResultBuilder AddWarningIf(bool condition, ValidationWarning warning)
        {
            if (condition) _warnings.Add(warning);
            return this;
        }

        /// <summary>
        /// 构建验证结果
        /// </summary>
        public ValidationResult Build()
        {
            return new ValidationResult
            {
                IsValid = _errors.Count == 0,
                Errors = _errors.ToList(),
                Warnings = _warnings.ToList(),
                ObjectType = _objectType,
                ObjectId = _objectId
            };
        }
    }
}
