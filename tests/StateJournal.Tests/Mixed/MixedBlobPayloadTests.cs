using System.Linq;
using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;
using Xunit;

namespace Atelia.StateJournal.Tests;

// ai:impl `src/StateJournal/MixedValueCatalog.cs` (ByteString entry)
// ai:impl `src/StateJournal/Internal/ValueBox.Blob.cs` (BlobPayloadFace)
//
// Mixed 容器中 BlobPayload (业务 API: ByteString) 的端到端契约测试 (CMS Step 3c)。
// 与 mixed String 严格区隔：
//   - String 走 OwnedStringPool；Blob 走 OwnedBlobPool；
//   - ValueKind 分别为 String / Blob，互读 → TypeMismatch；
//   - ByteString 是值类型且无 null 概念 (default == Empty)，与 BooleanFace 一致。
//
// 这些测试与 MixedStringPayloadTests 共享 partial class RevisionTests 的 helpers
// (CreateRevision / CommitToFile / OpenRevision / AssertCommitSucceeded / GetTempFilePath / AssertSuccess)。
partial class RevisionTests {

    private static ByteString Bs(params byte[] data) => new(data);

    // ═══════════════════════ MixedDict<int-key> ═══════════════════════

    [Fact]
    public void MixedDict_Blob_Upsert_Get_RoundTrip() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        dict.Upsert(1, Bs(0x01, 0x02, 0x03));
        dict.OfBlob.Upsert(2, Bs(0xAA, 0xBB));

        Assert.True(dict.TryGetValueKind(1, out var k1));
        Assert.Equal(ValueKind.Blob, k1);
        Assert.Equal(GetIssue.None, dict.OfBlob.Get(1, out ByteString v1));
        Assert.Equal(Bs(0x01, 0x02, 0x03), v1);
        Assert.True(dict.TryGet<ByteString>(2, out var v2));
        Assert.Equal(Bs(0xAA, 0xBB), v2);
    }

    [Fact]
    public void MixedDict_Blob_Empty_RoundTrip() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        int ownedBefore = ValuePools.OfOwnedBlob.Count;
        dict.Upsert(1, ByteString.Empty);
        // 与 mixed string 对称：空 blob 仍占一个 owned slot（不映射到 Null）。
        Assert.Equal(ownedBefore + 1, ValuePools.OfOwnedBlob.Count);
        Assert.True(dict.TryGetValueKind(1, out var kind));
        Assert.Equal(ValueKind.Blob, kind);
        Assert.Equal(GetIssue.None, dict.OfBlob.Get(1, out ByteString v));
        Assert.True(v.IsEmpty);
    }

    [Fact]
    public void MixedDict_Blob_SameContent_NotDeduplicated() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        int ownedBefore = ValuePools.OfOwnedBlob.Count;
        dict.Upsert(1, Bs(1, 2, 3));
        dict.Upsert(2, Bs(1, 2, 3));

        // 不去重：两次 Upsert 各占一个 owned slot。
        Assert.Equal(ownedBefore + 2, ValuePools.OfOwnedBlob.Count);
    }

    [Fact]
    public void MixedDict_Blob_VsString_TypeIsolation() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        dict.OfString.Upsert(1, "hi");
        Assert.Equal(GetIssue.TypeMismatch, dict.OfBlob.Get(1, out ByteString _));

        dict.OfBlob.Upsert(2, Bs(0x68, 0x69)); // "hi" 的字节
        Assert.Equal(GetIssue.TypeMismatch, dict.OfString.Get(2, out string? _));
    }

    [Fact]
    public void MixedDict_Blob_VsSymbol_TypeIsolation() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        dict.OfSymbol.Upsert(1, "shared");
        Assert.Equal(GetIssue.TypeMismatch, dict.OfBlob.Get(1, out ByteString _));

        dict.OfBlob.Upsert(2, Bs(1, 2));
        Assert.Equal(GetIssue.TypeMismatch, dict.OfSymbol.Get(2, out Symbol _));
    }

    [Fact]
    public void MixedDict_Blob_InplaceUpdate_DifferentLengths_PoolStaysBalanced() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        int ownedBefore = ValuePools.OfOwnedBlob.Count;
        dict.Upsert(1, Bs(1));
        dict.Upsert(1, Bs(1, 2, 3, 4, 5, 6, 7, 8, 9));
        dict.Upsert(1, Bs(0xAA));
        dict.Upsert(1, ByteString.Empty);
        dict.Upsert(1, Bs(0xFF, 0xEE));

        Assert.Equal(ownedBefore + 1, ValuePools.OfOwnedBlob.Count);
        Assert.Equal(GetIssue.None, dict.OfBlob.Get(1, out ByteString got));
        Assert.Equal(Bs(0xFF, 0xEE), got);

        Assert.True(dict.Remove(1));
        Assert.Equal(ownedBefore, ValuePools.OfOwnedBlob.Count);
    }

    [Fact]
    public void MixedDict_Blob_String_BlobSwap_PoolReleased() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        int blobBefore = ValuePools.OfOwnedBlob.Count;
        int strBefore = ValuePools.OfOwnedString.Count;

        dict.Upsert(1, Bs(1, 2, 3));
        Assert.Equal(blobBefore + 1, ValuePools.OfOwnedBlob.Count);

        // blob → string：旧 OwnedBlob slot 应被释放。
        dict.Upsert(1, "now string");
        Assert.Equal(blobBefore, ValuePools.OfOwnedBlob.Count);
        Assert.Equal(strBefore + 1, ValuePools.OfOwnedString.Count);
        Assert.True(dict.TryGetValueKind(1, out var k1));
        Assert.Equal(ValueKind.String, k1);

        // 反向：string → blob，旧 OwnedString slot 应被释放。
        dict.Upsert(1, Bs(0xDE, 0xAD, 0xBE, 0xEF));
        Assert.Equal(blobBefore + 1, ValuePools.OfOwnedBlob.Count);
        Assert.Equal(strBefore, ValuePools.OfOwnedString.Count);
        Assert.True(dict.TryGetValueKind(1, out var k2));
        Assert.Equal(ValueKind.Blob, k2);

        Assert.True(dict.Remove(1));
        Assert.Equal(blobBefore, ValuePools.OfOwnedBlob.Count);
    }

    [Fact]
    public void MixedDict_Blob_Symbol_BlobSwap_PoolReleased() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        int blobBefore = ValuePools.OfOwnedBlob.Count;
        dict.Upsert(1, Bs(1, 2, 3));
        Assert.Equal(blobBefore + 1, ValuePools.OfOwnedBlob.Count);

        // blob → Symbol：旧 OwnedBlob slot 应被释放。
        dict.OfSymbol.Upsert(1, "tag");
        Assert.Equal(blobBefore, ValuePools.OfOwnedBlob.Count);
        Assert.True(dict.TryGetValueKind(1, out var k1));
        Assert.Equal(ValueKind.Symbol, k1);

        // 反向
        dict.Upsert(1, Bs(7, 8, 9));
        Assert.Equal(blobBefore + 1, ValuePools.OfOwnedBlob.Count);
    }

    [Fact]
    public void MixedDict_Blob_HeapInteger_BlobSwap_PoolReleased() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        int blobBefore = ValuePools.OfOwnedBlob.Count;
        int bits64Before = ValuePools.OfBits64.Count;

        dict.Upsert(1, ulong.MaxValue);
        Assert.Equal(bits64Before + 1, ValuePools.OfBits64.Count);

        // heap integer → blob
        dict.Upsert(1, Bs(1, 2, 3, 4));
        Assert.Equal(bits64Before, ValuePools.OfBits64.Count);
        Assert.Equal(blobBefore + 1, ValuePools.OfOwnedBlob.Count);

        // blob → heap integer
        dict.Upsert(1, ulong.MaxValue - 1);
        Assert.Equal(bits64Before + 1, ValuePools.OfBits64.Count);
        Assert.Equal(blobBefore, ValuePools.OfOwnedBlob.Count);
    }

    [Fact]
    public void MixedDict_Blob_ToNull_CrossKind_PoolReleased() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        int blobBefore = ValuePools.OfOwnedBlob.Count;
        dict.Upsert(1, Bs(1, 2, 3));
        Assert.Equal(blobBefore + 1, ValuePools.OfOwnedBlob.Count);

        // blob → null (via string null path; ByteString itself has no null)
        dict.Upsert<string>(1, null);
        Assert.Equal(blobBefore, ValuePools.OfOwnedBlob.Count);
        Assert.True(dict.TryGetValueKind(1, out var kind));
        Assert.Equal(ValueKind.Null, kind);
    }

    [Fact]
    public void MixedDict_Blob_DiscardChanges_RestoresCommittedBlobAndReleasesDirtySlot() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        root.Upsert(1, Bs(1, 2, 3));
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        int blobAfterCommit = ValuePools.OfOwnedBlob.Count;
        root.Upsert(1, Bs(0xAA, 0xBB, 0xCC, 0xDD, 0xEE));
        Assert.Equal(blobAfterCommit + 1, ValuePools.OfOwnedBlob.Count);

        root.DiscardChanges();

        Assert.Equal(blobAfterCommit, ValuePools.OfOwnedBlob.Count);
        Assert.Equal(GetIssue.None, root.OfBlob.Get(1, out ByteString value));
        Assert.Equal(Bs(1, 2, 3), value);
    }

    [Fact]
    public void MixedDict_Blob_Commit_Open_RoundTrip() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        root.Upsert(1, ByteString.Empty);
        root.Upsert(2, Bs(0xDE, 0xAD, 0xBE, 0xEF));
        root.Upsert(3, Bs(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()));
        root.OfString.Upsert(4, "string-not-blob");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var opened = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(opened.IsSuccess, $"Open failed: {opened.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(opened.Value!.GraphRoot);
        Assert.Equal(4, loaded.Count);

        Assert.Equal(GetIssue.None, loaded.OfBlob.Get(1, out ByteString b1));
        Assert.True(b1.IsEmpty);
        Assert.Equal(GetIssue.None, loaded.OfBlob.Get(2, out ByteString b2));
        Assert.Equal(Bs(0xDE, 0xAD, 0xBE, 0xEF), b2);
        Assert.Equal(GetIssue.None, loaded.OfBlob.Get(3, out ByteString b3));
        Assert.Equal(256, b3.Length);
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), b3.AsSpan().ToArray());

        // ValueKind 严格区分
        Assert.True(loaded.TryGetValueKind(2, out var k2));
        Assert.Equal(ValueKind.Blob, k2);
        Assert.True(loaded.TryGetValueKind(4, out var k4));
        Assert.Equal(ValueKind.String, k4);

        // string ↔ blob 互读 TypeMismatch 在 reopen 后仍成立
        Assert.Equal(GetIssue.TypeMismatch, loaded.OfString.Get(2, out string? _));
        Assert.Equal(GetIssue.TypeMismatch, loaded.OfBlob.Get(4, out ByteString _));
    }

    // ═══════════════════════ MixedDeque ═══════════════════════

    [Fact]
    public void MixedDeque_Blob_PushPop_RoundTrip() {
        var rev = CreateRevision();
        var deque = rev.CreateDeque();

        deque.PushBack(Bs(0x10, 0x20));
        deque.OfBlob.PushFront(Bs(0x00));
        deque.OfBlob.PushBack(Bs(0xFF, 0xEE, 0xDD));

        Assert.Equal(3, deque.Count);
        Assert.Equal(GetIssue.None, deque.OfBlob.GetAt(0, out ByteString a0));
        Assert.Equal(Bs(0x00), a0);
        Assert.Equal(GetIssue.None, deque.OfBlob.GetAt(1, out ByteString a1));
        Assert.Equal(Bs(0x10, 0x20), a1);
        Assert.Equal(GetIssue.None, deque.OfBlob.GetAt(2, out ByteString a2));
        Assert.Equal(Bs(0xFF, 0xEE, 0xDD), a2);

        int ownedBefore = ValuePools.OfOwnedBlob.Count;
        Assert.Equal(GetIssue.None, deque.OfBlob.PopFront(out ByteString popped));
        Assert.Equal(Bs(0x00), popped);
        Assert.Equal(ownedBefore - 1, ValuePools.OfOwnedBlob.Count);
    }

    [Fact]
    public void MixedDeque_Blob_SetAt_DifferentLength_PoolStaysBalanced() {
        var rev = CreateRevision();
        var deque = rev.CreateDeque();

        deque.OfBlob.PushBack(Bs(1, 2, 3));
        int ownedBefore = ValuePools.OfOwnedBlob.Count;

        Assert.True(deque.OfBlob.TrySetAt(0, Bs(1, 2, 3, 4, 5, 6, 7, 8))); // 改长度
        Assert.True(deque.OfBlob.TrySetAt(0, Bs(0xAB))); // 缩短
        Assert.True(deque.OfBlob.TrySetAt(0, ByteString.Empty));

        Assert.Equal(ownedBefore, ValuePools.OfOwnedBlob.Count); // inplace 复用
        Assert.Equal(GetIssue.None, deque.OfBlob.GetAt(0, out ByteString got));
        Assert.True(got.IsEmpty);
    }

    [Fact]
    public void MixedDeque_Blob_SetAt_CrossKind_PoolReleased() {
        var rev = CreateRevision();
        var deque = rev.CreateDeque();

        int blobBefore = ValuePools.OfOwnedBlob.Count;
        int strBefore = ValuePools.OfOwnedString.Count;

        deque.OfBlob.PushBack(Bs(1, 2, 3));
        Assert.Equal(blobBefore + 1, ValuePools.OfOwnedBlob.Count);

        // blob → string
        Assert.True(deque.OfString.TrySetAt(0, "hello"));
        Assert.Equal(blobBefore, ValuePools.OfOwnedBlob.Count);
        Assert.Equal(strBefore + 1, ValuePools.OfOwnedString.Count);

        // string → Symbol
        Assert.True(deque.OfSymbol.TrySetAt(0, "sym"));
        Assert.Equal(strBefore, ValuePools.OfOwnedString.Count);

        // Symbol → blob
        Assert.True(deque.OfBlob.TrySetAt(0, Bs(9, 9, 9)));
        Assert.Equal(blobBefore + 1, ValuePools.OfOwnedBlob.Count);
    }

    [Fact]
    public void MixedDeque_Blob_VsString_TypeIsolation() {
        var rev = CreateRevision();
        var deque = rev.CreateDeque();

        deque.OfString.PushBack("str");
        deque.OfBlob.PushBack(Bs(1, 2, 3));

        Assert.Equal(GetIssue.None, deque.OfString.GetAt(0, out string? str));
        Assert.Equal("str", str);
        Assert.Equal(GetIssue.TypeMismatch, deque.OfBlob.GetAt(0, out ByteString _));

        Assert.Equal(GetIssue.None, deque.OfBlob.GetAt(1, out ByteString blob));
        Assert.Equal(Bs(1, 2, 3), blob);
        Assert.Equal(GetIssue.TypeMismatch, deque.OfString.GetAt(1, out string? _));
    }

    [Fact]
    public void MixedDeque_Blob_LargeBlob_RoundTrip() {
        var rev = CreateRevision();
        var deque = rev.CreateDeque();

        // 16KB blob
        byte[] big = new byte[16 * 1024];
        for (int i = 0; i < big.Length; i++) { big[i] = (byte)(i & 0xFF); }
        deque.OfBlob.PushBack(new ByteString(big));

        Assert.Equal(GetIssue.None, deque.OfBlob.GetAt(0, out ByteString got));
        Assert.Equal(big.Length, got.Length);
        Assert.True(big.AsSpan().SequenceEqual(got.AsSpan()));

        Assert.Equal(GetIssue.None, deque.OfBlob.PopFront(out ByteString popped));
        Assert.Equal(big.Length, popped.Length);
    }

    [Fact]
    public void MixedDeque_Blob_Commit_Open_RoundTrip() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var deque = rev.CreateDeque();
        deque.OfBlob.PushBack(ByteString.Empty);
        deque.OfBlob.PushBack(Bs(0xCA, 0xFE));
        deque.OfBlob.PushBack(Bs(Enumerable.Range(0, 100).Select(i => (byte)(i * 3)).ToArray()));
        deque.OfString.PushBack("string-tail");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, deque, file));

        var opened = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(opened.IsSuccess, $"Open failed: {opened.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDeque>(opened.Value!.GraphRoot);
        Assert.Equal(4, loaded.Count);
        Assert.Equal(GetIssue.None, loaded.OfBlob.GetAt(0, out ByteString b0));
        Assert.True(b0.IsEmpty);
        Assert.Equal(GetIssue.None, loaded.OfBlob.GetAt(1, out ByteString b1));
        Assert.Equal(Bs(0xCA, 0xFE), b1);
        Assert.Equal(GetIssue.None, loaded.OfBlob.GetAt(2, out ByteString b2));
        Assert.Equal(100, b2.Length);
        Assert.Equal(GetIssue.None, loaded.OfString.GetAt(3, out string? s3));
        Assert.Equal("string-tail", s3);
    }

    // ═══════════════════════ MixedOrderedDict ═══════════════════════

    [Fact]
    public void MixedOrderedDict_Blob_Upsert_Get_RoundTrip() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();

        dict.Upsert(2, Bs(2, 2));
        dict.OfBlob.Upsert(1, Bs(1));
        dict.OfBlob.Upsert(3, Bs(3, 3, 3));

        Assert.Equal(GetIssue.None, dict.OfBlob.Get(1, out ByteString v1));
        Assert.Equal(Bs(1), v1);
        Assert.Equal(GetIssue.None, dict.OfBlob.Get(2, out ByteString v2));
        Assert.Equal(Bs(2, 2), v2);
        Assert.Equal(GetIssue.None, dict.OfBlob.Get(3, out ByteString v3));
        Assert.Equal(Bs(3, 3, 3), v3);

        Assert.True(dict.TryGetValueKind(2, out var k));
        Assert.Equal(ValueKind.Blob, k);
    }

    [Fact]
    public void MixedOrderedDict_Blob_MixedTypes_RoundTrip() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();

        dict.OfBlob.Upsert(1, Bs(0xAA));
        dict.Upsert(2, 12345);
        dict.OfString.Upsert(3, "hello");
        dict.OfBlob.Upsert(4, Bs(0xBB, 0xCC));

        Assert.Equal(GetIssue.None, dict.OfBlob.Get(1, out ByteString b1));
        Assert.Equal(Bs(0xAA), b1);
        Assert.Equal(GetIssue.None, dict.Get<int>(2, out int i2));
        Assert.Equal(12345, i2);
        Assert.Equal(GetIssue.None, dict.OfString.Get(3, out string? s3));
        Assert.Equal("hello", s3);
        Assert.Equal(GetIssue.None, dict.OfBlob.Get(4, out ByteString b4));
        Assert.Equal(Bs(0xBB, 0xCC), b4);

        // 跨类型 typed read 应返回 TypeMismatch
        Assert.Equal(GetIssue.TypeMismatch, dict.OfBlob.Get(2, out ByteString _));
        Assert.Equal(GetIssue.TypeMismatch, dict.OfString.Get(1, out string? _));
    }

    [Fact]
    public void MixedOrderedDict_Blob_Commit_Open_RoundTrip() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();
        dict.OfBlob.Upsert(1, Bs(0x01, 0x02));
        dict.OfBlob.Upsert(2, ByteString.Empty);
        dict.OfBlob.Upsert(3, Bs(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));

        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file));

        var opened = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(opened.IsSuccess, $"Open failed: {opened.Error}");

        var loaded = Assert.IsAssignableFrom<DurableOrderedDict<int>>(opened.Value!.GraphRoot);
        Assert.Equal(GetIssue.None, loaded.OfBlob.Get(1, out ByteString v1));
        Assert.Equal(Bs(0x01, 0x02), v1);
        Assert.Equal(GetIssue.None, loaded.OfBlob.Get(2, out ByteString v2));
        Assert.True(v2.IsEmpty);
        Assert.Equal(GetIssue.None, loaded.OfBlob.Get(3, out ByteString v3));
        Assert.Equal(64, v3.Length);
    }
}
