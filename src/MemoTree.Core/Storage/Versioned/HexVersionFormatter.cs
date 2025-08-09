using System.Globalization;

namespace MemoTree.Core.Storage.Versioned
{
    /// <summary>
    /// 十六进制版本号格式化器，使用更短的文件名
    /// </summary>
    public class HexVersionFormatter : IVersionFormatter
    {
        public string FormatVersion(long version)
        {
            return version.ToString("X");
        }

        public long? ParseVersion(string versionString)
        {
            if (long.TryParse(versionString, NumberStyles.HexNumber, null, out var version) && version >= 1)
            {
                return version;
            }
            return null;
        }
    }
}
