using System;
using System.Collections.Generic;
using System.Linq;

namespace MemoTree.Core.Types {
    /// <summary>
    /// NodeMetadata.CustomProperties 的类型安全访问扩展方法
    /// 提供MVP阶段的类型安全访问模式，避免直接类型转换
    /// </summary>
    public static class CustomPropertiesExtensions {
        /// <summary>
        /// 安全获取字符串属性
        /// </summary>
        public static string? GetString(this IReadOnlyDictionary<string, object> properties, string key) {
            return properties.TryGetValue(key, out var value) ? value as string : null;
        }

        /// <summary>
        /// 安全获取字符串属性，带默认值
        /// </summary>
        public static string GetString(this IReadOnlyDictionary<string, object> properties, string key, string defaultValue) {
            return properties.GetString(key) ?? defaultValue;
        }

        /// <summary>
        /// 安全获取整数属性
        /// </summary>
        public static int? GetInt32(this IReadOnlyDictionary<string, object> properties, string key) {
            if (!properties.TryGetValue(key, out var value)) {
                return null;
            }

            return value switch {
                int intValue => intValue,
                long longValue when longValue >= int.MinValue && longValue <= int.MaxValue => (int)longValue,
                double doubleValue when doubleValue >= int.MinValue && doubleValue <= int.MaxValue && doubleValue % 1 == 0 => (int)doubleValue,
                string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
                _ => null
            };
        }

        /// <summary>
        /// 安全获取整数属性，带默认值
        /// </summary>
        public static int GetInt32(this IReadOnlyDictionary<string, object> properties, string key, int defaultValue) {
            return properties.GetInt32(key) ?? defaultValue;
        }

        /// <summary>
        /// 安全获取长整数属性
        /// </summary>
        public static long? GetInt64(this IReadOnlyDictionary<string, object> properties, string key) {
            if (!properties.TryGetValue(key, out var value)) {
                return null;
            }

            return value switch {
                long longValue => longValue,
                int intValue => intValue,
                double doubleValue when doubleValue >= long.MinValue && doubleValue <= long.MaxValue && doubleValue % 1 == 0 => (long)doubleValue,
                string stringValue when long.TryParse(stringValue, out var parsed) => parsed,
                _ => null
            };
        }

        /// <summary>
        /// 安全获取长整数属性，带默认值
        /// </summary>
        public static long GetInt64(this IReadOnlyDictionary<string, object> properties, string key, long defaultValue) {
            return properties.GetInt64(key) ?? defaultValue;
        }

        /// <summary>
        /// 安全获取双精度浮点数属性
        /// </summary>
        public static double? GetDouble(this IReadOnlyDictionary<string, object> properties, string key) {
            if (!properties.TryGetValue(key, out var value)) {
                return null;
            }

            return value switch {
                double doubleValue => doubleValue,
                float floatValue => floatValue,
                int intValue => intValue,
                long longValue => longValue,
                string stringValue when double.TryParse(stringValue, out var parsed) => parsed,
                _ => null
            };
        }

        /// <summary>
        /// 安全获取双精度浮点数属性，带默认值
        /// </summary>
        public static double GetDouble(this IReadOnlyDictionary<string, object> properties, string key, double defaultValue) {
            return properties.GetDouble(key) ?? defaultValue;
        }

        /// <summary>
        /// 安全获取布尔属性
        /// </summary>
        public static bool? GetBoolean(this IReadOnlyDictionary<string, object> properties, string key) {
            if (!properties.TryGetValue(key, out var value)) {
                return null;
            }

            return value switch {
                bool boolValue => boolValue,
                string stringValue when bool.TryParse(stringValue, out var parsed) => parsed,
                int intValue => intValue != 0,
                long longValue => longValue != 0,
                _ => null
            };
        }

        /// <summary>
        /// 安全获取布尔属性，带默认值
        /// </summary>
        public static bool GetBoolean(this IReadOnlyDictionary<string, object> properties, string key, bool defaultValue) {
            return properties.GetBoolean(key) ?? defaultValue;
        }

        /// <summary>
        /// 安全获取日期时间属性
        /// </summary>
        public static DateTime? GetDateTime(this IReadOnlyDictionary<string, object> properties, string key) {
            if (!properties.TryGetValue(key, out var value)) {
                return null;
            }

            return value switch {
                DateTime dateTimeValue => dateTimeValue,
                string stringValue when DateTime.TryParse(stringValue, out var parsed) => parsed,
                _ => null
            };
        }

        /// <summary>
        /// 安全获取日期时间属性，带默认值
        /// </summary>
        public static DateTime GetDateTime(this IReadOnlyDictionary<string, object> properties, string key, DateTime defaultValue) {
            return properties.GetDateTime(key) ?? defaultValue;
        }

        /// <summary>
        /// 安全获取字符串数组属性
        /// </summary>
        public static string[]? GetStringArray(this IReadOnlyDictionary<string, object> properties, string key) {
            if (!properties.TryGetValue(key, out var value)) {
                return null;
            }

            return value switch {
                string[] stringArray => stringArray,
                List<string> stringList => stringList.ToArray(),
                IEnumerable<string> stringEnumerable => stringEnumerable.ToArray(),
                string singleString => new[] { singleString },
                _ => null
            };
        }

        /// <summary>
        /// 安全获取字符串数组属性，带默认值
        /// </summary>
        public static string[] GetStringArray(this IReadOnlyDictionary<string, object> properties, string key, string[] defaultValue) {
            return properties.GetStringArray(key) ?? defaultValue;
        }

        /// <summary>
        /// 安全获取字符串列表属性
        /// </summary>
        public static List<string>? GetStringList(this IReadOnlyDictionary<string, object> properties, string key) {
            var array = properties.GetStringArray(key);
            return array?.ToList();
        }

        /// <summary>
        /// 安全获取字符串列表属性，带默认值
        /// </summary>
        public static List<string> GetStringList(this IReadOnlyDictionary<string, object> properties, string key, List<string> defaultValue) {
            return properties.GetStringList(key) ?? defaultValue;
        }

        /// <summary>
        /// 检查属性是否存在
        /// </summary>
        public static bool HasProperty(this IReadOnlyDictionary<string, object> properties, string key) {
            return properties.ContainsKey(key);
        }

        /// <summary>
        /// 检查属性是否存在且不为null
        /// </summary>
        public static bool HasNonNullProperty(this IReadOnlyDictionary<string, object> properties, string key) {
            return properties.TryGetValue(key, out var value) && value != null;
        }

        /// <summary>
        /// 获取所有属性键
        /// </summary>
        public static IEnumerable<string> GetPropertyKeys(this IReadOnlyDictionary<string, object> properties) {
            return properties.Keys;
        }

        /// <summary>
        /// 获取属性的类型名称
        /// </summary>
        public static string? GetPropertyTypeName(this IReadOnlyDictionary<string, object> properties, string key) {
            if (!properties.TryGetValue(key, out var value) || value == null) {
                return null;
            }

            return value.GetType().Name;
        }
    }
}
