using System;
using System.Collections.Generic;
using System.Linq;

namespace MemoTree.Core.Encoding {
    /// <summary>
    /// 自定义字符集编码器基类：需要外部提供字符集的编码器
    /// </summary>
    public abstract class CustomCharsetEncoderBase : EncoderBase {
        protected readonly char[] Charset;
        protected readonly Dictionary<char, int> CharToIndex;
        protected readonly int Base;

        /// <summary>
        /// 初始化自定义字符集编码器
        /// </summary>
        /// <param name="charset">字符集序列</param>
        protected CustomCharsetEncoderBase(string charset) {
            if (string.IsNullOrEmpty(charset)) { throw new ArgumentException("字符集不能为空", nameof(charset)); }
            var charArray = charset.ToCharArray();
            if (charArray.Length != charArray.Distinct().Count()) { throw new ArgumentException("字符集中包含重复字符", nameof(charset)); }
            Charset = charArray;
            Base = Charset.Length;
            CharToIndex = new Dictionary<char, int>();

            for (int i = 0; i < Charset.Length; i++) {
                CharToIndex[Charset[i]] = i;
            }
        }

        /// <summary>
        /// 验证字符集大小是否满足要求
        /// </summary>
        /// <param name="minSize">最小字符集大小</param>
        /// <param name="encoderName">编码器名称（用于错误信息）</param>
        protected void ValidateCharsetSize(int minSize, string encoderName) {
            if (Charset.Length < minSize) { throw new ArgumentException($"{encoderName}需要至少{minSize}个字符，当前只有{Charset.Length}个"); }
        }
    }
}
