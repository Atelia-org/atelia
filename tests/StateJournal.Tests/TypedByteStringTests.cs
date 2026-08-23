using System.Buffers;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Serialization;
using Xunit;

namespace Atelia.StateJournal.Tests;

public class TypedByteStringTests {
    private static ByteString Bs(params byte[] bytes) => new(bytes);

    [Fact]
    public void Helper_UsesContentEqualityHashAndUnsignedLexicographicComparison() {
        ByteString a = Bs(0x01, 0x80);
        ByteString equal = Bs(0x01, 0x80);
        ByteString prefix = Bs(0x01);
        ByteString lowerUnsigned = Bs(0x01, 0x7F);

        Assert.True(ByteStringHelper.Equals(a, equal));
        Assert.Equal(ByteStringHelper.GetHashCode(a), ByteStringHelper.GetHashCode(equal));
        Assert.Equal(0, ByteStringHelper.Compare(a, equal));
        Assert.True(ByteStringHelper.Compare(prefix, a) < 0);
        Assert.True(ByteStringHelper.Compare(lowerUnsigned, a) < 0);
        Assert.True(ByteStringHelper.Compare(ByteString.Empty, prefix) < 0);

        Assert.Equal(0, a.CompareTo(equal));
        Assert.True(prefix.CompareTo(a) < 0);
        Assert.True(lowerUnsigned.CompareTo(a) < 0);
    }

    [Fact]
    public void Helper_WireRoundTripAndEstimate_AreExactAcrossVarUIntBoundaries() {
        foreach (int length in new[] { 0, 1, 127, 128, 16_383, 16_384 }) {
            byte[] bytes = new byte[length];
            for (int i = 0; i < bytes.Length; i++) { bytes[i] = unchecked((byte)i); }
            ByteString value = new(bytes);

            var buffer = new ArrayBufferWriter<byte>();
            var writer = new BinaryDiffWriter(buffer);
            ByteStringHelper.Write(writer, value, asKey: true);

            Assert.Equal(ByteStringHelper.EstimateBareSize(value, asKey: true), (uint)buffer.WrittenCount);

            var reader = new BinaryDiffReader(buffer.WrittenSpan);
            ByteString roundTripped = ByteStringHelper.Read(ref reader, asKey: true);
            Assert.Equal(value, roundTripped);
            reader.EnsureFullyConsumed();

            ByteString updated = Bs(0xFF);
            var updateReader = new BinaryDiffReader(buffer.WrittenSpan);
            ByteStringHelper.UpdateOrInit(ref updateReader, ref updated);
            Assert.Equal(value, updated);
            updateReader.EnsureFullyConsumed();
        }
    }

    [Fact]
    public void ValueTuple2Through7_EstimatesEqualTheirSerializedBareSizes() {
        ByteString a = ByteString.Empty;
        ByteString b = Bs(0x01);
        ByteString c = Bs(0x02, 0x03);
        ByteString d = new(new byte[127]);
        ByteString e = new(new byte[128]);
        ByteString f = Bs(0xFE, 0xFF);
        ByteString g = Bs(0x80);

        AssertExactEstimate<(ByteString, ByteString),
            ValueTuple2Helper<ByteString, ByteString, ByteStringHelper, ByteStringHelper>>((a, b));
        AssertExactEstimate<(ByteString, ByteString, ByteString),
            ValueTuple3Helper<ByteString, ByteString, ByteString, ByteStringHelper, ByteStringHelper, ByteStringHelper>>((a, b, c));
        AssertExactEstimate<(ByteString, ByteString, ByteString, ByteString),
            ValueTuple4Helper<ByteString, ByteString, ByteString, ByteString,
                ByteStringHelper, ByteStringHelper, ByteStringHelper, ByteStringHelper>>((a, b, c, d));
        AssertExactEstimate<(ByteString, ByteString, ByteString, ByteString, ByteString),
            ValueTuple5Helper<ByteString, ByteString, ByteString, ByteString, ByteString,
                ByteStringHelper, ByteStringHelper, ByteStringHelper, ByteStringHelper, ByteStringHelper>>((a, b, c, d, e));
        AssertExactEstimate<(ByteString, ByteString, ByteString, ByteString, ByteString, ByteString),
            ValueTuple6Helper<ByteString, ByteString, ByteString, ByteString, ByteString, ByteString,
                ByteStringHelper, ByteStringHelper, ByteStringHelper, ByteStringHelper, ByteStringHelper, ByteStringHelper>>((a, b, c, d, e, f));
        AssertExactEstimate<(ByteString, ByteString, ByteString, ByteString, ByteString, ByteString, ByteString),
            ValueTuple7Helper<ByteString, ByteString, ByteString, ByteString, ByteString, ByteString, ByteString,
                ByteStringHelper, ByteStringHelper, ByteStringHelper, ByteStringHelper,
                ByteStringHelper, ByteStringHelper, ByteStringHelper>>((a, b, c, d, e, f, g));
    }

    [Fact]
    public void RegistryAndTypeCode_ExposeByteStringAsFullTypedScalar() {
        TypeEntry keyEntry = HelperRegistry.ResolveKeyHelper(typeof(ByteString));
        Assert.True(keyEntry.IsValid);
        Assert.Equal(typeof(ByteStringHelper), keyEntry.HelperType);
        Assert.Equal(new byte[] { 17 }, keyEntry.TypeCode);
        Assert.Equal(17, (byte)TypeOpCode.PushByteString);

        Type composite = typeof(DurableDict<(ByteString, int), DurableDeque<ByteString>>);
        TypeEntry valueEntry = HelperRegistry.ResolveValueHelper(composite);
        Assert.True(valueEntry.IsValid);
        Assert.True(TypeCodec.TryDecode(valueEntry.TypeCode, out Type? decoded));
        Assert.Equal(composite, decoded);
    }

    [Fact]
    public void TypedContainers_ByteString_CreateCommitForkAndReopenThroughPublicRepositoryApi() {
        string repoDir = Path.Combine(Path.GetTempPath(), $"state-journal-typed-bytestring-{Guid.NewGuid():N}");

        try {
            AteliaResult<Repository> createResult = Repository.Create(repoDir);
            Assert.True(createResult.IsSuccess, $"Repository.Create failed: {createResult.Error}");

            using (Repository repo = createResult.Value!) {
                AteliaResult<Revision> branchResult = repo.CreateBranch("main");
                Assert.True(branchResult.IsSuccess, $"CreateBranch failed: {branchResult.Error}");
                Revision revision = branchResult.Value!;
                DurableDict<string> root = revision.CreateDict<string>();

                DurableDict<ByteString, ByteString> dict = revision.CreateDict<ByteString, ByteString>();
                Assert.Equal(UpsertStatus.Inserted, dict.Upsert(Bs(0x10), Bs(0xA0)));
                Assert.Equal(UpsertStatus.Updated, dict.Upsert(Bs(0x10), Bs(0xA1)));
                Assert.Equal(1, dict.Count);
                dict.Upsert(ByteString.Empty, ByteString.Empty);

                DurableDeque<ByteString> deque = revision.CreateDeque<ByteString>();
                deque.PushBack(ByteString.Empty);
                deque.PushBack(Bs(0xDE, 0xAD));
                deque.PushBack(Bs(0xDE, 0xAD));

                DurableOrderedDict<ByteString, ByteString> ordered = revision.CreateOrderedDict<ByteString, ByteString>();
                foreach (ByteString key in new[] { Bs(0xFF), Bs(0x01, 0x00), Bs(0x80), Bs(0x01), Bs(0x7F), ByteString.Empty }) {
                    ordered.Upsert(key, key);
                }

                DurableHashSet<ByteString> set = revision.CreateHashSet<ByteString>();
                Assert.True(set.Add(ByteString.Empty));
                Assert.True(set.Add(Bs(0x44, 0x55)));
                Assert.False(set.Add(Bs(0x44, 0x55)));

                DurableDict<(ByteString, int), (ByteString, ByteString)> tupleDict =
                    revision.CreateDict<(ByteString, int), (ByteString, ByteString)>();
                Assert.Equal(UpsertStatus.Inserted, tupleDict.Upsert((Bs(0x22), 7), (ByteString.Empty, Bs(0x33))));
                Assert.Equal(UpsertStatus.Updated, tupleDict.Upsert((Bs(0x22), 7), (Bs(0x34), Bs(0x35))));
                Assert.Equal(1, tupleDict.Count);

                root.Upsert("dict", dict);
                root.Upsert("deque", deque);
                root.Upsert("ordered", ordered);
                root.Upsert("set", set);
                root.Upsert("tuple", tupleDict);

                AteliaResult<CommitAddress> firstCommit = repo.Commit(root);
                Assert.True(firstCommit.IsSuccess, $"First commit failed: {firstCommit.Error}");
                Assert.False(deque.HasChanges);

                Assert.True(deque.TrySetAt(1, Bs(0xDE, 0xAD)));
                Assert.False(deque.HasChanges);

                DurableDeque<ByteString> fork = deque.ForkCommittedAsMutable();
                fork.PushBack(Bs(0xBE, 0xEF));
                root.Upsert("fork", fork);

                AteliaResult<CommitAddress> secondCommit = repo.Commit(root);
                Assert.True(secondCommit.IsSuccess, $"Second commit failed: {secondCommit.Error}");
            }

            AteliaResult<Repository> openResult = Repository.Open(repoDir);
            Assert.True(openResult.IsSuccess, $"Repository.Open failed: {openResult.Error}");
            using Repository reopenedRepo = openResult.Value!;

            AteliaResult<Revision> checkoutResult = reopenedRepo.CheckoutBranch("main");
            Assert.True(checkoutResult.IsSuccess, $"CheckoutBranch failed: {checkoutResult.Error}");
            DurableDict<string> reopenedRoot = Assert.IsAssignableFrom<DurableDict<string>>(checkoutResult.Value!.GraphRoot);

            DurableDict<ByteString, ByteString> reopenedDict =
                reopenedRoot.GetOrThrow<DurableDict<ByteString, ByteString>>("dict")!;
            Assert.Equal(2, reopenedDict.Count);
            Assert.Equal(Bs(0xA1), reopenedDict.GetOrThrow(Bs(0x10)));
            Assert.Equal(ByteString.Empty, reopenedDict.GetOrThrow(ByteString.Empty));

            DurableDeque<ByteString> reopenedDeque = reopenedRoot.GetOrThrow<DurableDeque<ByteString>>("deque")!;
            Assert.Equal(3, reopenedDeque.Count);
            Assert.Equal(GetIssue.None, reopenedDeque.GetAt(0, out ByteString empty));
            Assert.Equal(ByteString.Empty, empty);
            Assert.Equal(GetIssue.None, reopenedDeque.GetAt(1, out ByteString firstDuplicate));
            Assert.Equal(GetIssue.None, reopenedDeque.GetAt(2, out ByteString secondDuplicate));
            Assert.Equal(Bs(0xDE, 0xAD), firstDuplicate);
            Assert.Equal(firstDuplicate, secondDuplicate);
            Assert.NotSame(firstDuplicate.DangerousGetUnderlyingArray(), secondDuplicate.DangerousGetUnderlyingArray());

            DurableOrderedDict<ByteString, ByteString> reopenedOrdered =
                reopenedRoot.GetOrThrow<DurableOrderedDict<ByteString, ByteString>>("ordered")!;
            Assert.Equal(
                new[] { ByteString.Empty, Bs(0x01), Bs(0x01, 0x00), Bs(0x7F), Bs(0x80), Bs(0xFF) },
                reopenedOrdered.GetKeys()
            );

            DurableHashSet<ByteString> reopenedSet = reopenedRoot.GetOrThrow<DurableHashSet<ByteString>>("set")!;
            Assert.Equal(2, reopenedSet.Count);
            Assert.True(reopenedSet.Contains(ByteString.Empty));
            Assert.True(reopenedSet.Contains(Bs(0x44, 0x55)));

            DurableDict<(ByteString, int), (ByteString, ByteString)> reopenedTuple =
                reopenedRoot.GetOrThrow<DurableDict<(ByteString, int), (ByteString, ByteString)>>("tuple")!;
            Assert.Equal(GetIssue.None, reopenedTuple.Get((Bs(0x22), 7), out var tupleValue));
            Assert.Equal((Bs(0x34), Bs(0x35)), tupleValue);

            DurableDeque<ByteString> reopenedFork = reopenedRoot.GetOrThrow<DurableDeque<ByteString>>("fork")!;
            Assert.Equal(4, reopenedFork.Count);
            Assert.Equal(GetIssue.None, reopenedFork.GetAt(3, out ByteString forkTail));
            Assert.Equal(Bs(0xBE, 0xEF), forkTail);
        }
        finally {
            try {
                if (Directory.Exists(repoDir)) { Directory.Delete(repoDir, recursive: true); }
            }
            catch {
            }
        }
    }

    private static void AssertExactEstimate<T, THelper>(T value)
        where T : notnull
        where THelper : unmanaged, ITypeHelper<T> {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        THelper.Write(writer, value, asKey: false);
        Assert.Equal(THelper.EstimateBareSize(value, asKey: false), (uint)buffer.WrittenCount);
    }
}
