namespace MemoTree.Core.Storage.Versioned
{
    /// <summary>
    /// 版本号格式化器接口，用于统一处理版本号的编码和解码
    /// </summary>
    public interface IVersionFormatter
    {
        /// <summary>
        /// 将版本号格式化为文件名中的字符串
        /// </summary>
        /// <param name="version">版本号</param>
        /// <returns>格式化后的字符串</returns>
        string FormatVersion(long version);
        
        /// <summary>
        /// 从文件名中的字符串解析版本号
        /// </summary>
        /// <param name="versionString">版本字符串</param>
        /// <returns>解析成功返回版本号，失败返回null</returns>
        long? ParseVersion(string versionString);
    }
}
