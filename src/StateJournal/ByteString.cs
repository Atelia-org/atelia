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
/// <see cref="ByteString(byte[])"/> 默认走 defensive clone（与 <see cref="ByteString(ReadOnlySpan{byte})"/> 对齐），
/// 调用方对原数组的后续 mutation 不会泄漏到本 <see cref="ByteString"/>。性能敏感场景（大 blob + 已知调用方独占所有权）
/// 可用 <see cref="FromTrustedOwned(byte[])"/> 跳过 <see cref="ByteString"/> 自身的 clone；若还要跳过 mixed
/// face 入池 clone，必须显式选择 trusted 入池路径。
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
    /// 防御性复制 <paramref name="data"/>，将外部可变数组隔离为 <see cref="ByteString"/> 独占的内部副本。
    /// 调用方后续对 <paramref name="data"/> 的 mutation 不会影响本 <see cref="ByteString"/>。与
    /// <see cref="ByteString(ReadOnlySpan{byte})"/> 行为对齐。
    /// </summary>
    /// <remarks>
    /// 性能敏感场景（大 blob + 已知调用方独占所有权）请改用 <see cref="FromTrustedOwned(byte[])"/> 跳过本次 clone；
    /// 若还要跳过 mixed face 入池 clone，必须显式选择 trusted 入池路径。
    /// </remarks>
    public ByteString(byte[] data) : this(data, clone: true) { }

    /// <summary>
    /// 复制 <paramref name="data"/> 到内部数组。<see cref="ReadOnlySpan{T}"/> 的生命周期不安全，必须复制。
    /// </summary>
    public ByteString(ReadOnlySpan<byte> data) {
        _data = data.IsEmpty ? null : data.ToArray();
    }

    /// <summary>
    /// 内部共享构造路径：根据 <paramref name="clone"/> 决定是否 defensive clone。
    /// <c>clone=true</c> 服务 <see cref="ByteString(byte[])"/> public ctor；<c>clone=false</c> 服务
    /// <see cref="FromTrustedOwned(byte[])"/>。
    /// </summary>
    private ByteString(byte[] data, bool clone) {
        ArgumentNullException.ThrowIfNull(data);
        if (clone) {
            _data = data.Length == 0 ? null : (byte[])data.Clone();
        }
        else {
            _data = data;
        }
    }

    /// <summary>空字节串。等价于 <c>default(ByteString)</c>。</summary>
    public static ByteString Empty => default;

    /// <summary>
    /// 高级 API：直接持有调用方提供的 <paramref name="data"/> 引用，<strong>跳过</strong>默认 <see cref="ByteString(byte[])"/>
    /// ctor 的 defensive clone，明示"独占所有权"语义。配合显式 trusted 入池路径可达成大 blob 端到端零拷贝。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>调用方必须保证</strong>：(a) 除本次转交给 <see cref="ByteString"/> 的引用外，<paramref name="data"/>
    /// 没有任何会被继续使用的活跃可变引用；(b) <paramref name="data"/> 后续永不被任何代码 mutate（哪怕调用方自己也不行）。违反契约会导致 StateJournal
    /// 内部 dict / wire 状态被静默篡改，类型系统无法防御。
    /// </para>
    /// <para>
    /// 用途：从 IO buffer / <see cref="System.Buffers.ArrayPool{T}"/> rent / 文件读取等场景拿到的大 byte[]，
    /// 已知数组生命周期、且只会传递给 StateJournal 一次的高级用户。普通用户应继续使用
    /// <see cref="ByteString(byte[])"/>（自带 defensive clone）或更安全的
    /// <see cref="ByteString(ReadOnlySpan{byte})"/>（无条件复制）。
    /// </para>
    /// <para>
    /// 端到端零拷贝需要 <c>ByteString.FromTrustedOwned(largeBytes)</c> + 显式 trusted 入池路径（当前实现层为
    /// <c>BlobPayloadFace.FromTrusted</c> / <c>UpdateOrInitTrusted</c>；公开 mixed 容器 trusted API 留待后续接通）。
    /// 仅创建 <c>ByteString</c> 但走默认 mixed <c>Upsert</c> 路径仍会触发 face 层 defensive clone（CMS Step 3b 决策 B），
    /// 此时 <see cref="FromTrustedOwned"/> 节省的只有 ctor 一次 clone。
    /// </para>
    /// </remarks>
    public static ByteString FromTrustedOwned(byte[] data) {
        return new ByteString(data, clone: false);
    }

    public int Length => _data?.Length ?? 0;

    public bool IsEmpty => _data is null || _data.Length == 0;

    public ReadOnlySpan<byte> AsSpan() => _data;

    /// <summary>
    /// 内部使用：取出底层 <see cref="byte"/>[] 引用（<c>default</c> 情况下返回 <see cref="Array.Empty{T}"/>）。
    /// 默认 <see cref="Internal.ValueBox.BlobPayloadFace"/> 入池路径走 defensive clone（CMS Step 3b 决策 B）；
    /// 高级零拷贝路径 <c>BlobPayloadFace.FromTrusted</c> / <c>UpdateOrInitTrusted</c> 通过本方法直接复用底层数组，
    /// 配合 <see cref="FromTrustedOwned(byte[])"/> 形成 "独占 + immutable" 契约入口。
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
