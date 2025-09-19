using System.Globalization;

namespace MemoTree.Core.Storage.Versioned {
    /// <summary>
    /// 十进制版本号格式化器
    /// </summary>
    public class DecimalVersionFormatter : IVersionFormatter {
        public string FormatVersion(long version) {
            return version.ToString();
        }

        public long? ParseVersion(string versionString) {
            if (long.TryParse(versionString, out var version) && version >= 1) { return version; }
            return null;
        }
    }
}
