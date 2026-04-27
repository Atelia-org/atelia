using System.Text;

namespace Atelia.StateJournal;

/// <summary>
/// Immutable 字节串值类型，作为 mixed payload <c>BlobPayload</c> 的业务侧 API。
/// 与 <see cref="string"/> / <see cref="Symbol"/> 对称：值语义、不可变、<c>default</c> 等价于 <see cref="Empty"/>。
/// </summary>
/// <remarks>
/// <para>
/// <strong>术语对照</strong>：本类在用户侧称为 <c>ByteString</c>；在框架内部 / wire / pool / face 实现中统称为
/// "Blob"（如 <c>HeapValueKind.BlobPayload</c> / <c>ValueKind.Blob</c> / <c>OfOwnedBlob</c> / <c>BlobPayloadFace</c> /
/// <c>OfBlob</c> view / wire tag <c>0xC1</c>）。两个名字指代同一概念：immutable byte sequence with payload semantics。
/// 这与 <see cref="string"/>（业务 API）↔ <c>StringPayload</c>（内部前缀）的分工完全对称——业务侧用 BCL/简短名，
/// 实现侧用 payload 概念专有术语。详见 <c>MixedValueCatalog.cs</c> 的术语表。
/// </para>
/// <para>
/// <see cref="ByteString(byte[])"/> 不做防御性 clone：调用方必须保证传入的 <see cref="byte"/>[] 在 <see cref="ByteString"/>
/// 的整个生命周期内不被外部代码 mutate（与 <see cref="string"/> 的 immutable 约定对齐）。需要安全副本时请使用
/// <see cref="ByteString(ReadOnlySpan{byte})"/>，它会无条件复制一份。
/// </para>
/// <para><see cref="Equals(ByteString)"/> 走字节序列等值；<see cref="GetHashCode"/> 走 FNV-1a 32-bit。</para>
/// <para>
/// 暂不支持 <c>null</c> 概念：上层（mixed 容器 / wire）通过单独的 null tag 表达“无”，<see cref="ByteString"/> 自身只表示
/// “一个具体的字节序列”（可以为空）。
/// </para>
/// </remarks>
public readonly struct ByteString : IEquatable<ByteString> {
    private readonly byte[]? _data;

    /// <summary>
    /// 直接持有传入数组的引用，不做防御性 clone。调用方必须保证 <paramref name="data"/> 不被外部 mutate。
    /// </summary>
    public ByteString(byte[] data) {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
    }

    /// <summary>
    /// 复制 <paramref name="data"/> 到内部数组。<see cref="ReadOnlySpan{T}"/> 的生命周期不安全，必须复制。
    /// </summary>
    public ByteString(ReadOnlySpan<byte> data) {
        _data = data.IsEmpty ? null : data.ToArray();
    }

    /// <summary>空字节串。等价于 <c>default(ByteString)</c>。</summary>
    public static ByteString Empty => default;

    public int Length => _data?.Length ?? 0;

    public bool IsEmpty => _data is null || _data.Length == 0;

    public ReadOnlySpan<byte> AsSpan() => _data;

    /// <summary>
    /// 内部使用：取出底层 <see cref="byte"/>[] 引用（<c>default</c> 情况下返回 <see cref="Array.Empty{T}"/>）。
    /// 当前 <see cref="Internal.ValueBox.BlobPayloadFace"/> 入池路径走 defensive clone（CMS Step 3b 决策 B），
    /// 暂无活跃调用方；保留作为 future <c>ByteString.FromTrustedOwned(byte[])</c> 高级 API 的零拷贝种子，
    /// 届时配合 face 的 <c>FromTrusted</c> 路径直接复用底层数组。
    /// </summary>
    internal byte[] DangerousGetUnderlyingArray() => _data ?? Array.Empty<byte>();

    public bool Equals(ByteString other) => AsSpan().SequenceEqual(other.AsSpan());

    public override bool Equals(object? obj) => obj is ByteString other && Equals(other);

    public override int GetHashCode() {
        // FNV-1a 32-bit。简单可靠，对短字节串友好；对碰撞敏感的场景留待 future 升级（如 xxHash）。
        ReadOnlySpan<byte> span = AsSpan();
        uint hash = 2166136261u;
        for (int i = 0; i < span.Length; i++) {
            hash ^= span[i];
            hash *= 16777619u;
        }
        return unchecked((int)hash);
    }

    public static bool operator ==(ByteString left, ByteString right) => left.Equals(right);
    public static bool operator !=(ByteString left, ByteString right) => !left.Equals(right);

    /// <summary>调试用短预览：<c>ByteString[len] HH HH HH ...</c>。不保证稳定格式，仅用于诊断。</summary>
    public override string ToString() {
        ReadOnlySpan<byte> span = AsSpan();
        if (span.IsEmpty) { return "ByteString[0]"; }

        const int PreviewBytes = 8;
        int show = Math.Min(span.Length, PreviewBytes);
        var sb = new StringBuilder(16 + show * 3);
        sb.Append("ByteString[").Append(span.Length).Append("] ");
        for (int i = 0; i < show; i++) {
            if (i > 0) { sb.Append(' '); }
            sb.Append(span[i].ToString("X2"));
        }
        if (span.Length > show) { sb.Append(" ..."); }
        return sb.ToString();
    }
}
