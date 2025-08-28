using System;

namespace MemoTree.Core.Encoding {
    /// <summary>
    /// 编码器接口：最纯粹的编码/解码抽象
    /// </summary>
    public interface IEncoder {
        /// <summary>
        /// 将字节数据编码为字符串
        /// </summary>
        /// <param name="data">要编码的字节数据</param>
        /// <returns>编码后的字符串</returns>
        string EncodeBytes(byte[] data);

        /// <summary>
        /// 将编码字符串解码为字节数据
        /// </summary>
        /// <param name="encoded">编码的字符串</param>
        /// <returns>解码后的字节数据</returns>
        byte[] DecodeString(string encoded);

        /// <summary>
        /// 每个字符的位数
        /// </summary>
        float BitsPerChar {
            get;
        }

        /// <summary>
        /// 编码模式名称
        /// </summary>
        string ModeName {
            get;
        }

        /// <summary>
        /// 编码UUID/GUID（默认实现）
        /// </summary>
        /// <param name="guid">要编码的GUID，如果为null则生成新的GUID</param>
        /// <returns>编码后的字符串</returns>
        string EncodeUuid(Guid? guid = null);

        /// <summary>
        /// 解码UUID/GUID（默认实现）
        /// </summary>
        /// <param name="encoded">编码的字符串</param>
        /// <returns>解码后的GUID</returns>
        Guid DecodeUuid(string encoded);
    }
}
