using System.Text.Json;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.AgentControl.Tests;

public sealed class AgentControlVerticalTests : IDisposable {
    private readonly List<string> _paths = [];
    private readonly O200kBaseHistoryUnitLoadEstimator _estimator = new();

    [Fact]
    public async Task BuiltInRegistrationReplaysAndConflictsWithoutAuthorityPayload() {
        Fixture fixture = CreateFixture();
        using (fixture.Journal)
        using (RecapGridAgentControlHandle handle = Open(fixture)) {
            Assert.Equal(
                "recap_grid.control",
                Assert.Single(handle.ToolSession.VisibleDefinitions).Name
            );
            Assert.StartsWith(
                "atelia.recap-grid.agent-control.v1",
                handle.RuntimeIdentity.HostId
            );
            string command = JsonSerializer.Serialize(new {
                action = "provision-built-in",
                builtInAssetId = RecapGridAgentControlBuiltIns
                    .MysteryInvestigationV3
            });
            ToolCallExecutionResult applied = await handle.ToolSession
                .ExecuteReservedAsync(
                    new RawToolCall(
                        "recap_grid.control",
                        "call-1",
                        command
                    ),
                    1,
                    "durable-operation-1",
                    TestContext.Current.CancellationToken
                );
            Assert.Equal(ToolExecutionStatus.Success,
                applied.ExecuteResult.Status);
            Assert.Contains("\"status\":\"applied\"",
                applied.ExecuteResult.GetFlattenedText());

            ToolCallExecutionResult replay = await handle.ToolSession
                .ExecuteReservedAsync(
                    new RawToolCall(
                        "recap_grid.control",
                        "call-1",
                        command
                    ),
                    1,
                    "durable-operation-1",
                    TestContext.Current.CancellationToken
                );
            Assert.Equal(ToolExecutionStatus.Success,
                replay.ExecuteResult.Status);
            Assert.Contains("\"status\":\"replayed\"",
                replay.ExecuteResult.GetFlattenedText());

            Assert.True(RecapGridAgentControlBuiltIns
                .TryCreateRegistrationBundle(
                    RecapGridAgentControlBuiltIns.MysteryInvestigationV3,
                    out RecapGridControlRegistrationBundle? builtIn));
            string conflictingCommand = JsonSerializer.Serialize(new {
                action = "register-family",
                canonicalValueBase64 = Convert.ToBase64String(
                    builtIn!.Families[0].ToCanonicalBytes())
            });
            ToolCallExecutionResult conflict = await handle.ToolSession
                .ExecuteReservedAsync(
                    new RawToolCall(
                        "recap_grid.control",
                        "call-1",
                        conflictingCommand
                    ),
                    1,
                    "durable-operation-1",
                    TestContext.Current.CancellationToken
                );
            Assert.Equal(ToolExecutionStatus.Failed,
                conflict.ExecuteResult.Status);
            Assert.Contains("operation-conflict",
                conflict.ExecuteResult.GetFlattenedText());

            using RecapGridControlReaderHandle reader = Assert.IsType<
                RecapGridControlReaderOpenResult.Opened
            >(RecapGridControlFactory.OpenReader(
                fixture.Path,
                fixture.Journal.BranchRefId
            )).Handle;
            RecapGridControlSnapshot snapshot = Assert.IsType<
                RecapGridControlSnapshotResult.Available
            >(reader.Reader.ReadSnapshot()).Snapshot;
            Assert.Single(snapshot.Families);
            Assert.Equal(2, snapshot.Definitions.Count);
            Assert.Equal(1, snapshot.Head.Generation);
        }
    }

    [Fact]
    public async Task NarrowProfileRejectsDefinitionBeforeAnyControlMutation() {
        Fixture fixture = CreateFixture();
        Assert.True(RecapGridAgentControlBuiltIns.TryCreateRegistrationBundle(
            RecapGridAgentControlBuiltIns.MysteryInvestigationV3,
            out RecapGridControlRegistrationBundle? builtIn
        ));
        var narrow = new RecapGridControlAdmission(
            RecapGridControlPermission.RegisterFamily,
            [builtIn!.Families[0].Digest],
            builtIn.Definitions.Select(static value =>
                value.Capability.CapabilityFingerprint),
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 64,
            maximumProjectedCalls: 128
        );
        using (fixture.Journal)
        using (RecapGridAgentControlHandle handle = Assert.IsType<
                   RecapGridAgentControlOpenResult.Opened
               >(RecapGridAgentControlFactory.Bind(
                   fixture.Journal.ReadView,
                   RecapGridAgentControlProfile.Create("narrow-v1", narrow),
                   _estimator
               )).Handle) {
            ControlHeadRef before = ReadControlHead(fixture);
            ToolCallExecutionResult result = await handle.ToolSession
                .ExecuteReservedAsync(
                    new RawToolCall(
                        "recap_grid.control",
                        "unauthorized-definition-call",
                        JsonSerializer.Serialize(new {
                            action = "register-definition",
                            canonicalValueBase64 = Convert.ToBase64String(
                                builtIn.Definitions[0].ToCanonicalBytes()
                            )
                        })
                    ),
                    1,
                    "unauthorized-definition-operation",
                    TestContext.Current.CancellationToken
                );

            Assert.Equal(ToolExecutionStatus.Failed,
                result.ExecuteResult.Status);
            Assert.Contains("unauthorized",
                result.ExecuteResult.GetFlattenedText());
            Assert.Equal(before, ReadControlHead(fixture));
        }
    }

    [Fact]
    public async Task ManagerInspectionCancellationPrecedesPromotionMutation() {
        Fixture fixture = CreateFixture();
        using (fixture.Journal) {
            RecapGridAgentControlProfile profile =
                RecapGridAgentControlProfile.Create(
                    "manager-cancelled-v1",
                    fixture.Admission
                );
            using RecapGridAgentControlHandle handle = Assert.IsType<
                RecapGridAgentControlOpenResult.Opened
            >(RecapGridAgentControlFactory.BindForTest(
                fixture.Journal.ReadView,
                profile,
                new AgentControlDependencyTestHooks(
                    ProgressResultOverride: _ =>
                        new RecapGridBuildProgressResult.Cancelled()
                ),
                _estimator
            )).Handle;

            ControlHeadRef before = ReadControlHead(fixture);
            await Assert.ThrowsAsync<
                ToolExecutionCancelledBeforeMutationException>(async () =>
                    await handle.ToolSession.ExecuteReservedAsync(
                        new RawToolCall(
                            "recap_grid.control",
                            "cancelled-promotion-call",
                            JsonSerializer.Serialize(new {
                                action = "promote",
                                recipeDigest = new string('a', 64)
                            })
                        ),
                        1,
                        "cancelled-promotion-operation",
                        TestContext.Current.CancellationToken
                    ));
            Assert.Equal(before, ReadControlHead(fixture));
        }
    }

    [Fact]
    public async Task PromotionMissingAndStaleOutcomesUseStableCodes() {
        Fixture fixture = CreateFixture();
        var recipe = new GridBuildRecipeDigest(new string('a', 64));
        TimelineHeadRef timelineHead = ReadTimelineHead(fixture);
        (RecapGridBuildProgressResult Result, string Code)[] cases = [
            (new RecapGridBuildProgressResult.RecipeAbsent(recipe),
                "recipe-absent"),
            (new RecapGridBuildProgressResult.StaleTimelineHead(timelineHead),
                "stale-timeline-head")
        ];
        using (fixture.Journal) {
            int sequence = 0;
            foreach ((RecapGridBuildProgressResult progress, string code)
                     in cases) {
                RecapGridAgentControlProfile profile =
                    RecapGridAgentControlProfile.Create(
                        $"promotion-{sequence}-v1",
                        fixture.Admission
                    );
                using RecapGridAgentControlHandle handle = Assert.IsType<
                    RecapGridAgentControlOpenResult.Opened
                >(RecapGridAgentControlFactory.BindForTest(
                    fixture.Journal.ReadView,
                    profile,
                    new AgentControlDependencyTestHooks(
                        ProgressResultOverride: _ => progress
                    ),
                    _estimator
                )).Handle;
                ToolCallExecutionResult result = await handle.ToolSession
                    .ExecuteReservedAsync(
                        new RawToolCall(
                            "recap_grid.control",
                            $"promotion-{sequence}",
                            JsonSerializer.Serialize(new {
                                action = "promote",
                                recipeDigest = recipe.Value
                            })
                        ),
                        ++sequence,
                        $"promotion-outcome-{sequence}",
                        TestContext.Current.CancellationToken
                    );
                Assert.Equal(ToolExecutionStatus.Failed,
                    result.ExecuteResult.Status);
                Assert.Contains($"\"status\":\"{code}\"",
                    result.ExecuteResult.GetFlattenedText());
            }
        }
    }

    [Theory]
    [InlineData("{\"action\":\"inspect\",\"action\":\"inspect\"}")]
    [InlineData("{\"Action\":\"inspect\"}")]
    [InlineData("{\"action\":\"inspect\",}")]
    [InlineData("{/*x*/\"action\":\"inspect\"}")]
    [InlineData("{\"action\":\"inspect\",\"unknown\":\"x\"}")]
    [InlineData("{\"action\":{\"x\":{\"x\":{\"x\":{\"x\":{\"x\":{\"x\":{\"x\":{\"x\":\"inspect\"}}}}}}}}}")]
    public async Task StrictArgumentsFailClosed(string rawArguments) {
        Fixture fixture = CreateFixture();
        using (fixture.Journal)
        using (RecapGridAgentControlHandle handle = Open(fixture)) {
            ToolCallExecutionResult result = await handle.ToolSession
                .ExecuteReservedAsync(
                    new RawToolCall(
                        "recap_grid.control",
                        "call",
                        rawArguments
                    ),
                    1,
                    "strict-operation",
                    TestContext.Current.CancellationToken
                );
            Assert.Equal(ToolExecutionStatus.Failed,
                result.ExecuteResult.Status);
            string detail = result.ExecuteResult.GetFlattenedText();
            Assert.True(
                detail.Contains("arguments-invalid", StringComparison.Ordinal)
                || detail.Contains("工具参数解析失败", StringComparison.Ordinal),
                detail
            );
        }
    }

    [Fact]
    public async Task MultiMegabyteInvalidArgumentsNeverOpenDependenciesOrMutateControl() {
        Fixture fixture = CreateFixture();
        using (fixture.Journal) {
            int dependencyOpens = 0;
            ControlHeadRef before = ReadControlHead(fixture);
            RecapGridAgentControlProfile profile =
                RecapGridAgentControlProfile.Create(
                    "large-invalid-v1",
                    fixture.Admission
                );
            using RecapGridAgentControlHandle handle = Assert.IsType<
                RecapGridAgentControlOpenResult.Opened
            >(RecapGridAgentControlFactory.BindForTest(
                fixture.Journal.ReadView,
                profile,
                new AgentControlDependencyTestHooks(
                    BeforeOpen: () => dependencyOpens++
                ),
                _estimator
            )).Handle;
            string secretTail = "agent-control-secret-tail";
            string rawArguments = string.Concat(
                "{\"action\":\"inspect\",\"unknown\":\"",
                new string('x', 2 * 1024 * 1024),
                secretTail,
                "\"}"
            );

            ToolCallExecutionResult result = await handle.ToolSession
                .ExecuteReservedAsync(
                    new RawToolCall(
                        "recap_grid.control",
                        "large-invalid-call",
                        rawArguments
                    ),
                    1,
                    "large-invalid-operation",
                    TestContext.Current.CancellationToken
                );

            Assert.Equal(ToolExecutionStatus.Failed,
                result.ExecuteResult.Status);
            string detail = result.ExecuteResult.GetFlattenedText();
            Assert.True(Encoding.UTF8.GetByteCount(detail) <= 4 * 1024,
                detail);
            Assert.Contains("tool_input_parse_failed", detail,
                StringComparison.Ordinal);
            Assert.DoesNotContain(secretTail, detail,
                StringComparison.Ordinal);
            Assert.Equal(0, dependencyOpens);
            Assert.Equal(before, ReadControlHead(fixture));
        }
    }

    [Fact]
    public async Task MissingOperationAndDisposedHandleFailBeforeMutation() {
        Fixture fixture = CreateFixture();
        using (fixture.Journal) {
            RecapGridAgentControlHandle handle = Open(fixture);
            ToolCallExecutionResult missing = await handle.ToolSession
                .ExecuteAsync(
                    new RawToolCall(
                        "recap_grid.control",
                        "call",
                        "{\"action\":\"inspect\"}"
                    ),
                    TestContext.Current.CancellationToken
                );
            Assert.Equal(ToolExecutionStatus.Failed,
                missing.ExecuteResult.Status);
            Assert.Contains("operation-id-required",
                missing.ExecuteResult.GetFlattenedText());
            handle.Dispose();
            ToolCallExecutionResult disposed = await handle.ToolSession
                .ExecuteReservedAsync(
                    new RawToolCall(
                        "recap_grid.control",
                        "call",
                        "{\"action\":\"inspect\"}"
                    ),
                    1,
                    "disposed-operation",
                    TestContext.Current.CancellationToken
                );
            Assert.Equal(ToolExecutionStatus.Failed,
                disposed.ExecuteResult.Status);
            Assert.Contains("disposed",
                disposed.ExecuteResult.GetFlattenedText());
        }
    }

    [Fact]
    public void RuntimeIdentityIsExactToAdmissionAndCanonicalBuiltIns() {
        Fixture fixture = CreateFixture();
        using (fixture.Journal)
        using (RecapGridAgentControlHandle first = Open(fixture))
        using (RecapGridAgentControlHandle second = Open(fixture)) {
            Assert.Equal(first.RuntimeIdentity, second.RuntimeIdentity);
            Assert.Equal(
                "e1820acd6d127007c8fef62e479b1e3e026e7be45df78cf3075fe6eb632f74fc",
                first.RuntimeIdentity.ImplementationSetFingerprint
            );
            Assert.Equal(
                [RecapGridAgentControlBuiltIns.MysteryInvestigationV3],
                RecapGridAgentControlBuiltIns.AssetIds
            );
            Assert.True(RecapGridAgentControlBuiltIns
                .TryCreateRegistrationBundle(
                    RecapGridAgentControlBuiltIns.MysteryInvestigationV3,
                    out RecapGridControlRegistrationBundle? one));
            Assert.True(RecapGridAgentControlBuiltIns
                .TryCreateRegistrationBundle(
                    RecapGridAgentControlBuiltIns.MysteryInvestigationV3,
                    out RecapGridControlRegistrationBundle? two));
            Assert.Equal(
                one!.Families[0].ToCanonicalBytes(),
                two!.Families[0].ToCanonicalBytes()
            );
            Assert.Equal(
                one.ToCanonicalCommandBytes(),
                two.ToCanonicalCommandBytes()
            );
            Assert.Equal(
                "75322b32dd1596da8e2deb0234fa3efa4907de4d7781f866ddf542d03fc8c2e4",
                one.CanonicalCommandDigest
            );
            Assert.Equal(
                one.Definitions.Select(static value => value.Digest),
                two.Definitions.Select(static value => value.Digest)
            );
            Assert.Equal(
                "1c624c15a45116b5e32620b2c76d65f52a0dbac3ab819c2ad15c84f3f1d00508",
                one.Families[0].Digest.Value
            );
            Assert.Equal(
                [
                    "55946165a17a3249d2f49ce8c4b9fcf4d71b7df45ce80223da817d8d03bc0a13",
                    "8007c70ef5fc4d53d037a96e1f3af93f83dcb8e7a75a8f9afff00470ff4ec5e2"
                ],
                one.Definitions.Select(static value => value.Digest.Value)
            );
        }
    }

    [Fact]
    public void ProfileCodecAndRegistryBindExactWithoutFallback() {
        Fixture fixture = CreateFixture();
        using (fixture.Journal) {
            RecapGridAgentControlProfile profile =
                RecapGridAgentControlProfile.Create(
                    "operator-v1",
                    fixture.Admission
                );
            RecapGridAgentControlProfile decoded =
                RecapGridAgentControlProfile.DecodeCanonical(
                    profile.ToCanonicalBytes()
                );
            Assert.Equal(profile.ProfileId, decoded.ProfileId);
            Assert.Equal(profile.RuntimeIdentity, decoded.RuntimeIdentity);
            Assert.Equal(
                profile.ToCanonicalBytes(),
                decoded.ToCanonicalBytes()
            );
            var registry = new RecapGridAgentControlProfileRegistry(
                [profile]
            );
            Assert.True(registry.TryGet("operator-v1", out var byId));
            Assert.Same(profile, byId);
            Assert.True(registry.TryBindExact(
                profile.RuntimeIdentity,
                out var byRuntime
            ));
            Assert.Same(profile, byRuntime);
            Assert.False(registry.TryGet("missing", out _));
            Assert.False(registry.TryBindExact(
                profile.RuntimeIdentity with {
                    CapabilitySetFingerprint = new string('f', 64)
                },
                out _
            ));

            using RecapGridAgentControlHandle opened = Assert.IsType<
                RecapGridAgentControlOpenResult.Opened
            >(RecapGridAgentControlFactory.Bind(
                fixture.Journal.ReadView,
                profile,
                _estimator
            )).Handle;
            Assert.Equal(profile.RuntimeIdentity, opened.RuntimeIdentity);

            byte[] nonCanonical = [
                .. Encoding.UTF8.GetBytes(" "),
                .. profile.ToCanonicalBytes()
            ];
            Assert.Throws<InvalidDataException>(() =>
                RecapGridAgentControlProfile.DecodeCanonical(nonCanonical));
        }
    }

    [Fact]
    public async Task FrozenBindingIsDerivedLazyAndFirstExecutionFailsClosed() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "atelia-agent-control-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        using SessionJournalEngine journal = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "surface")
        );
        RecapGridAgentControlProfile profile =
            RecapGridAgentControlProfile.Create(
                "frozen-v1",
                CreateAdmission()
            );
        int dependencyOpens = 0;
        using RecapGridAgentControlHandle handle = Assert.IsType<
            RecapGridAgentControlOpenResult.Opened
        >(RecapGridAgentControlFactory.BindForTest(
            journal.ReadView,
            profile,
            new AgentControlDependencyTestHooks(
                BeforeOpen: () => dependencyOpens++
            ),
            _estimator
        )).Handle;

        Assert.Equal(0, dependencyOpens);
        Assert.Equal(profile.RuntimeIdentity, handle.RuntimeIdentity);
        Assert.Single(handle.ToolSession.VisibleDefinitions);

        ToolCallExecutionResult first = await handle.ToolSession
            .ExecuteReservedAsync(
                new RawToolCall(
                    "recap_grid.control",
                    "call",
                    "{\"action\":\"inspect\"}"
                ),
                1,
                "frozen-operation",
                TestContext.Current.CancellationToken
            );
        Assert.Equal(ToolExecutionStatus.Failed,
            first.ExecuteResult.Status);
        Assert.Contains("timeline-absent",
            first.ExecuteResult.GetFlattenedText());
        Assert.Equal(1, dependencyOpens);

        _ = await handle.ToolSession.ExecuteReservedAsync(
            new RawToolCall(
                "recap_grid.control",
                "call",
                "{\"action\":\"inspect\"}"
            ),
            1,
            "frozen-operation",
            TestContext.Current.CancellationToken
        );
        Assert.Equal(1, dependencyOpens);
    }

    [Fact]
    public async Task DisposeWaitsForLazyOpenAndThenRejectsFurtherExecution() {
        Fixture fixture = CreateFixture();
        using (fixture.Journal) {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int dependencyOpens = 0;
            RecapGridAgentControlProfile profile =
                RecapGridAgentControlProfile.Create(
                    "blocking-profile-v1",
                    fixture.Admission
                );
            RecapGridAgentControlHandle handle = Assert.IsType<
                RecapGridAgentControlOpenResult.Opened
            >(RecapGridAgentControlFactory.BindForTest(
                fixture.Journal.ReadView,
                profile,
                new AgentControlDependencyTestHooks(
                    BeforeOpen: () => {
                        Interlocked.Increment(ref dependencyOpens);
                        entered.Set();
                        release.Wait();
                    }
                ),
                _estimator
            )).Handle;
            Task<ToolCallExecutionResult> execution = Task.Run(async () =>
                await handle.ToolSession.ExecuteReservedAsync(
                    new RawToolCall(
                        "recap_grid.control",
                        "call",
                        "{\"action\":\"inspect\"}"
                    ),
                    1,
                    "blocking-operation",
                    TestContext.Current.CancellationToken
                )
            );
            Assert.True(entered.Wait(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken
            ));
            Task disposing = Task.Run(
                handle.Dispose,
                TestContext.Current.CancellationToken
            );
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.False(disposing.IsCompleted);

            release.Set();
            ToolCallExecutionResult settled = await execution.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(ToolExecutionStatus.Success,
                settled.ExecuteResult.Status);
            await disposing.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(1, dependencyOpens);

            ToolCallExecutionResult disposed = await handle.ToolSession
                .ExecuteReservedAsync(
                    new RawToolCall(
                        "recap_grid.control",
                        "call",
                        "{\"action\":\"inspect\"}"
                    ),
                    2,
                    "after-dispose-operation",
                    TestContext.Current.CancellationToken
                );
            Assert.Equal(ToolExecutionStatus.Failed,
                disposed.ExecuteResult.Status);
            Assert.Contains("disposed",
                disposed.ExecuteResult.GetFlattenedText());
            handle.Dispose();
        }
    }

    [Fact]
    public async Task CancellationDuringLazyOpenIsSkippedBeforeMutation() {
        Fixture fixture = CreateFixture();
        using (fixture.Journal)
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var cancellation = new CancellationTokenSource()) {
            ControlHeadRef before = ReadControlHead(fixture);
            RecapGridAgentControlProfile profile =
                RecapGridAgentControlProfile.Create(
                    "cancel-during-open-v1",
                    fixture.Admission
                );
            using RecapGridAgentControlHandle handle = Assert.IsType<
                RecapGridAgentControlOpenResult.Opened
            >(RecapGridAgentControlFactory.BindForTest(
                fixture.Journal.ReadView,
                profile,
                new AgentControlDependencyTestHooks(
                    BeforeOpen: () => {
                        entered.Set();
                        release.Wait();
                    }
                ),
                _estimator
            )).Handle;
            Task<ToolCallExecutionResult> executing = Task.Run(async () =>
                await handle.ToolSession.ExecuteReservedAsync(
                    new RawToolCall(
                        "recap_grid.control",
                        "cancel-call",
                        "{\"action\":\"provision-built-in\",\"builtInAssetId\":\"mystery-investigation-v3\"}"
                    ),
                    1,
                    "cancel-before-mutation",
                    cancellation.Token
                )
            );
            Assert.True(entered.Wait(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken
            ));
            cancellation.Cancel();
            release.Set();
            ToolCallExecutionResult result = await executing.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(ToolExecutionStatus.Skipped,
                result.ExecuteResult.Status);
            Assert.Equal(before, ReadControlHead(fixture));
        }
    }

    [Fact]
    public async Task ControlCommitIndeterminatePropagatesAsUnsettledTool() {
        Fixture fixture = CreateFixture();
        using (fixture.Journal) {
            ControlHeadRef head = ReadControlHead(fixture);
            RecapGridAgentControlProfile profile =
                RecapGridAgentControlProfile.Create(
                    "indeterminate-v1",
                    fixture.Admission
                );
            using RecapGridAgentControlHandle handle = Assert.IsType<
                RecapGridAgentControlOpenResult.Opened
            >(RecapGridAgentControlFactory.BindForTest(
                fixture.Journal.ReadView,
                profile,
                new AgentControlDependencyTestHooks(
                    ControlOperationResultOverride: () =>
                        new RecapGridControlOperationResult
                            .CommitIndeterminate(
                                "operation",
                                head,
                                null
                            )
                ),
                _estimator
            )).Handle;
            ToolExecutionUnsettledException unsettled =
                await Assert.ThrowsAsync<ToolExecutionUnsettledException>(
                    async () => await handle.ToolSession.ExecuteReservedAsync(
                        new RawToolCall(
                            "recap_grid.control",
                            "indeterminate-call",
                            "{\"action\":\"provision-built-in\",\"builtInAssetId\":\"mystery-investigation-v3\"}"
                        ),
                        1,
                        "indeterminate-operation",
                        TestContext.Current.CancellationToken
                    )
                );
            Assert.Equal("commit-indeterminate", unsettled.Code);
            Assert.Equal(head, ReadControlHead(fixture));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FatalDependencyOpenPropagates(bool accessViolation) {
        Fixture fixture = CreateFixture();
        using (fixture.Journal) {
            RecapGridAgentControlProfile profile =
                RecapGridAgentControlProfile.Create(
                    "fatal-open-v1",
                    fixture.Admission
                );
            using RecapGridAgentControlHandle handle = Assert.IsType<
                RecapGridAgentControlOpenResult.Opened
            >(RecapGridAgentControlFactory.BindForTest(
                fixture.Journal.ReadView,
                profile,
                new AgentControlDependencyTestHooks(
                    BeforeOpen: () => throw (accessViolation
                        ? new AccessViolationException("fatal open")
                        : new OutOfMemoryException("fatal open"))
                ),
                _estimator
            )).Handle;
            Task Invoke() => handle.ToolSession.ExecuteReservedAsync(
                new RawToolCall(
                    "recap_grid.control",
                    "fatal-call",
                    "{\"action\":\"inspect\"}"
                ),
                1,
                "fatal-operation",
                TestContext.Current.CancellationToken
            ).AsTask();
            if (accessViolation) {
                await Assert.ThrowsAsync<AccessViolationException>(Invoke);
            }
            else {
                await Assert.ThrowsAsync<OutOfMemoryException>(Invoke);
            }
        }
    }

    private Fixture CreateFixture() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "atelia-agent-control-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        SessionJournalEngine journal = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "surface")
        );
        Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                journal.ReadView,
                new HistoryTimelineInitialPolicySpec(
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    new HistoryLoadUnit(1),
                    8,
                    1024 * 1024
                ),
                _estimator
            )
        );
        RecapGridControlAdmission admission = CreateAdmission();
        Assert.True(RecapGridAgentControlBuiltIns
            .TryCreateRegistrationBundle(
                RecapGridAgentControlBuiltIns.MysteryInvestigationV3,
                out RecapGridControlRegistrationBundle? builtIn));
        Assert.NotNull(builtIn);
        Assert.IsType<RecapGridControlCreateResult.Created>(
            RecapGridControlFactory.Create(
                path,
                journal.BranchRefId,
                admission
            )
        );
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(path)
        );
        return new Fixture(path, journal, admission);
    }

    private static RecapGridControlAdmission CreateAdmission() {
        Assert.True(RecapGridAgentControlBuiltIns
            .TryCreateRegistrationBundle(
                RecapGridAgentControlBuiltIns.MysteryInvestigationV3,
                out RecapGridControlRegistrationBundle? builtIn));
        return new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [builtIn!.Families[0].Digest],
            builtIn.Definitions.Select(static value =>
                value.Capability.CapabilityFingerprint),
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 64,
            maximumProjectedCalls: 128
        );
    }

    private RecapGridAgentControlHandle Open(Fixture fixture) {
        RecapGridAgentControlOpenResult result =
            RecapGridAgentControlFactory.Open(
                fixture.Journal.ReadView,
                fixture.Admission,
                _estimator
            );
        if (result is not RecapGridAgentControlOpenResult.Opened opened) {
            Assert.Fail(result.ToString());
            throw new InvalidOperationException();
        }
        return opened.Handle;
    }

    private static ControlHeadRef ReadControlHead(Fixture fixture) {
        using RecapGridControlReaderHandle reader = Assert.IsType<
            RecapGridControlReaderOpenResult.Opened
        >(RecapGridControlFactory.OpenReader(
            fixture.Path,
            fixture.Journal.BranchRefId
        )).Handle;
        return Assert.IsType<RecapGridControlSnapshotResult.Available>(
            reader.Reader.ReadSnapshot()
        ).Snapshot.Head;
    }

    private static TimelineHeadRef ReadTimelineHead(Fixture fixture) {
        using HistoryTimelineReaderHandle reader = Assert.IsType<
            HistoryTimelineReaderOpenResult.Opened
        >(HistoryTimelineMaintenance.OpenReader(
            fixture.Path,
            fixture.Journal.BranchRefId
        )).Handle;
        return Assert.IsType<HistoryTimelineSnapshotResult.Available>(
            reader.Reader.ReadSnapshot()
        ).Head;
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private sealed record Fixture(
        string Path,
        SessionJournalEngine Journal,
        RecapGridControlAdmission Admission
    );
}
