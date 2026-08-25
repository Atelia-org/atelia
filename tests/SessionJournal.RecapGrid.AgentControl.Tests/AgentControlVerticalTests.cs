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
                "recap_grid_control",
                Assert.Single(handle.ToolSession.VisibleDefinitions).Name
            );
            Assert.StartsWith(
                "atelia.recap-grid.agent-control.v1",
                handle.RuntimeIdentity.HostId
            );
            string command = JsonSerializer.Serialize(new {
                action = "provision-built-in",
                builtInAssetId = RecapGridAgentControlBuiltIns
                    .MysteryInvestigationV4
            });
            ToolCallExecutionResult applied = await handle.ToolSession
                .ExecuteReservedAsync(
                    new RawToolCall(
                        "recap_grid_control",
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
                        "recap_grid_control",
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
                    RecapGridAgentControlBuiltIns.MysteryInvestigationV4,
                    out RecapGridControlRegistrationBundle? builtIn));
            string conflictingCommand = JsonSerializer.Serialize(new {
                action = "register-family",
                canonicalValueBase64 = Convert.ToBase64String(
                    builtIn!.Families[0].ToCanonicalBytes())
            });
            ToolCallExecutionResult conflict = await handle.ToolSession
                .ExecuteReservedAsync(
                    new RawToolCall(
                        "recap_grid_control",
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
            RecapGridAgentControlBuiltIns.MysteryInvestigationV4,
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
                        "recap_grid_control",
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
                            "recap_grid_control",
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
                            "recap_grid_control",
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
                        "recap_grid_control",
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
                        "recap_grid_control",
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
                        "recap_grid_control",
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
                        "recap_grid_control",
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
                "206c09481894b95c86fa15f531254f5ef48a52a74d214f32b5f06ea97d837ce3",
                first.RuntimeIdentity.ImplementationSetFingerprint
            );
            Assert.Equal(
                [RecapGridAgentControlBuiltIns.MysteryInvestigationV4],
                RecapGridAgentControlBuiltIns.AssetIds
            );
            Assert.True(RecapGridAgentControlBuiltIns
                .TryCreateRegistrationBundle(
                    RecapGridAgentControlBuiltIns.MysteryInvestigationV4,
                    out RecapGridControlRegistrationBundle? one));
            Assert.True(RecapGridAgentControlBuiltIns
                .TryCreateRegistrationBundle(
                    RecapGridAgentControlBuiltIns.MysteryInvestigationV4,
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
                "5a3b24dd662302d61ad58ba8242e7969c9baab7339bc88aa285946840051b68f",
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
                    "a6f8e36dd4cec94c645b4ee38f414a7a1b574b583ded444fe904c3fe1aea4395",
                    "4274736deeba8156ae020a3db752519f4c257b4a021ceaad8d2e067002724da6"
                ],
                one.Definitions.Select(static value => value.Digest.Value)
            );
            Assert.Equal(
                [
                    "Derived context from prior history: culprit hypothesis",
                    "Derived context from prior history: suspicion about X"
                ],
                one.Definitions.Select(static value =>
                    value.Target.SemanticHeading)
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
    public void ProfileAndAdmissionByteGatesAreInclusiveAndOwnerCanonical() {
        const int MaximumProfileBytes = 128 * 1024;
        const int MaximumAdmissionBytes = 64 * 1024;
        RecapGridControlAdmission admission = CreateAdmission();
        byte[] admissionBytes = admission.ToCanonicalBytes();

        Assert.InRange(admissionBytes.Length, 2, MaximumAdmissionBytes);
        Assert.Equal(
            admissionBytes,
            RecapGridControlAdmission.DecodeCanonical(admissionBytes)
                .ToCanonicalBytes()
        );
        RecapGridAgentControlProfile profile =
            RecapGridAgentControlProfile.Create("profile", admission);
        Assert.InRange(profile.ToCanonicalBytes().Length, 1,
            MaximumProfileBytes);
        Assert.Equal(
            profile.ToCanonicalBytes(),
            RecapGridAgentControlProfile.DecodeCanonical(
                profile.ToCanonicalBytes()
            ).ToCanonicalBytes()
        );

        byte[] admissionMinimumBytes = "xx"u8.ToArray();
        InvalidDataException admissionMinimum = Assert.Throws<
            InvalidDataException>(() =>
                RecapGridControlAdmission.DecodeCanonical(
                    admissionMinimumBytes
                ));
        Assert.IsType<JsonException>(admissionMinimum.InnerException);
        byte[] admissionBelowMinimumBytes = "x"u8.ToArray();
        InvalidDataException admissionBelowMinimum = Assert.Throws<
            InvalidDataException>(() =>
                RecapGridControlAdmission.DecodeCanonical(
                    admissionBelowMinimumBytes
                ));
        Assert.Null(admissionBelowMinimum.InnerException);

        byte[] admissionMaximum = Enumerable.Repeat(
            (byte)'x',
            MaximumAdmissionBytes
        ).ToArray();
        InvalidDataException admissionAtMaximum = Assert.Throws<
            InvalidDataException>(() =>
                RecapGridControlAdmission.DecodeCanonical(admissionMaximum));
        Assert.IsType<JsonException>(admissionAtMaximum.InnerException);
        InvalidDataException admissionAboveMaximum = Assert.Throws<
            InvalidDataException>(() =>
                RecapGridControlAdmission.DecodeCanonical([
                    .. admissionMaximum,
                    (byte)'x'
                ]));
        Assert.Null(admissionAboveMaximum.InnerException);

        byte[] profileAtMaximum = PadJsonWithSpaces(
            "{\"v\":1,\"profileId\":\"profile\","
                + "\"admissionCanonicalBase64\":\"%\"}",
            MaximumProfileBytes
        );
        InvalidDataException profileMaximum = Assert.Throws<
            InvalidDataException>(() =>
                RecapGridAgentControlProfile.DecodeCanonical(
                    profileAtMaximum
                ));
        Assert.IsType<FormatException>(profileMaximum.InnerException);
        InvalidDataException profileAboveMaximum = Assert.Throws<
            InvalidDataException>(() =>
                RecapGridAgentControlProfile.DecodeCanonical([
                    .. profileAtMaximum,
                    (byte)' '
                ]));
        Assert.Null(profileAboveMaximum.InnerException);
    }

    [Fact]
    public void ProfileIdUtf8BoundIsInclusive() {
        const int MaximumProfileIdUtf8Bytes = 128;
        string maximum = new('x', MaximumProfileIdUtf8Bytes);
        string maximumNonAscii = new('\u00e9', 64);
        RecapGridControlAdmission admission = CreateAdmission();

        RecapGridAgentControlProfile accepted =
            RecapGridAgentControlProfile.Create(maximum, admission);
        RecapGridAgentControlProfile acceptedNonAscii =
            RecapGridAgentControlProfile.Create(
                maximumNonAscii,
                admission
            );

        Assert.Equal(
            MaximumProfileIdUtf8Bytes,
            Encoding.UTF8.GetByteCount(accepted.ProfileId)
        );
        Assert.Equal(
            MaximumProfileIdUtf8Bytes,
            Encoding.UTF8.GetByteCount(acceptedNonAscii.ProfileId)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RecapGridAgentControlProfile.Create(maximum + "x", admission));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RecapGridAgentControlProfile.Create(
                maximumNonAscii + "x",
                admission
            ));
        foreach (string invalidId in new[] { " ", "profile\n", "\ud800" }) {
            Assert.Throws<ArgumentException>(() =>
                RecapGridAgentControlProfile.Create(invalidId, admission));
        }
    }

    [Theory]
    [InlineData("missing-version")]
    [InlineData("future-version")]
    [InlineData("fractional-version")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("duplicate-version")]
    [InlineData("root-order")]
    [InlineData("wrong-case")]
    [InlineData("missing-admission")]
    public void ProfileCodecRejectsNoncanonicalV1Mutations(string kind) {
        RecapGridAgentControlProfile profile =
            RecapGridAgentControlProfile.Create(
                "operator-v1",
                CreateAdmission()
            );
        string canonical = Encoding.UTF8.GetString(
            profile.ToCanonicalBytes()
        );
        string invalid = kind switch {
            "missing-version" => canonical.Replace(
                "{\"v\":1,",
                "{",
                StringComparison.Ordinal
            ),
            "future-version" => canonical.Replace(
                "\"v\":1",
                "\"v\":2",
                StringComparison.Ordinal
            ),
            "fractional-version" => canonical.Replace(
                "\"v\":1",
                "\"v\":1.0",
                StringComparison.Ordinal
            ),
            "unknown" => canonical.Replace(
                "\"profileId\":",
                "\"unknown\":1,\"profileId\":",
                StringComparison.Ordinal
            ),
            "duplicate" => canonical.Replace(
                "\"profileId\":",
                "\"profileId\":\"operator-v1\",\"profileId\":",
                StringComparison.Ordinal
            ),
            "duplicate-version" => canonical.Replace(
                "{\"v\":1,",
                "{\"v\":1,\"v\":1,",
                StringComparison.Ordinal
            ),
            "root-order" => canonical.Replace(
                "{\"v\":1,\"profileId\":\"operator-v1\",",
                "{\"profileId\":\"operator-v1\",\"v\":1,",
                StringComparison.Ordinal
            ),
            "wrong-case" => canonical.Replace(
                "\"profileId\":",
                "\"ProfileId\":",
                StringComparison.Ordinal
            ),
            "missing-admission" => RemoveFinalJsonProperty(
                canonical,
                ",\"admissionCanonicalBase64\":"
            ),
            _ => throw new InvalidOperationException()
        };
        Assert.NotEqual(canonical, invalid);

        Assert.Throws<InvalidDataException>(() =>
            RecapGridAgentControlProfile.DecodeCanonical(
                Encoding.UTF8.GetBytes(invalid)
            ));
    }

    [Fact]
    public void ProfileRegistryCountAndIdentityBoundsAreExact() {
        RecapGridAgentControlProfile[] maximum = Enumerable.Range(0, 256)
            .Select(index => RecapGridAgentControlProfile.Create(
                $"profile-{index:D3}",
                AdmissionWithProjectedCalls(index)
            ))
            .ToArray();

        var registry = new RecapGridAgentControlProfileRegistry(maximum);
        Assert.Equal(256, registry.ProfileIds.Count);
        Assert.Throws<ArgumentException>(() =>
            new RecapGridAgentControlProfileRegistry([]));
        Assert.Throws<ArgumentException>(() =>
            new RecapGridAgentControlProfileRegistry([
                .. maximum,
                RecapGridAgentControlProfile.Create(
                    "profile-overflow",
                    AdmissionWithProjectedCalls(256)
                )
            ]));

        Assert.Throws<ArgumentException>(() =>
            new RecapGridAgentControlProfileRegistry([
                RecapGridAgentControlProfile.Create(
                    "duplicate-id",
                    AdmissionWithProjectedCalls(1)
                ),
                RecapGridAgentControlProfile.Create(
                    "duplicate-id",
                    AdmissionWithProjectedCalls(2)
                )
            ]));
        RecapGridControlAdmission sharedAdmission =
            AdmissionWithProjectedCalls(3);
        Assert.Throws<ArgumentException>(() =>
            new RecapGridAgentControlProfileRegistry([
                RecapGridAgentControlProfile.Create(
                    "runtime-a",
                    sharedAdmission
                ),
                RecapGridAgentControlProfile.Create(
                    "runtime-b",
                    sharedAdmission
                )
            ]));
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
                    "recap_grid_control",
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
                "recap_grid_control",
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
                        "recap_grid_control",
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
                        "recap_grid_control",
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
                        "recap_grid_control",
                        "cancel-call",
                        "{\"action\":\"provision-built-in\",\"builtInAssetId\":\"mystery-investigation-v4\"}"
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
                            "recap_grid_control",
                            "indeterminate-call",
                            "{\"action\":\"provision-built-in\",\"builtInAssetId\":\"mystery-investigation-v4\"}"
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
                    "recap_grid_control",
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
                RecapGridAgentControlBuiltIns.MysteryInvestigationV4,
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
                RecapGridAgentControlBuiltIns.MysteryInvestigationV4,
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

    private static RecapGridControlAdmission AdmissionWithProjectedCalls(
        int maximumProjectedCalls
    ) => new(
        RecapGridControlPermission.None,
        [],
        [],
        [],
        ["case."],
        maximumBootstrapRows: 0,
        maximumProjectedCalls: maximumProjectedCalls
    );

    private static string RemoveFinalJsonProperty(
        string canonical,
        string propertyMarker
    ) {
        int propertyOffset = canonical.IndexOf(
            propertyMarker,
            StringComparison.Ordinal
        );
        Assert.True(propertyOffset >= 0);
        Assert.EndsWith("}", canonical, StringComparison.Ordinal);
        return canonical[..propertyOffset] + "}";
    }

    private static byte[] PadJsonWithSpaces(string json, int exactLength) {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        Assert.True(bytes.Length <= exactLength);
        int contentLength = bytes.Length;
        Array.Resize(ref bytes, exactLength);
        bytes.AsSpan(contentLength).Fill((byte)' ');
        return bytes;
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
