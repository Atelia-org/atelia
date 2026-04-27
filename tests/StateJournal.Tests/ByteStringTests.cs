using Xunit;

namespace Atelia.StateJournal.Tests;

public class ByteStringTests {
    [Fact]
    public void Empty_DefaultAndExplicit_Equal() {
        ByteString a = default;
        ByteString b = ByteString.Empty;
        Assert.True(a.IsEmpty);
        Assert.Equal(0, a.Length);
        Assert.True(a.AsSpan().IsEmpty);
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
    }

    [Fact]
    public void Ctor_ByteArray_ClonesData_IsolatedFromSource() {
        // CMS Step D: byte[] ctor 默认 defensive clone（与 ReadOnlySpan ctor 行为对齐）。
        // 调用方对原数组的后续 mutation 不会泄漏到本 ByteString。性能敏感场景请用 FromTrustedOwned 跳过 clone。
        byte[] arr = [1, 2, 3];
        var bs = new ByteString(arr);
        int hash1 = bs.GetHashCode();
        arr[1] = 0xFF;
        int hash2 = bs.GetHashCode();
        Assert.Equal(hash1, hash2);
        Assert.Equal(new byte[] { 1, 2, 3 }, bs.AsSpan().ToArray());
    }

    [Fact]
    public void Ctor_ReadOnlySpan_CopiesData_IsolatedFromSource() {
        // ReadOnlySpan ctor 必须 ToArray，外部 mutate 不应影响 ByteString。
        byte[] arr = [10, 20, 30];
        var bs = new ByteString((ReadOnlySpan<byte>)arr);
        int hash1 = bs.GetHashCode();
        arr[1] = 0xFF;
        int hash2 = bs.GetHashCode();
        Assert.Equal(hash1, hash2);
        Assert.Equal(new byte[] { 10, 20, 30 }, bs.AsSpan().ToArray());
    }

    [Fact]
    public void Ctor_ByteArray_NullThrows() {
        Assert.Throws<ArgumentNullException>(() => new ByteString((byte[])null!));
    }

    [Fact]
    public void FromTrustedOwned_DoesNotClone_SharesArrayReference() {
        // CMS Step D: FromTrustedOwned 是唯一跳过 defensive clone 的零拷贝入口（ctor 已收紧为强制 clone）。
        // 通过 mutate 后 hash 变化证明引用复用——本测试与 Ctor_ByteArray_ClonesData 形成对照, 锁定 trusted vs 默认两条路径的差异。
        byte[] arr = [7, 8, 9];
        var bs = ByteString.FromTrustedOwned(arr);
        int hash1 = bs.GetHashCode();
        arr[0] = 0xFE;
        int hash2 = bs.GetHashCode();
        Assert.NotEqual(hash1, hash2);
        Assert.Equal(new byte[] { 0xFE, 8, 9 }, bs.AsSpan().ToArray());
    }

    [Fact]
    public void FromTrustedOwned_NullThrows() {
        Assert.Throws<ArgumentNullException>(() => ByteString.FromTrustedOwned(null!));
    }

    [Fact]
    public void FromTrustedOwned_EmptyArray_BehavesLikeEmpty() {
        var bs = ByteString.FromTrustedOwned(Array.Empty<byte>());
        Assert.True(bs.IsEmpty);
        Assert.Equal(0, bs.Length);
        Assert.Equal(ByteString.Empty, bs);
    }

    [Fact]
    public void Ctor_EmptySpan_BehavesLikeEmpty() {
        var bs = new ByteString(ReadOnlySpan<byte>.Empty);
        Assert.True(bs.IsEmpty);
        Assert.Equal(0, bs.Length);
        Assert.Equal(ByteString.Empty, bs);
    }

    [Fact]
    public void Ctor_ByteArray_EmptyArray_BehavesLikeEmpty() {
        // CMS Step D: ctor clone 路径上空数组特殊形态——应得到 IsEmpty == true（_data == null），与 ReadOnlySpan ctor 对齐。
        var bs = new ByteString(Array.Empty<byte>());
        Assert.True(bs.IsEmpty);
        Assert.Equal(0, bs.Length);
        Assert.Equal(ByteString.Empty, bs);
    }

    [Fact]
    public void Equals_SameContent_TrueAcrossDifferentBackingArrays() {
        var a = new ByteString(new byte[] { 1, 2, 3 });
        var b = new ByteString(new byte[] { 1, 2, 3 });
        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equals_DifferentContent_False() {
        var a = new ByteString(new byte[] { 1, 2, 3 });
        var b = new ByteString(new byte[] { 1, 2, 4 });
        Assert.False(a.Equals(b));
        Assert.True(a != b);
    }

    [Fact]
    public void Equals_DifferentLength_False() {
        var a = new ByteString(new byte[] { 1, 2, 3 });
        var b = new ByteString(new byte[] { 1, 2, 3, 0 });
        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Equals_NullBytesAndHighBytes_RoundTripsContent() {
        var a = new ByteString(new byte[] { 0x00, 0xFF, 0x7F, 0x80, 0x00 });
        var b = new ByteString(new byte[] { 0x00, 0xFF, 0x7F, 0x80, 0x00 });
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void EqualsObject_NonByteString_False() {
        var a = new ByteString(new byte[] { 1 });
        Assert.False(a.Equals((object)"not a byte string"));
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void GetHashCode_EmptyVsSingleZero_Differ() {
        // FNV-1a: empty => 2166136261, [0x00] => (2166136261 ^ 0) * 16777619 mod 2^32.
        // 不严格断言数值，只要求不同。
        var empty = ByteString.Empty;
        var zero = new ByteString(new byte[] { 0 });
        Assert.NotEqual(empty.GetHashCode(), zero.GetHashCode());
    }

    [Fact]
    public void ToString_DoesNotThrow_AndIncludesLength() {
        Assert.Equal("ByteString[0]", ByteString.Empty.ToString());
        var bs = new ByteString(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        string s = bs.ToString();
        Assert.Contains("ByteString[4]", s);
        Assert.Contains("DE", s);
        Assert.Contains("EF", s);
    }

    [Fact]
    public void ToString_LongerThanPreview_AppendsEllipsis() {
        var bs = new ByteString(new byte[16]); // 16 bytes, preview is 8.
        string s = bs.ToString();
        Assert.Contains("ByteString[16]", s);
        Assert.Contains("...", s);
    }
}
