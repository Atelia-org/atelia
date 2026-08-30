using System.Security.Cryptography;
using Atelia.MemoPod;

namespace Atelia.MemoPod.Tests.Reconciliation;

public sealed class MemoPodReconciliationTests : IDisposable {
    private static readonly MemoPodId PodId = MemoPodId.Parse(
        "77777777777777777777777777777777"
    );

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-memo-pod-reconciliation-tests",
        Guid.NewGuid().ToString("N")
    );

    public MemoPodReconciliationTests() {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task StateIdentityUsesExactCanonicalDocumentBytes() {
        MemoPod pod = MemoPod.Create(_root, PodId, "customer details");
        MemoId first = pod.Append(
            "order 17 ships Friday",
            title: "Order 17",
            gist: "Ships Friday",
            summary: "The customer expects Friday shipment."
        );
        MemoId second = pod.Append("prefers email");
        byte[] canonical = MemoPodDocumentCodec.Encode(new MemoPodDocument(
            PodId,
            "customer details",
            nextMemoOrdinal: 3,
            [
                new Memo(
                    first,
                    "order 17 ships Friday",
                    "Order 17",
                    "Ships Friday",
                    "The customer expects Friday shipment."
                ),
                new Memo(second, "prefers email")
            ]
        ));
        string expected = MemoPod.StateIdentityPrefix
            + Convert.ToHexStringLower(SHA256.HashData(canonical));

        string editable = pod.ComputeStateIdentity();
        await pod.FreezeAsync();
        string frozen = pod.ComputeStateIdentity();
        MemoPod reopened = MemoPod.Open(_root, PodId);

        Assert.Equal(expected, editable);
        Assert.Equal(expected, frozen);
        Assert.Equal(expected, reopened.ComputeStateIdentity());
        Assert.StartsWith(
            "atelia.memo-pod.document.v2.sha256:",
            expected,
            StringComparison.Ordinal
        );
        Assert.Matches(
            "^atelia[.]memo-pod[.]document[.]v2[.]sha256:[0-9a-f]{64}$",
            expected
        );
    }

    [Fact]
    public void AllocatorHighWaterChangesIdentityWhenListIsEqual() {
        MemoPod pod = MemoPod.Create(_root, PodId, "topic");
        string before = pod.ComputeStateIdentity();
        MemoId provisional = pod.Append("temporary");
        pod.Remove(provisional);

        string after = pod.ComputeStateIdentity();

        Assert.Empty(pod.List());
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void EveryCanonicalDocumentFieldParticipatesInIdentity() {
        string baseline = CandidateIdentity(
            "11111111111111111111111111111111",
            "topic",
            "text",
            "title",
            "gist",
            "summary"
        );

        Assert.NotEqual(baseline, CandidateIdentity(
            "22222222222222222222222222222222",
            "topic",
            "text",
            "title",
            "gist",
            "summary"
        ));
        Assert.NotEqual(baseline, CandidateIdentity(
            "11111111111111111111111111111111",
            "other topic",
            "text",
            "title",
            "gist",
            "summary"
        ));
        Assert.NotEqual(baseline, CandidateIdentity(
            "11111111111111111111111111111111",
            "topic",
            "other text",
            "title",
            "gist",
            "summary"
        ));
        Assert.NotEqual(baseline, CandidateIdentity(
            "11111111111111111111111111111111",
            "topic",
            "text",
            null,
            "gist",
            "summary"
        ));
        Assert.NotEqual(baseline, CandidateIdentity(
            "11111111111111111111111111111111",
            "topic",
            "text",
            "title",
            "other gist",
            "summary"
        ));
        Assert.NotEqual(baseline, CandidateIdentity(
            "11111111111111111111111111111111",
            "topic",
            "text",
            "title",
            "gist",
            "other summary"
        ));
    }

    [Fact]
    public async Task IdentityIsReadOnlyAcrossEditableAndFrozenPhases() {
        MemoPod pod = MemoPod.Create(_root, PodId, "topic");

        string emptyCandidate = pod.ComputeStateIdentity();

        Assert.Equal(MemoPodPhase.Editable, pod.Phase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        pod.Append("memo");
        string target = pod.ComputeStateIdentity();
        Assert.NotEqual(emptyCandidate, target);
        Assert.Equal(MemoPodPhase.Editable, pod.Phase);

        await pod.FreezeAsync();
        Assert.Equal(target, pod.ComputeStateIdentity());
        pod.ResumeEditing();
        Assert.Equal(MemoPodPhase.Editable, pod.Phase);
        Assert.Equal(target, pod.ComputeStateIdentity());
    }

    [Fact]
    public async Task DurabilityConfirmationRequiresFrozenCurrentDocument() {
        MemoPod pod = MemoPod.Create(_root, PodId, "topic");
        pod.Append("memo");
        Assert.Throws<InvalidOperationException>(
            () => pod.ConfirmCurrentDocumentDurability()
        );
        await pod.FreezeAsync();
        byte[] before = File.ReadAllBytes(
            MemoPodStoreLayout.Resolve(_root, PodId).DocumentPath
        );

        pod.ConfirmCurrentDocumentDurability();

        Assert.Equal(MemoPodPhase.Frozen, pod.Phase);
        Assert.Equal(
            before,
            File.ReadAllBytes(
                MemoPodStoreLayout.Resolve(_root, PodId).DocumentPath
            )
        );
    }

    [Fact]
    public async Task DurabilityConfirmationMapsMissingCurrentDocument() {
        MemoPod pod = MemoPod.Create(_root, PodId, "topic");
        pod.Append("memo");
        await pod.FreezeAsync();
        File.Delete(MemoPodStoreLayout.Resolve(_root, PodId).DocumentPath);

        MemoPodPersistenceException failure = Assert.Throws<
            MemoPodPersistenceException>(
                () => pod.ConfirmCurrentDocumentDurability()
            );

        Assert.Equal(
            MemoPodPersistenceFailureKind.NotFound,
            failure.FailureKind
        );
    }

    [Fact]
    public async Task FreshOpenCanConfirmInstalledIndeterminateTarget() {
        MemoPod pod = MemoPod.CreateForTesting(
            _root,
            PodId,
            "topic",
            new MemoPodLifecycleTestHooks(
                PublisherHooks: new MemoPodPublisherTestHooks(
                    AfterInstallBeforeDirectoryFsync: _ =>
                        throw new IOException("indeterminate fixture")
                )
            )
        );
        pod.Append("installed target");
        await Assert.ThrowsAsync<MemoPodCommitIndeterminateException>(
            () => pod.FreezeAsync()
        );

        Assert.Throws<MemoPodInvalidatedException>(
            () => pod.ComputeStateIdentity()
        );
        Assert.Throws<MemoPodInvalidatedException>(
            () => pod.ConfirmCurrentDocumentDurability()
        );

        MemoPod reopened = MemoPod.Open(_root, PodId);
        string identity = reopened.ComputeStateIdentity();
        reopened.ConfirmCurrentDocumentDurability();

        Assert.StartsWith(MemoPod.StateIdentityPrefix, identity);
        Assert.Equal(
            "installed target",
            Assert.Single(reopened.List()).ExactText
        );
    }

    private string CandidateIdentity(
        string podId,
        string topic,
        string exactText,
        string? title,
        string? gist,
        string? summary
    ) {
        string root = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MemoPod pod = MemoPod.Create(
            root,
            MemoPodId.Parse(podId),
            topic
        );
        pod.Append(exactText, title, gist, summary);
        return pod.ComputeStateIdentity();
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }
}
