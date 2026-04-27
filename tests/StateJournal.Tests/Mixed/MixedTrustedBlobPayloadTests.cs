using System.Linq;
using System.Runtime.CompilerServices;
using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;
using Xunit;

namespace Atelia.StateJournal.Tests;

// ai:impl `src/StateJournal/MixedValueCatalog.cs` (ByteString entry SupportsTrustedFromCallerOwnedBuffer = true)
// ai:impl `src/StateJournal/Internal/ValueBox.cs` (ITrustedTypedFace<T>)
// ai:impl `src/StateJournal/Internal/ValueBox.Blob.cs` (BlobPayloadFace : ITrustedTypedFace<ByteString>)
// ai:impl `src/StateJournal/DurableDict.Mixed.cs` (UpsertCoreTrusted)
// ai:impl `src/StateJournal/DurableDeque.Mixed.cs` (PushCoreTrusted / TrySetCoreTrusted / TrySetAtCoreTrusted)
// ai:impl `src/StateJournal/DurableOrderedDict.Mixed.cs` (UpsertCoreTrusted abstract)
// ai:impl `src/StateJournal/Internal/MixedOrderedDictImpl.cs` (UpsertCoreTrusted override)
// ai:impl `src/StateJournal.Generators/MixedValueContainerGenerator.cs` (EmitDictTrustedOverloads / EmitDequeTrustedOverloads)
//
// CMS Step E：mixed 容器 trusted 路径端到端测试。
// 关键契约：
//   - opt-in：仅 ByteString 在 catalog 标 SupportsTrustedFromCallerOwnedBuffer = true，generator 仅为之生成
//     UpsertTrustedBlob / PushFrontTrustedBlob 等公开 overload；其他类型（int/string/Symbol 等）无 trusted overload。
//   - 零拷贝：UpsertTrustedBlob(ByteString.FromTrustedOwned(arr)) 后，pool 内 byte[] 与 arr 同 ref（external mutation
//     可见到 pool / Get 返回值）。与默认 Upsert(new ByteString(arr)) 双层 clone 路径形成对照。
//   - CMS Step 1 snapshot 契约：trusted 路径下 oldBareBytesBeforeMutation 仍正确，在任何 mutation 前 capture。
//     通过覆写 + commit + reopen 间接验证（若 dirty 估算被破坏，reopen 内容会失真）。
partial class RevisionTests {

    /// <summary>从 dict 中 Get blob 后取其底层 byte[] 引用（Get 走 ByteString.FromTrustedOwned 包装 pool slot）。</summary>
    private static byte[] GetUnderlyingArray(DurableDict<int> dict, int key) {
        Assert.Equal(GetIssue.None, dict.OfBlob.Get(key, out ByteString bs));
        return bs.DangerousGetUnderlyingArray();
    }

    private static byte[] GetUnderlyingArray(DurableDeque deque, int index) {
        Assert.Equal(GetIssue.None, deque.OfBlob.GetAt(index, out ByteString bs));
        return bs.DangerousGetUnderlyingArray();
    }

    private static byte[] GetUnderlyingArray(DurableOrderedDict<int> dict, int key) {
        Assert.Equal(GetIssue.None, dict.OfBlob.Get(key, out ByteString bs));
        return bs.DangerousGetUnderlyingArray();
    }

    // ═══════════════════════ MixedDict<int> ═══════════════════════

    [Fact]
    public void MixedDict_UpsertTrustedBlob_NewKey_AllocatesPoolSlot() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        int ownedBefore = ValuePools.OfOwnedBlob.Count;
        var status = dict.UpsertTrustedBlob(1, ByteString.FromTrustedOwned([1, 2, 3]));
        Assert.Equal(UpsertStatus.Inserted, status);
        Assert.Equal(ownedBefore + 1, ValuePools.OfOwnedBlob.Count);

        Assert.True(dict.TryGetValueKind(1, out var kind));
        Assert.Equal(ValueKind.Blob, kind);
        Assert.Equal(GetIssue.None, dict.OfBlob.Get(1, out ByteString got));
        Assert.Equal(new byte[] { 1, 2, 3 }, got.AsSpan().ToArray());
    }

    [Fact]
    public void MixedDict_UpsertTrustedBlob_ZeroCopy_PoolSharesByteArray() {
        // 关键契约：trusted 路径直接复用 caller buffer，pool 内 byte[] 与 external 同 ref。
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        byte[] arrTrusted = [10, 20, 30];
        dict.UpsertTrustedBlob(1, ByteString.FromTrustedOwned(arrTrusted));
        byte[] poolArr = GetUnderlyingArray(dict, 1);
        Assert.Same(arrTrusted, poolArr); // ★ 零拷贝

        // 反证：默认 Upsert（face 层 defensive clone + ctor 层 defensive clone）→ pool byte[] 与 external 不同 ref。
        byte[] arrDefault = [40, 50, 60];
        dict.Upsert(2, new ByteString(arrDefault));
        byte[] poolArrDefault = GetUnderlyingArray(dict, 2);
        Assert.NotSame(arrDefault, poolArrDefault);
        Assert.Equal(new byte[] { 40, 50, 60 }, poolArrDefault);
    }

    [Fact]
    public void MixedDict_UpsertTrustedBlob_OverwriteExisting_NoOpSameContent() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        byte[] first = [1, 2, 3];
        dict.UpsertTrustedBlob(1, ByteString.FromTrustedOwned(first));
        byte[] poolArrBefore = GetUnderlyingArray(dict, 1);
        Assert.Same(first, poolArrBefore);
        int ownedAfterFirst = ValuePools.OfOwnedBlob.Count;

        // 同内容 → no-op：pool slot 不变，本次传入数组未被接管（generator/face 契约 doc 已说明）。
        byte[] sameContent = [1, 2, 3];
        var status = dict.UpsertTrustedBlob(1, ByteString.FromTrustedOwned(sameContent));
        Assert.Equal(UpsertStatus.Updated, status);
        Assert.Equal(ownedAfterFirst, ValuePools.OfOwnedBlob.Count);
        byte[] poolArrAfter = GetUnderlyingArray(dict, 1);
        Assert.Same(first, poolArrAfter); // 仍是首次的 trusted 数组；no-op 路径不替换。
    }

    [Fact]
    public void MixedDict_UpsertTrustedBlob_OverwriteExisting_InplaceDifferentContent() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        byte[] first = [1, 2, 3];
        dict.UpsertTrustedBlob(1, ByteString.FromTrustedOwned(first));
        int ownedAfterFirst = ValuePools.OfOwnedBlob.Count;

        // 不同内容 → inplace overwrite：复用同一个 pool slot，slot 内容指针替换为新的 trusted 数组。
        byte[] second = [9, 8, 7, 6];
        var status = dict.UpsertTrustedBlob(1, ByteString.FromTrustedOwned(second));
        Assert.Equal(UpsertStatus.Updated, status);
        Assert.Equal(ownedAfterFirst, ValuePools.OfOwnedBlob.Count); // pool slot 复用，无新增
        byte[] poolArr = GetUnderlyingArray(dict, 1);
        Assert.Same(second, poolArr); // ★ inplace 仍零拷贝

        Assert.Equal(GetIssue.None, dict.OfBlob.Get(1, out ByteString got));
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, got.AsSpan().ToArray());
    }

    [Fact]
    public void MixedDict_UpsertTrustedBlob_LargeBlob_RoundTripAfterReopen() {
        // 1MB blob 零拷贝 + commit + reopen，验证 trusted 路径与 CMS Step 1 snapshot 契约协作良好。
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        byte[] big = new byte[1024 * 1024];
        for (int i = 0; i < big.Length; i++) { big[i] = (byte)(i & 0xFF); }
        dict.UpsertTrustedBlob(42, ByteString.FromTrustedOwned(big));
        Assert.Same(big, GetUnderlyingArray(dict, 42));

        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit1");
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var dict2 = Assert.IsAssignableFrom<DurableDict<int>>(opened.GraphRoot);
        Assert.Equal(GetIssue.None, dict2.OfBlob.Get(42, out ByteString got));
        Assert.Equal(big.Length, got.Length);
        Assert.True(got.AsSpan().SequenceEqual(big));
    }

    [Fact]
    public void MixedDict_UpsertTrustedBlob_FrozenThrows() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();
        dict.UpsertTrustedBlob(1, ByteString.FromTrustedOwned([1]));
        dict.Freeze();

        Assert.True(dict.IsFrozen);
        Assert.Throws<ObjectFrozenException>(() => dict.UpsertTrustedBlob(2, ByteString.FromTrustedOwned([2])));
        Assert.Throws<ObjectFrozenException>(() => dict.UpsertTrustedBlob(1, ByteString.FromTrustedOwned([3])));
    }

    [Fact]
    public void MixedDict_UpsertTrustedBlob_SnapshotContractPreserved_DirtyEstimateAcrossOverwrite() {
        // CMS Step 1 snapshot 契约：trusted 路径下 oldBareBytesBeforeMutation 在任何 mutation 前 capture。
        // 间接验证手段：连续多次不同长度的 trusted 覆写后 commit + reopen，内容必须精确一致；
        // 若 estimate 在 inplace overwrite 中读到 mutation 后的状态会让 dirty bytes 漂移、commit 出错。
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        dict.UpsertTrustedBlob(1, ByteString.FromTrustedOwned([1]));
        dict.UpsertTrustedBlob(1, ByteString.FromTrustedOwned([2, 2, 2, 2, 2, 2, 2, 2, 2]));
        dict.UpsertTrustedBlob(1, ByteString.FromTrustedOwned([0xAA]));
        dict.UpsertTrustedBlob(1, ByteString.FromTrustedOwned([]));
        dict.UpsertTrustedBlob(1, ByteString.FromTrustedOwned([0xFF, 0xEE]));

        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit1");
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var dict2 = Assert.IsAssignableFrom<DurableDict<int>>(opened.GraphRoot);
        Assert.Equal(GetIssue.None, dict2.OfBlob.Get(1, out ByteString got));
        Assert.Equal(new byte[] { 0xFF, 0xEE }, got.AsSpan().ToArray());
    }

    // ═══════════════════════ MixedDeque ═══════════════════════

    [Fact]
    public void MixedDeque_PushBackTrustedBlob_ZeroCopy_PoolSharesByteArray() {
        var rev = CreateRevision();
        var deque = rev.CreateDeque();

        byte[] arrTrusted = [11, 22, 33];
        deque.PushBackTrustedBlob(ByteString.FromTrustedOwned(arrTrusted));
        Assert.Same(arrTrusted, GetUnderlyingArray(deque, 0));

        // 反证：默认 PushBack 走 defensive clone。
        byte[] arrDefault = [44, 55];
        deque.PushBack(new ByteString(arrDefault));
        Assert.NotSame(arrDefault, GetUnderlyingArray(deque, 1));
    }

    [Fact]
    public void MixedDeque_TrySetAtTrustedBlob_InplaceZeroCopy() {
        var rev = CreateRevision();
        var deque = rev.CreateDeque();
        deque.PushBackTrustedBlob(ByteString.FromTrustedOwned([1, 2, 3]));
        int ownedAfterPush = ValuePools.OfOwnedBlob.Count;

        byte[] replacement = [9, 9, 9, 9];
        Assert.True(deque.TrySetAtTrustedBlob(0, ByteString.FromTrustedOwned(replacement)));
        Assert.Equal(ownedAfterPush, ValuePools.OfOwnedBlob.Count); // inplace
        Assert.Same(replacement, GetUnderlyingArray(deque, 0));

        // out-of-range 安全返回 false（与默认 TrySetAt 一致）。
        Assert.False(deque.TrySetAtTrustedBlob(99, ByteString.FromTrustedOwned([0])));
    }

    [Fact]
    public void MixedDeque_TrySetFrontBackTrustedBlob_InplaceZeroCopy() {
        var rev = CreateRevision();
        var deque = rev.CreateDeque();
        deque.PushBackTrustedBlob(ByteString.FromTrustedOwned([1]));
        deque.PushBackTrustedBlob(ByteString.FromTrustedOwned([2]));

        byte[] newFront = [0xA0];
        byte[] newBack = [0xB0, 0xB1];
        Assert.True(deque.TrySetFrontTrustedBlob(ByteString.FromTrustedOwned(newFront)));
        Assert.True(deque.TrySetBackTrustedBlob(ByteString.FromTrustedOwned(newBack)));
        Assert.Same(newFront, GetUnderlyingArray(deque, 0));
        Assert.Same(newBack, GetUnderlyingArray(deque, 1));
    }

    // ═══════════════════════ MixedOrderedDict<int> ═══════════════════════

    [Fact]
    public void MixedOrderedDict_UpsertTrustedBlob_ZeroCopy_PoolSharesByteArray() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();

        byte[] arrTrusted = [7, 7, 7];
        var status = dict.UpsertTrustedBlob(1, ByteString.FromTrustedOwned(arrTrusted));
        Assert.Equal(UpsertStatus.Inserted, status);
        Assert.Same(arrTrusted, GetUnderlyingArray(dict, 1));

        // inplace overwrite 仍零拷贝。
        byte[] replacement = [8, 8];
        Assert.Equal(UpsertStatus.Updated, dict.UpsertTrustedBlob(1, ByteString.FromTrustedOwned(replacement)));
        Assert.Same(replacement, GetUnderlyingArray(dict, 1));
    }
}
