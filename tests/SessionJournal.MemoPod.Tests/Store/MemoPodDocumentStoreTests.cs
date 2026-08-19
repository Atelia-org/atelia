using Atelia.SessionJournal.MemoPod;
using System.Runtime.InteropServices;

namespace Atelia.SessionJournal.MemoPod.Tests.Store;

public sealed class MemoPodDocumentStoreTests : IDisposable {
    private static readonly MemoPodId PodId = MemoPodId.Parse(
        "55555555555555555555555555555555"
    );
    private static readonly MemoPodId OtherPodId = MemoPodId.Parse(
        "66666666666666666666666666666666"
    );

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-memo-pod-store-tests",
        Guid.NewGuid().ToString("N")
    );

    public MemoPodDocumentStoreTests() {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void CreatePublishesCanonicalDocumentAndStrictReadRoundTrips() {
        MemoPodDocument expected = Document("new");

        MemoPodPublishResult result = MemoPodDocumentPublisher.Publish(
            _root,
            expected,
            MemoPodPublishMode.CreateNew
        );
        MemoPodDocument actual = MemoPodDocumentStore.Read(_root, PodId);

        Assert.Equal(MemoPodPublishSettlement.Published, result.Settlement);
        Assert.Null(result.Failure);
        Assert.Equal(expected.PodId, actual.PodId);
        Assert.Equal(expected.Topic, actual.Topic);
        Assert.Equal(expected.NextMemoOrdinal, actual.NextMemoOrdinal);
        Assert.Equal(expected.Memos.ToArray(), actual.Memos.ToArray());
    }

    [Fact]
    public void CreateNoClobberReturnsNotPublishedAndPreservesAuthority() {
        PublishRequired(Document("old"), MemoPodPublishMode.CreateNew);

        MemoPodPublishResult result = MemoPodDocumentPublisher.Publish(
            _root,
            Document("new"),
            MemoPodPublishMode.CreateNew
        );

        Assert.Equal(
            MemoPodPublishSettlement.NotPublished,
            result.Settlement
        );
        Assert.Equal(
            MemoPodPublishFailureKind.TargetAlreadyExists,
            result.Failure?.Kind
        );
        Assert.Equal(
            "old",
            Assert.Single(MemoPodDocumentStore.Read(_root, PodId).Memos)
                .ExactText
        );
    }

    [Fact]
    public void ReplaceRequiresExistingAuthorityAndThenAtomicallyReplacesIt() {
        MemoPodPublishResult missing = MemoPodDocumentPublisher.Publish(
            _root,
            Document("new"),
            MemoPodPublishMode.ReplaceExisting
        );
        Assert.Equal(
            MemoPodPublishSettlement.NotPublished,
            missing.Settlement
        );
        Assert.Equal(
            MemoPodPublishFailureKind.TargetMissing,
            missing.Failure?.Kind
        );

        PublishRequired(Document("old"), MemoPodPublishMode.CreateNew);
        PublishRequired(Document("new"), MemoPodPublishMode.ReplaceExisting);

        Assert.Equal(
            "new",
            Assert.Single(MemoPodDocumentStore.Read(_root, PodId).Memos)
                .ExactText
        );
    }

    [Fact]
    public void BeforePublishFaultIsNotPublishedAndLeavesOldAuthority() {
        PublishRequired(Document("old"), MemoPodPublishMode.CreateNew);

        MemoPodPublishResult result = MemoPodDocumentPublisher.Publish(
            _root,
            Document("new"),
            MemoPodPublishMode.ReplaceExisting,
            new MemoPodPublisherTestHooks(
                BeforePublish: _ => throw new IOException("before publish")
            )
        );

        Assert.Equal(
            MemoPodPublishSettlement.NotPublished,
            result.Settlement
        );
        Assert.Equal(
            "old",
            Assert.Single(MemoPodDocumentStore.Read(_root, PodId).Memos)
                .ExactText
        );
        Assert.Empty(Directory.EnumerateFiles(
            MemoPodStoreLayout.Resolve(_root, PodId).PodsPath,
            "*.tmp"
        ));
    }

    [Fact]
    public void InstalledButUnsyncedFaultIsCommitIndeterminate() {
        PublishRequired(Document("old"), MemoPodPublishMode.CreateNew);

        MemoPodPublishResult result = MemoPodDocumentPublisher.Publish(
            _root,
            Document("new"),
            MemoPodPublishMode.ReplaceExisting,
            new MemoPodPublisherTestHooks(
                AfterInstallBeforeDirectoryFsync: _ =>
                    throw new IOException("after install")
            )
        );

        Assert.Equal(
            MemoPodPublishSettlement.CommitIndeterminate,
            result.Settlement
        );
        Assert.Equal(
            MemoPodPublishFailureKind.SettlementFailed,
            result.Failure?.Kind
        );
        Assert.Equal(
            "new",
            Assert.Single(MemoPodDocumentStore.Read(_root, PodId).Memos)
                .ExactText
        );
    }

    [Fact]
    public void PostSyncHookAndCleanupFaultsCannotReversePublished() {
        string? temporaryPath = null;

        MemoPodPublishResult result = MemoPodDocumentPublisher.Publish(
            _root,
            Document("new"),
            MemoPodPublishMode.CreateNew,
            new MemoPodPublisherTestHooks(
                BeforePublish: path => temporaryPath = path,
                AfterDirectoryFsync: _ => {
                    Directory.CreateDirectory(temporaryPath!);
                    throw new IOException("after fsync");
                }
            )
        );

        Assert.Equal(MemoPodPublishSettlement.Published, result.Settlement);
        Assert.IsType<AggregateException>(result.PostPublishDiagnostic);
        Assert.Equal(
            "new",
            Assert.Single(MemoPodDocumentStore.Read(_root, PodId).Memos)
                .ExactText
        );
    }

    [Fact]
    public void CancellationAtLastPreFenceCheckPropagatesAndDoesNotPublish() {
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() =>
            MemoPodDocumentPublisher.Publish(
                _root,
                Document("new"),
                MemoPodPublishMode.CreateNew,
                new MemoPodPublisherTestHooks(
                    BeforePublish: _ => cancellation.Cancel()
                ),
                cancellation.Token
            ));

        MemoPodStoreException absent = Assert.Throws<MemoPodStoreException>(
            () => MemoPodDocumentStore.Read(_root, PodId)
        );
        Assert.Equal(MemoPodStoreErrorCode.DocumentAbsent, absent.Code);
    }

    [Fact]
    public void TemporaryPathReplacedByLinkIsRejectedBeforeFence() {
        if (!OperatingSystem.IsLinux()) { return; }

        string external = Path.Combine(_root, "external.json");
        File.WriteAllText(external, "not authority");

        MemoPodPublishResult result = MemoPodDocumentPublisher.Publish(
            _root,
            Document("new"),
            MemoPodPublishMode.CreateNew,
            new MemoPodPublisherTestHooks(
                BeforePublish: temporary => {
                    File.Delete(temporary);
                    File.CreateSymbolicLink(temporary, external);
                }
            )
        );

        Assert.Equal(
            MemoPodPublishSettlement.NotPublished,
            result.Settlement
        );
        Assert.Equal(
            MemoPodPublishFailureKind.PreparationFailed,
            result.Failure?.Kind
        );
        Assert.Equal("not authority", File.ReadAllText(external));
        Assert.False(File.Exists(
            MemoPodStoreLayout.Resolve(_root, PodId).DocumentPath
        ));
    }

    [Fact]
    public void ReadUsesOnlyExactMappedFinalAndValidatesDocumentIdentity() {
        PublishRequired(Document("new"), MemoPodPublishMode.CreateNew);
        MemoPodStorePaths paths = MemoPodStoreLayout.Resolve(_root, PodId);
        File.Delete(paths.DocumentPath);
        File.WriteAllBytes(
            paths.DocumentPath,
            MemoPodDocumentCodec.Encode(new MemoPodDocument(
                OtherPodId,
                "topic",
                1,
                Array.Empty<Memo>()
            ))
        );
        File.WriteAllBytes(
            Path.Combine(paths.PodsPath, $".{PodId.Value}.orphan.tmp"),
            MemoPodDocumentCodec.Encode(Document("ignored"))
        );

        MemoPodStoreException mismatch = Assert.Throws<MemoPodStoreException>(
            () => MemoPodDocumentStore.Read(_root, PodId)
        );

        Assert.Equal(
            MemoPodStoreErrorCode.DocumentIdentityMismatch,
            mismatch.Code
        );
    }

    [Fact]
    public void ReadNeverPromotesOrphanTemporaryDocument() {
        MemoPodPublishResult prepared = MemoPodDocumentPublisher.Publish(
            _root,
            Document("new"),
            MemoPodPublishMode.CreateNew,
            new MemoPodPublisherTestHooks(
                BeforePublish: _ => throw new IOException("leave absent")
            )
        );
        Assert.Equal(
            MemoPodPublishSettlement.NotPublished,
            prepared.Settlement
        );
        MemoPodStorePaths paths = MemoPodStoreLayout.Resolve(_root, PodId);
        File.WriteAllBytes(
            Path.Combine(paths.PodsPath, $".{PodId.Value}.orphan.tmp"),
            MemoPodDocumentCodec.Encode(Document("orphan"))
        );

        MemoPodStoreException absent = Assert.Throws<MemoPodStoreException>(
            () => MemoPodDocumentStore.Read(_root, PodId)
        );

        Assert.Equal(MemoPodStoreErrorCode.DocumentAbsent, absent.Code);
    }

    [Fact]
    public void PublisherRequiresPreExistingCallerRoot() {
        string absentRoot = Path.Combine(_root, "absent");

        MemoPodPublishResult result = MemoPodDocumentPublisher.Publish(
            absentRoot,
            Document("new"),
            MemoPodPublishMode.CreateNew
        );

        Assert.Equal(
            MemoPodPublishSettlement.NotPublished,
            result.Settlement
        );
        Assert.Equal(
            MemoPodPublishFailureKind.PreparationFailed,
            result.Failure?.Kind
        );
        Assert.False(Directory.Exists(absentRoot));
    }

    [Fact]
    public void RootFixedChildrenAndFinalLinksAreRejected() {
        if (!OperatingSystem.IsLinux()) { return; }

        string external = Path.Combine(_root, "external");
        Directory.CreateDirectory(external);
        string linkedRoot = Path.Combine(_root, "linked-root");
        Directory.CreateSymbolicLink(linkedRoot, external);
        MemoPodPublishResult rootResult = MemoPodDocumentPublisher.Publish(
            linkedRoot,
            Document("root"),
            MemoPodPublishMode.CreateNew
        );
        Assert.Equal(
            MemoPodPublishSettlement.NotPublished,
            rootResult.Settlement
        );

        string fixedRoot = Path.Combine(_root, "fixed-root");
        Directory.CreateDirectory(fixedRoot);
        Directory.CreateSymbolicLink(
            Path.Combine(fixedRoot, "memo-pods"),
            external
        );
        MemoPodPublishResult fixedResult = MemoPodDocumentPublisher.Publish(
            fixedRoot,
            Document("fixed"),
            MemoPodPublishMode.CreateNew
        );
        Assert.Equal(
            MemoPodPublishSettlement.NotPublished,
            fixedResult.Settlement
        );

        PublishRequired(Document("old"), MemoPodPublishMode.CreateNew);
        MemoPodStorePaths paths = MemoPodStoreLayout.Resolve(_root, PodId);
        string externalDocument = Path.Combine(external, "external.json");
        File.Move(paths.DocumentPath, externalDocument);
        File.CreateSymbolicLink(paths.DocumentPath, externalDocument);
        MemoPodStoreException finalLink = Assert.Throws<MemoPodStoreException>(
            () => MemoPodDocumentStore.Read(_root, PodId)
        );
        Assert.Equal(MemoPodStoreErrorCode.PathLinkRejected, finalLink.Code);
    }

    [Fact]
    public void FinalSpecialFileShapeIsRejectedWithoutOpeningIt() {
        if (!OperatingSystem.IsLinux()) { return; }

        PublishRequired(Document("old"), MemoPodPublishMode.CreateNew);
        MemoPodStorePaths paths = MemoPodStoreLayout.Resolve(_root, PodId);
        File.Delete(paths.DocumentPath);
        Assert.Equal(0, NativeMkFifo(paths.DocumentPath, 0x180));

        MemoPodStoreException readFailure =
            Assert.Throws<MemoPodStoreException>(() =>
                MemoPodDocumentStore.Read(_root, PodId));
        MemoPodPublishResult replaceFailure =
            MemoPodDocumentPublisher.Publish(
                _root,
                Document("new"),
                MemoPodPublishMode.ReplaceExisting
            );

        Assert.Equal(
            MemoPodStoreErrorCode.PathShapeInvalid,
            readFailure.Code
        );
        Assert.Equal(
            MemoPodPublishSettlement.NotPublished,
            replaceFailure.Settlement
        );
        Assert.Equal(
            MemoPodPublishFailureKind.PreparationFailed,
            replaceFailure.Failure?.Kind
        );
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void PublishRequired(
        MemoPodDocument document,
        MemoPodPublishMode mode
    ) {
        MemoPodPublishResult result = MemoPodDocumentPublisher.Publish(
            _root,
            document,
            mode
        );
        Assert.Equal(MemoPodPublishSettlement.Published, result.Settlement);
    }

    private static MemoPodDocument Document(string exactText)
        => new(
            PodId,
            "customer details",
            2,
            [new Memo(MemoId.FromOrdinal(1), exactText)]
        );

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int NativeMkFifo(string path, uint mode);
}
