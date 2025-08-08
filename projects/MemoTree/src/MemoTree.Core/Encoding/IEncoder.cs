using System;

namespace MemoTree.Core.Encoding
{
    public interface IEncoder
    {
        string EncodeBytes(byte[] data);
        byte[] DecodeString(string encoded);
        float BitsPerChar { get; }
        string ModeName { get; }
        string EncodeUuid(Guid? guid = null);
        Guid DecodeUuid(string encoded);
    }
}
