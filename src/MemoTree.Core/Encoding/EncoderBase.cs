using System;

namespace MemoTree.Core.Encoding {
    /// <summary>
    /// 编码器基类：提供UUID编码/解码的默认实现
    /// </summary>
    public abstract class EncoderBase : IEncoder {
        /// <summary>
        /// 将字节数据编码为字符串
        /// </summary>
        public abstract string EncodeBytes(byte[] data);

        /// <summary>
        /// 将编码字符串解码为字节数据
        /// </summary>
        public abstract byte[] DecodeString(string encoded);

        /// <summary>
        /// 每个字符的位数
        /// </summary>
        public abstract float BitsPerChar {
            get;
        }

        /// <summary>
        /// 编码模式名称
        /// </summary>
        public abstract string ModeName {
            get;
        }

        /// <summary>
        /// 编码UUID/GUID（默认实现）
        /// </summary>
        /// <param name="guid">要编码的GUID，如果为null则生成新的GUID</param>
        /// <returns>编码后的字符串</returns>
        public virtual string EncodeUuid(Guid? guid = null) {
            var guidValue = guid ?? Guid.NewGuid();
            var guidBytes = guidValue.ToByteArray();
            return EncodeBytes(guidBytes);
        }

        /// <summary>
        /// 解码UUID/GUID（默认实现）
        /// </summary>
        /// <param name="encoded">编码的字符串</param>
        /// <returns>解码后的GUID</returns>
        public virtual Guid DecodeUuid(string encoded) {
            if (string.IsNullOrEmpty(encoded)) {
                throw new ArgumentException("编码字符串不能为空", nameof(encoded));
            }

            var guidBytes = DecodeString(encoded);

            if (guidBytes.Length != 16) {
                throw new ArgumentException($"解码结果长度错误: {guidBytes.Length} bytes，期望16 bytes", nameof(encoded));
            }

            return new Guid(guidBytes);
        }
    }
}
