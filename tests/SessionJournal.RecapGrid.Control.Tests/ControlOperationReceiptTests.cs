using System.Globalization;
using System.Text;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Control.Tests;

public sealed partial class ControlVerticalTests {
    private static readonly string OperationRuntimeDigest = new('a', 64);

    [Fact]
    public void RegistrationOperationReplaysBeforeStaleAndConflictsExactly() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        var bundle = new RecapGridControlRegistrationBundle(
            [values.Family],
            [values.Definition],
            [new RecapGridControlRecipeRegistration(values.Recipe, null)]
        );
        RecapGridControlOperation operation =
            RecapGridControlOperation.Create(
                "session-operation-1",
                1,
                OperationRuntimeDigest
            );

        using RecapGridControlHandle handle = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            values.Admission
        )).Handle;
        RecapGridControlOperationResult.Applied applied = Assert.IsType<
            RecapGridControlOperationResult.Applied
        >(handle.Coordinator.ApplyRegistrationBundle(
            initial,
            values.TimelineHead,
            operation,
            bundle
        ));
        Assert.Equal(1, applied.Head.Generation);

        RecapGridControlOperationResult.Replayed replay = Assert.IsType<
            RecapGridControlOperationResult.Replayed
        >(handle.Coordinator.ApplyRegistrationBundle(
            initial,
            values.TimelineHead,
            operation,
            bundle
        ));
        Assert.Equal(applied.Head, replay.CurrentHead);
        Assert.Equal(applied.ResultIdentity, replay.ResultIdentity);
        Assert.False(replay.HeadAdvancedSinceApply);
        Assert.False(replay.InstanceReplaced);

        RecapGridControlOperation conflictingSequence =
            RecapGridControlOperation.Create(
                "session-operation-1",
                2,
                OperationRuntimeDigest
            );
        Assert.IsType<RecapGridControlOperationResult.Conflict>(
            handle.Coordinator.ApplyRegistrationBundle(
                initial,
                values.TimelineHead,
                conflictingSequence,
                bundle
            )
        );
        Assert.Equal(
            applied.Head,
            Assert.IsType<RecapGridControlSnapshotResult.Available>(
                handle.Reader.ReadSnapshot()
            ).Snapshot.Head
        );

        RecapGridControlOperation second = RecapGridControlOperation.Create(
            "session-operation-2",
            2,
            OperationRuntimeDigest
        );
        RecapGridControlOperationResult.Applied advanced = Assert.IsType<
            RecapGridControlOperationResult.Applied
        >(handle.Coordinator.ApplyRegistrationBundle(
            applied.Head,
            values.TimelineHead,
            second,
            bundle
        ));
        Assert.Equal(2, advanced.Head.Generation);
        replay = Assert.IsType<RecapGridControlOperationResult.Replayed>(
            handle.Coordinator.ApplyRegistrationBundle(
                initial,
                values.TimelineHead,
                operation,
                bundle
            )
        );
        Assert.True(replay.HeadAdvancedSinceApply);
        Assert.False(replay.InstanceReplaced);
        Assert.Equal(1, replay.OriginalGeneration);
        Assert.Equal(advanced.Head, replay.CurrentHead);
    }

    [Fact]
    public void RegistrationBundleFailurePublishesNeitherPrefixNorReceipt() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        MaintainerDefinitionRevision unauthorizedDefinition =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("case.unauthorized"),
                new FamilyDefinitionDigest(new string('f', 64)),
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "unauthorized"
                ),
                values.Definition.Capability,
                new MaintainerDeclarativeSpec(
                    "Unauthorized",
                    "Must not be partially registered."
                ),
                1024
            );
        var bundle = new RecapGridControlRegistrationBundle(
            [values.Family],
            [unauthorizedDefinition],
            []
        );
        string statePath = ControlStatePath(
            path,
            journal.BranchRefId,
            initial
        );
        byte[] before = File.ReadAllBytes(statePath);

        using RecapGridControlHandle handle = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            values.Admission
        )).Handle;
        Assert.IsType<RecapGridControlOperationResult.Unauthorized>(
            handle.Coordinator.ApplyRegistrationBundle(
                initial,
                values.TimelineHead,
                RecapGridControlOperation.Create(
                    "partial-bundle-must-not-publish",
                    1,
                    OperationRuntimeDigest
                ),
                bundle
            )
        );

        Assert.Equal(before, File.ReadAllBytes(statePath));
        Assert.Equal(
            initial,
            Assert.IsType<RecapGridControlSnapshotResult.Available>(
                handle.Reader.ReadSnapshot()
            ).Snapshot.Head
        );
    }

    [Fact]
    public void OperationReceiptSettlesAfterPublishedState() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        var afterPublish = new ControlPersistenceTestHooks(
            AfterStatePublish: static _ => throw new IOException(
                "injected after publish"
            )
        );
        using RecapGridControlHandle handle = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.OpenForTest(
            path,
            journal.BranchRefId,
            values.Admission,
            afterPublish
        )).Handle;
        RecapGridControlOperationResult.Applied settled = Assert.IsType<
            RecapGridControlOperationResult.Applied
        >(handle.Coordinator.ApplyRegistrationBundle(
            initial,
            values.TimelineHead,
            RecapGridControlOperation.Create(
                "settled-operation",
                1,
                OperationRuntimeDigest
            ),
            new RecapGridControlRegistrationBundle(
                [values.Family],
                [],
                []
            )
        ));
        Assert.Equal(1, settled.Head.Generation);
        Assert.Equal(
            settled.Head,
            Assert.IsType<RecapGridControlSnapshotResult.Available>(
                handle.Reader.ReadSnapshot()
            ).Snapshot.Head
        );
    }

    [Fact]
    public void PromotionOperationIsAtomicAndReplayable() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        using RecapGridControlHandle handle = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            values.Admission
        )).Handle;
        RecapGridControlOperationResult.Applied registered = Assert.IsType<
            RecapGridControlOperationResult.Applied
        >(handle.Coordinator.ApplyRegistrationBundle(
            initial,
            values.TimelineHead,
            RecapGridControlOperation.Create(
                "register-for-promotion",
                1,
                OperationRuntimeDigest
            ),
            new RecapGridControlRegistrationBundle(
                [values.Family],
                [values.Definition],
                [new RecapGridControlRecipeRegistration(
                    values.Recipe,
                    null
                )]
            )
        ));
        RecapGridControlOperation promotion =
            RecapGridControlOperation.Create(
                "promote-operation",
                2,
                OperationRuntimeDigest
            );
        RecapGridControlOperationResult.Applied promoted = Assert.IsType<
            RecapGridControlOperationResult.Applied
        >(handle.Coordinator.CompareExchangeAgentPromotion(
            registered.Head,
            values.TimelineHead,
            values.Recipe.Digest,
            promotion
        ));
        Assert.Equal(values.Recipe.Digest, promoted.Head.ActiveRecipeDigest);
        Assert.IsType<RecapGridControlOperationResult.Replayed>(
            handle.Coordinator.CompareExchangeAgentPromotion(
                registered.Head,
                values.TimelineHead,
                values.Recipe.Digest,
                promotion
            )
        );
    }

    [Fact]
    public void RestoreUnionsAndReinitializePreservesTerminalReceipts() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        var bundle = new RecapGridControlRegistrationBundle(
            [values.Family],
            [],
            []
        );
        RecapGridControlOperation first = RecapGridControlOperation.Create(
            "receipt-before-backup",
            1,
            OperationRuntimeDigest
        );
        RecapGridControlOperation second = RecapGridControlOperation.Create(
            "receipt-after-backup",
            2,
            OperationRuntimeDigest
        );

        ControlHeadRef firstHead;
        using (RecapGridControlHandle handle = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   values.Admission
               )).Handle) {
            firstHead = Assert.IsType<
                RecapGridControlOperationResult.Applied
            >(handle.Coordinator.ApplyRegistrationBundle(
                initial,
                values.TimelineHead,
                first,
                bundle
            )).Head;
        }

        string backup = Path.Combine(path, "receipt-backup");
        Assert.IsType<RecapGridControlBackupResult.Created>(
            RecapGridControlMaintenance.Backup(
                path,
                journal.BranchRefId,
                firstHead,
                backup
            )
        );

        ControlHeadRef secondHead;
        using (RecapGridControlHandle handle = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   values.Admission
               )).Handle) {
            secondHead = Assert.IsType<
                RecapGridControlOperationResult.Applied
            >(handle.Coordinator.ApplyRegistrationBundle(
                firstHead,
                values.TimelineHead,
                second,
                bundle
            )).Head;
        }

        ControlHeadRef restored = Assert.IsType<
            RecapGridControlAdminResult.Applied
        >(RecapGridControlMaintenance.Restore(
            path,
            journal.BranchRefId,
            secondHead,
            backup
        )).Head;
        using (RecapGridControlHandle handle = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   values.Admission
               )).Handle) {
            RecapGridControlOperationResult.Replayed firstReplay =
                Assert.IsType<RecapGridControlOperationResult.Replayed>(
                    handle.Coordinator.ApplyRegistrationBundle(
                        initial,
                        values.TimelineHead,
                        first,
                        bundle
                    )
                );
            RecapGridControlOperationResult.Replayed secondReplay =
                Assert.IsType<RecapGridControlOperationResult.Replayed>(
                    handle.Coordinator.ApplyRegistrationBundle(
                        firstHead,
                        values.TimelineHead,
                        second,
                        bundle
                    )
                );
            Assert.Equal(restored, firstReplay.CurrentHead);
            Assert.Equal(restored, secondReplay.CurrentHead);
            Assert.True(firstReplay.HeadAdvancedSinceApply);
            Assert.True(firstReplay.InstanceReplaced);
            Assert.True(secondReplay.HeadAdvancedSinceApply);
            Assert.True(secondReplay.InstanceReplaced);
        }

        ControlHeadRef reinitialized = Assert.IsType<
            RecapGridControlAdminResult.Applied
        >(RecapGridControlMaintenance.Reinitialize(
            path,
            journal.BranchRefId,
            restored
        )).Head;
        using RecapGridControlHandle afterReinitialize = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            values.Admission
        )).Handle;
        RecapGridControlOperationResult.Replayed replay = Assert.IsType<
            RecapGridControlOperationResult.Replayed
        >(afterReinitialize.Coordinator.ApplyRegistrationBundle(
            secondHead,
            values.TimelineHead,
            second,
            bundle
        ));
        Assert.Equal(reinitialized, replay.CurrentHead);
        Assert.True(replay.HeadAdvancedSinceApply);
        Assert.True(replay.InstanceReplaced);
    }

    [Fact]
    public void ReceiptCapIsExactAndOperationIdIsStrictBoundedText() {
        Assert.Equal(
            16_384,
            ControlStorageLimits.MaximumOperationReceiptCount
        );
        ControlState state = ControlState.CreateEmpty(
            new Atelia.EventJournal.RefId(1),
            new Atelia.SessionJournal.HistoryTimeline.TimelineId(
                "00112233445566778899aabbccddeeff"
            )
        );
        ControlOperationReceipt first = Receipt("1", 1);
        state = state.WithTerminalOperationForTest(
            first,
            generation: 1,
            maximumCount: 1
        );
        Assert.Throws<ControlLimitException>(() =>
            state.WithTerminalOperationForTest(
                Receipt("2", 2),
                generation: 2,
                maximumCount: 1
            ));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RecapGridControlOperation.Create(
                new string('x', 513),
                1,
                OperationRuntimeDigest
            ));
        Assert.Throws<ArgumentException>(() =>
            RecapGridControlOperation.Create(
                "bad\ud800",
                1,
                OperationRuntimeDigest
            ));

        static ControlOperationReceipt Receipt(string suffix, long sequence) {
            string digest = suffix.PadLeft(64, '0');
            return new ControlOperationReceipt(
                digest,
                sequence,
                new string('a', 64),
                new string('b', 64),
                new string('c', 64),
                new ControlInstanceId(
                    "00112233445566778899aabbccddeeff"
                ),
                sequence
            );
        }
    }

    [Fact]
    public void Admission_ExactLiteralRoundtripsAsOwnerCanonicalBytes() {
        byte[] expected =
            """{"schemaVersion":1,"permissions":0,"familyDigests":[],"capabilityFingerprints":[],"targetCarriers":[],"logicalColumnPrefixes":["case."],"maximumBootstrapRows":0,"maximumProjectedCalls":0}"""u8.ToArray();

        RecapGridControlAdmission decoded =
            RecapGridControlAdmission.DecodeCanonical(expected);

        Assert.Equal(expected, decoded.ToCanonicalBytes());
    }

    [Theory]
    [InlineData("future-version")]
    [InlineData("fractional-version")]
    [InlineData("duplicate-version")]
    [InlineData("root-order")]
    [InlineData("unknown")]
    [InlineData("duplicate-prefix")]
    [InlineData("descending-prefix")]
    public void Admission_RejectsNoncanonicalV1Mutations(string kind) {
        const string canonical =
            """{"schemaVersion":1,"permissions":0,"familyDigests":[],"capabilityFingerprints":[],"targetCarriers":[],"logicalColumnPrefixes":["case."],"maximumBootstrapRows":0,"maximumProjectedCalls":0}""";
        string invalid = kind switch {
            "future-version" => canonical.Replace(
                "\"schemaVersion\":1",
                "\"schemaVersion\":2",
                StringComparison.Ordinal
            ),
            "fractional-version" => canonical.Replace(
                "\"schemaVersion\":1",
                "\"schemaVersion\":1.0",
                StringComparison.Ordinal
            ),
            "duplicate-version" => canonical.Replace(
                "{\"schemaVersion\":1,",
                "{\"schemaVersion\":1,\"schemaVersion\":1,",
                StringComparison.Ordinal
            ),
            "root-order" => canonical.Replace(
                "{\"schemaVersion\":1,\"permissions\":0,",
                "{\"permissions\":0,\"schemaVersion\":1,",
                StringComparison.Ordinal
            ),
            "unknown" => canonical.Replace(
                "{\"schemaVersion\":1,",
                "{\"schemaVersion\":1,\"unknown\":0,",
                StringComparison.Ordinal
            ),
            "duplicate-prefix" => canonical.Replace(
                "[\"case.\"]",
                "[\"case.\",\"case.\"]",
                StringComparison.Ordinal
            ),
            "descending-prefix" => canonical.Replace(
                "[\"case.\"]",
                "[\"z.\",\"a.\"]",
                StringComparison.Ordinal
            ),
            _ => throw new InvalidOperationException()
        };
        Assert.NotEqual(canonical, invalid);

        Assert.Throws<InvalidDataException>(() =>
            RecapGridControlAdmission.DecodeCanonical(
                Encoding.UTF8.GetBytes(invalid)
            ));
    }

    [Fact]
    public void Admission_PublicProducerRejectsBytesBeyondDecoderCap() {
        FamilyDefinitionDigest[] families = AdmissionFamilyDigests(256);
        string[] capabilities = AdmissionCapabilityDigests(256);
        string[] escapingPrefixes = Enumerable.Range(0, 128)
            .Select(index => new string('\\', 126)
                + index.ToString("X2", CultureInfo.InvariantCulture))
            .ToArray();
        var nearBound = new RecapGridControlAdmission(
            RecapGridControlPermission.None,
            families,
            capabilities,
            [],
            escapingPrefixes[..118],
            0,
            0
        );
        byte[] nearBoundBytes = nearBound.ToCanonicalBytes();

        Assert.InRange(nearBoundBytes.Length, 60 * 1024, 64 * 1024);
        Assert.Equal(
            nearBoundBytes,
            RecapGridControlAdmission.DecodeCanonical(nearBoundBytes)
                .ToCanonicalBytes()
        );
        Assert.Throws<ArgumentException>(() =>
            new RecapGridControlAdmission(
                RecapGridControlPermission.None,
                families,
                capabilities,
                [],
                escapingPrefixes,
                0,
                0
            ));
    }

    [Fact]
    public void Admission_OrdinalOrderIsIndependentOfCurrentCulture() {
        CultureInfo priorCulture = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var owner = new RecapGridControlAdmission(
                RecapGridControlPermission.None,
                [],
                [],
                [],
                ["z.", "ä."],
                0,
                0
            );
            byte[] ownerBytes = owner.ToCanonicalBytes();
            string ownerText = Encoding.UTF8.GetString(ownerBytes);
            Assert.Contains(
                "\"logicalColumnPrefixes\":[\"z.\",\"ä.\"]",
                ownerText,
                StringComparison.Ordinal
            );

            Assert.Equal(
                ownerBytes,
                RecapGridControlAdmission.DecodeCanonical(ownerBytes)
                    .ToCanonicalBytes()
            );
            string cultureOrdered = ownerText.Replace(
                "[\"z.\",\"ä.\"]",
                "[\"ä.\",\"z.\"]",
                StringComparison.Ordinal
            );
            Assert.NotEqual(ownerText, cultureOrdered);
            Assert.Throws<InvalidDataException>(() =>
                RecapGridControlAdmission.DecodeCanonical(
                    Encoding.UTF8.GetBytes(cultureOrdered)
                ));
        }
        finally {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Fact]
    public void Admission_CollectionAndNumericBoundsAreInclusive() {
        FamilyDefinitionDigest[] maximumFamilies =
            AdmissionFamilyDigests(256);
        string[] maximumCapabilities = AdmissionCapabilityDigests(256);
        string[] maximumPrefixes = Enumerable.Range(0, 128)
            .Select(index => $"p{index:D3}.")
            .ToArray();

        AssertAdmissionRoundtrips(new RecapGridControlAdmission(
            RecapGridControlPermission.None,
            maximumFamilies,
            [],
            [],
            ["case."],
            0,
            0
        ));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RecapGridControlAdmission(
                RecapGridControlPermission.None,
                AdmissionFamilyDigests(257),
                [],
                [],
                ["case."],
                0,
                0
            ));
        AssertAdmissionRoundtrips(new RecapGridControlAdmission(
            RecapGridControlPermission.None,
            [],
            maximumCapabilities,
            [],
            ["case."],
            0,
            0
        ));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RecapGridControlAdmission(
                RecapGridControlPermission.None,
                [],
                AdmissionCapabilityDigests(257),
                [],
                ["case."],
                0,
                0
            ));
        AssertAdmissionRoundtrips(new RecapGridControlAdmission(
            RecapGridControlPermission.None,
            [],
            [],
            [],
            maximumPrefixes,
            0,
            0
        ));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RecapGridControlAdmission(
                RecapGridControlPermission.None,
                [],
                [],
                [],
                [.. maximumPrefixes, "overflow."],
                0,
                0
            ));
        AssertAdmissionRoundtrips(new RecapGridControlAdmission(
            RecapGridControlPermission.None,
            [],
            [],
            [],
            ["case."],
            1_000_000,
            1_000_000
        ));
        foreach (int invalid in new[] { -1, 1_000_001 }) {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new RecapGridControlAdmission(
                    RecapGridControlPermission.None,
                    [],
                    [],
                    [],
                    ["case."],
                    invalid,
                    0
                ));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new RecapGridControlAdmission(
                    RecapGridControlPermission.None,
                    [],
                    [],
                    [],
                    ["case."],
                    0,
                    invalid
                ));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FatalPublishFailuresPropagateAndRetrySettlesByReceipt(
        bool afterPublish,
        bool accessViolation
    ) {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        RecapGridControlOperation operation =
            RecapGridControlOperation.Create(
                $"fatal-{afterPublish}-{accessViolation}",
                1,
                OperationRuntimeDigest
            );
        var bundle = new RecapGridControlRegistrationBundle(
            [values.Family], [], []
        );
        Exception Fatal() => accessViolation
            ? new AccessViolationException("injected fatal")
            : new OutOfMemoryException("injected fatal");
        var hooks = afterPublish
            ? new ControlPersistenceTestHooks(
                AfterStatePublish: _ => throw Fatal())
            : new ControlPersistenceTestHooks(
                BeforeStatePublish: () => throw Fatal());

        using (RecapGridControlHandle injected = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.OpenForTest(
                   path,
                   journal.BranchRefId,
                   values.Admission,
                   hooks
               )).Handle) {
            if (accessViolation) {
                Assert.Throws<AccessViolationException>(() =>
                    injected.Coordinator.ApplyRegistrationBundle(
                        initial,
                        values.TimelineHead,
                        operation,
                        bundle
                    ));
            }
            else {
                Assert.Throws<OutOfMemoryException>(() =>
                    injected.Coordinator.ApplyRegistrationBundle(
                        initial,
                        values.TimelineHead,
                        operation,
                        bundle
                    ));
            }
        }

        using RecapGridControlHandle retry = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            values.Admission
        )).Handle;
        RecapGridControlOperationResult result =
            retry.Coordinator.ApplyRegistrationBundle(
                initial,
                values.TimelineHead,
                operation,
                bundle
            );
        if (afterPublish) {
            Assert.IsType<RecapGridControlOperationResult.Replayed>(result);
        }
        else {
            Assert.IsType<RecapGridControlOperationResult.Applied>(result);
        }
    }

    [Fact]
    public void FatalInnerPublishIndeterminatePropagatesOriginalFatal() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        RecapGridControlOperation operation =
            RecapGridControlOperation.Create(
                "fatal-inner-indeterminate",
                1,
                OperationRuntimeDigest
            );
        var bundle = new RecapGridControlRegistrationBundle(
            [values.Family], [], []
        );
        var hooks = new ControlPersistenceTestHooks(
            AfterStatePublish: static _ => throw
                new ControlStatePublishIndeterminateException(
                    new OutOfMemoryException("fatal inner")
                )
        );
        using (RecapGridControlHandle injected = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.OpenForTest(
                   path,
                   journal.BranchRefId,
                   values.Admission,
                   hooks
               )).Handle) {
            Assert.Throws<OutOfMemoryException>(() =>
                injected.Coordinator.ApplyRegistrationBundle(
                    initial,
                    values.TimelineHead,
                    operation,
                    bundle
                ));
        }
        using RecapGridControlHandle retry = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            values.Admission
        )).Handle;
        Assert.IsType<RecapGridControlOperationResult.Replayed>(
            retry.Coordinator.ApplyRegistrationBundle(
                initial,
                values.TimelineHead,
                operation,
                bundle
            )
        );
    }

    [Theory]
    [InlineData("familyDigests")]
    [InlineData("capabilityFingerprints")]
    [InlineData("targetCarriers")]
    [InlineData("logicalColumnPrefixes")]
    public void AdmissionNullCollectionsAreTypedInvalidData(string property) {
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.None,
            [],
            [],
            [],
            ["case."],
            0,
            0
        );
        string canonical = System.Text.Encoding.UTF8.GetString(
            admission.ToCanonicalBytes()
        );
        string marker = property == "logicalColumnPrefixes"
            ? $"\"{property}\":[\"case.\"]"
            : $"\"{property}\":[]";
        string malformed = canonical.Replace(
            marker,
            $"\"{property}\":null",
            StringComparison.Ordinal
        );
        Assert.NotEqual(canonical, malformed);
        Assert.Throws<InvalidDataException>(() =>
            RecapGridControlAdmission.DecodeCanonical(
                System.Text.Encoding.UTF8.GetBytes(malformed)
            ));
    }

    [Fact]
    public void LegacyV1ControlStateIsExplicitlyUnsupported() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef created = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        string statePath = ControlStatePath(
            path,
            journal.BranchRefId,
            created
        );
        string current = File.ReadAllText(statePath);
        Assert.StartsWith("{\"schemaVersion\":2,", current,
            StringComparison.Ordinal);
        File.WriteAllText(
            statePath,
            "{\"schemaVersion\":1," + current[
                "{\"schemaVersion\":2,".Length..]
        );

        RecapGridControlOpenResult.UnsupportedSchema unsupported =
            Assert.IsType<RecapGridControlOpenResult.UnsupportedSchema>(
                RecapGridControlFactory.Open(
                    path,
                    journal.BranchRefId,
                    values.Admission
                )
            );
        Assert.Equal(1, unsupported.SchemaVersion);
    }

    private static FamilyDefinitionDigest[] AdmissionFamilyDigests(int count)
        => Enumerable.Range(0, count)
            .Select(index => new FamilyDefinitionDigest(
                index.ToString("x64", CultureInfo.InvariantCulture)
            ))
            .ToArray();

    private static string[] AdmissionCapabilityDigests(int count)
        => Enumerable.Range(0, count)
            .Select(index => (index + 4_096).ToString(
                "x64",
                CultureInfo.InvariantCulture
            ))
            .ToArray();

    private static void AssertAdmissionRoundtrips(
        RecapGridControlAdmission admission
    ) {
        byte[] bytes = admission.ToCanonicalBytes();
        Assert.Equal(
            bytes,
            RecapGridControlAdmission.DecodeCanonical(bytes)
                .ToCanonicalBytes()
        );
    }
}
