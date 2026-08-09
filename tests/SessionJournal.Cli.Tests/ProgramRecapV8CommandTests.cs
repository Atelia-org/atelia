using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramRecapV8CommandTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-v8-cli-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void PlannerConfigInitWritesCanonicalV3AndRejectsOldField() {
        Directory.CreateDirectory(_root);
        Assert.Equal(0, Program.Main([
            "recap", "planner-config", "init", "--input", _root
        ]));
        string path = RecapEpochConfigLoader.GetCanonicalPath(_root);
        byte[] canonical = File.ReadAllBytes(path);
        RecapEpochConfigDocument document =
            RecapEpochConfigCodec.Decode(canonical);
        Assert.Equal(RecapEpochConfigCodec.SchemaV3, document.Schema);
        Assert.Equal(0, Program.Main([
            "recap", "planner-config", "inspect", "--input", _root
        ]));

        RecapEpochConfigDocument changedStoreLimit = document with {
            Limits = document.Limits with {
                MaxPublicationBytes =
                    document.Limits.MaxPublicationBytes - 1
            }
        };
        File.WriteAllBytes(
            path,
            RecapEpochConfigCodec.Encode(changedStoreLimit)
        );
        Assert.Equal(2, Program.Main([
            "recap", "planner-config", "inspect", "--input", _root
        ]));

        string old = Encoding.UTF8.GetString(canonical).Replace(
            "\"maxMaintainerCallsPerEpoch\":2",
            "\"maxMaintainerCallsPerBuild\":2",
            StringComparison.Ordinal
        );
        File.WriteAllText(path, old, new UTF8Encoding(false));
        Assert.Equal(2, Program.Main([
            "recap", "planner-config", "inspect", "--input", _root
        ]));
    }

    [Fact]
    public void RunAndExplicitResetRebuildEnforceConfirmationAndSealAuthority() {
        Directory.CreateDirectory(_root);
        string repository = Path.Combine(_root, "repo");
        string connections = Path.Combine(_root, "connections.json");
        string refId;
        using (SessionJournalEngine engine = SessionJournalEngine.Create(
            repository,
            new SessionCreateOptions("model-a", "system-a", "surface-a")
        )) {
            refId = engine.BranchRefId.ToHexString();
        }
        File.WriteAllText(
            connections,
            """
            {
              "defaultConnectionId": "test",
              "connections": [{
                "id": "test",
                "kind": "openai-chat",
                "modelId": "model-a",
                "completionSurfaceId": "surface-a",
                "baseAddress": "https://example.invalid"
              }]
            }
            """
        );
        string[] common = [
            "--input", repository,
            "--branch", "main",
            "--connections", connections
        ];
        Assert.Equal(0, Program.MainCore([
            "recap", "run", .. common
        ], ThrowingCompletionClientFactory.Instance));
        string storeRoot = Path.Combine(
            repository,
            "derived",
            "recap",
            "v8",
            "refs",
            refId
        );
        string marker = Path.Combine(storeRoot, "reset-marker");
        File.WriteAllText(marker, "must disappear only after confirmation");
        string campaign = Guid.NewGuid().ToString("N");

        Assert.Equal(1, Program.MainCore([
            "recap", "rebuild", .. common,
            "--campaign", campaign,
            "--reset",
            "--confirm-ref", new string('0', 32)
        ], ThrowingCompletionClientFactory.Instance));
        Assert.True(File.Exists(marker));

        Assert.Equal(0, Program.MainCore([
            "recap", "rebuild", .. common,
            "--campaign", campaign,
            "--reset",
            "--confirm-ref", refId
        ], ThrowingCompletionClientFactory.Instance));
        Assert.False(File.Exists(marker));
        Assert.True(File.Exists(Path.Combine(
            repository,
            "derived",
            "recap",
            "rebuild",
            "v1",
            "campaigns",
            refId,
            campaign,
            "seal.json"
        )));
        string resumeMarker = Path.Combine(
            storeRoot,
            "same-campaign-resume-marker"
        );
        File.WriteAllText(resumeMarker, "must survive non-reset resume");
        Assert.Equal(0, Program.MainCore([
            "recap", "rebuild", .. common,
            "--campaign", campaign
        ], ThrowingCompletionClientFactory.Instance));
        Assert.True(File.Exists(resumeMarker));
    }

    [Fact]
    public async Task ExplicitResetRebuildResumesMultiEpochWithoutChangingRawAuthority() {
        Directory.CreateDirectory(_root);
        string repository = Path.Combine(_root, "multi-epoch-repo");
        string connections = Path.Combine(
            _root,
            "multi-epoch-connections.json"
        );
        string failureLogs = Path.Combine(_root, "failed-online-logs");
        string rebuildLogs = Path.Combine(_root, "rebuild-logs");
        string firstReport = Path.Combine(_root, "first-report.json");
        string secondReport = Path.Combine(_root, "second-report.json");
        string refId;
        using (SessionJournalEngine engine = SessionJournalEngine.Create(
            repository,
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "openai-chat/strict"
            )
        )) {
            refId = engine.BranchRefId.ToHexString();
            for (int index = 0; index < 7; index++) {
                _ = engine.AppendObservation($"observation-{index}");
                _ = engine.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"action-{index}")
                    ]),
                    new CompletionDescriptor(
                        "import",
                        "test",
                        "model-a"
                    )
                );
            }
        }
        WriteMultiEpochConfig(repository);
        File.WriteAllText(
            connections,
            """
            {
              "defaultConnectionId": "test",
              "connections": [{
                "id": "test",
                "kind": "openai-chat",
                "modelId": "model-a",
                "completionSurfaceId": "openai-chat/strict",
                "baseAddress": "https://example.invalid"
              }]
            }
            """
        );
        DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
            repository,
            RefId.ParseHex(refId).Value
        );
        await store.EnsureCreatedAsync();
        string storeRoot = Path.Combine(
            repository,
            "derived",
            "recap",
            "v8",
            "refs",
            refId
        );
        string marker = Path.Combine(storeRoot, "pre-reset-marker");
        File.WriteAllText(marker, "existing sidecar state");
        RawAuthoritySnapshot rawBefore = CaptureRawAuthority(repository);
        string campaign = Guid.NewGuid().ToString("N");
        string campaignRoot = Path.Combine(
            repository,
            "derived",
            "recap",
            "rebuild",
            "v1",
            "campaigns",
            refId,
            campaign
        );
        string[] common = [
            "--input", repository,
            "--branch", "main",
            "--connections", connections
        ];

        var neverCalled = new ScriptedRecapClientFactory(valid: true);
        Assert.Equal(1, Program.MainCore([
            "recap", "rebuild", .. common,
            "--campaign", campaign,
            "--reset",
            "--confirm-ref", new string('0', 32),
            "--call-log-dir", rebuildLogs
        ], neverCalled));
        Assert.Equal(0, neverCalled.CreateCallCount);
        Assert.True(File.Exists(marker));
        Assert.False(Directory.Exists(campaignRoot));
        AssertRawAuthorityUnchanged(rawBefore, repository);

        var invalid = new ScriptedRecapClientFactory(valid: false);
        Assert.Equal(2, Program.MainCore([
            "recap", "run", .. common,
            "--call-log-dir", failureLogs
        ], invalid));
        Assert.Equal(1, invalid.CreateCallCount);
        Assert.True(invalid.MaintainerCallCount >= 1);
        Assert.IsType<RecapEpochBuildingSelectionResult.Selected>(
            await store.SelectBuildingAsync()
        );
        Assert.True(File.Exists(marker));
        AssertRawAuthorityUnchanged(rawBefore, repository);

        var valid = new ScriptedRecapClientFactory(valid: true);
        Assert.Equal(3, Program.MainCore([
            "recap", "rebuild", .. common,
            "--campaign", campaign,
            "--reset",
            "--confirm-ref", refId,
            "--call-log-dir", rebuildLogs,
            "--report-json", firstReport
        ], valid));
        Assert.False(File.Exists(marker));
        Assert.True(File.Exists(Path.Combine(campaignRoot, "seal.json")));
        Assert.Equal("MoreWorkPending", ReadStatus(firstReport));
        Assert.Equal(campaign, ReadCampaign(firstReport));
        Assert.Equal(1, valid.CreateCallCount);
        Assert.Equal(4, valid.MaintainerCallCount);
        Assert.Equal(
            2,
            (await store.ListPublishedAnchorsAsync()).Count
        );
        AssertRawAuthorityUnchanged(rawBefore, repository);

        Assert.Equal(0, Program.MainCore([
            "recap", "rebuild", .. common,
            "--campaign", campaign,
            "--call-log-dir", rebuildLogs,
            "--report-json", secondReport
        ], valid));
        Assert.Equal("Fresh", ReadStatus(secondReport));
        Assert.Equal(campaign, ReadCampaign(secondReport));
        Assert.Equal(2, valid.CreateCallCount);
        Assert.Equal(8, valid.MaintainerCallCount);
        Assert.Equal(
            rawBefore.CapturedHead,
            ReadLatestAdmission(secondReport)
        );
        Assert.Equal(
            4,
            (await store.ListPublishedAnchorsAsync()).Count
        );
        AssertRawAuthorityUnchanged(rawBefore, repository);

        string[] logs = [
            .. Directory.EnumerateFiles(
                rebuildLogs,
                "*.json",
                SearchOption.AllDirectories
            )
        ];
        Assert.Equal(8, logs.Length);
        var maintainerIds = new HashSet<string>(StringComparer.Ordinal);
        int leaders = 0;
        int followers = 0;
        foreach (string path in logs) {
            using JsonDocument log = JsonDocument.Parse(
                File.ReadAllText(path)
            );
            JsonElement root = log.RootElement;
            Assert.Equal(
                "test",
                root.GetProperty("connection")
                    .GetProperty("id")
                    .GetString()
            );
            Assert.Equal(
                "reuseExpectedSoon",
                root.GetProperty("invocationOptions")
                    .GetProperty("promptCacheReuseHint")
                    .GetString()
            );
            JsonElement context = root.GetProperty("context");
            maintainerIds.Add(
                context.GetProperty("maintainerId").GetString()!
            );
            switch (context.GetProperty("callRole").GetString()) {
                case "Leader":
                    leaders++;
                    break;
                case "Follower":
                    followers++;
                    break;
                default:
                    throw new Xunit.Sdk.XunitException(
                        "Unexpected recap call role."
                    );
            }
        }
        Assert.Equal(
            new HashSet<string>([
                WorldUnderstandingRecapMaintainers.MaintainerId,
                AutobiographicalRecapMaintainers.MaintainerId
            ]),
            maintainerIds
        );
        Assert.Equal(4, leaders);
        Assert.Equal(4, followers);
    }

    public void Dispose() {
        try {
            if (Directory.Exists(_root)) {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch {
        }
    }

    private static void WriteMultiEpochConfig(string repository) {
        RecapEpochConfigDocument defaults =
            RecapEpochConfigCodec.Decode(
                RecapEpochConfigCodec.Encode(
                    BuiltInRecapPlannerConfig.Document
                )
            );
        RecapEpochConfigDocument document = defaults with {
            Cadence = defaults.Cadence with {
                MinimumRecentHistoryLoad = 0,
                RecapBuildIntervalHistoryLoad = 1
            },
            Limits = defaults.Limits with {
                MaxRawEventsPerEpoch = 4,
                MaxEpochsPerOperation = 2,
                MaxMaintainerCallsPerOperation = 4
            }
        };
        string path = RecapEpochConfigLoader.GetCanonicalPath(repository);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, RecapEpochConfigCodec.Encode(document));
    }

    private static string ReadStatus(string reportPath) {
        using JsonDocument report = JsonDocument.Parse(
            File.ReadAllText(reportPath)
        );
        return report.RootElement.GetProperty("resultStatus").GetString()!;
    }

    private static string ReadCampaign(string reportPath) {
        using JsonDocument report = JsonDocument.Parse(
            File.ReadAllText(reportPath)
        );
        return report.RootElement.GetProperty("campaignId").GetString()!;
    }

    private static string ReadLatestAdmission(string reportPath) {
        using JsonDocument report = JsonDocument.Parse(
            File.ReadAllText(reportPath)
        );
        return report.RootElement.GetProperty("latestAdmissionAnchor")
            .GetString()!;
    }

    private static RawAuthoritySnapshot CaptureRawAuthority(
        string repository
    ) {
        using SessionJournalEngine engine =
            SessionJournalEngine.OpenReadOnly(repository);
        SessionCurrentLineageSnapshot lineage =
            engine.ReadCurrentLineageHeaders();
        string[] hashes = [
            .. Directory.EnumerateFiles(
                    repository,
                    "*",
                    SearchOption.AllDirectories
                )
                .Select(path => (
                    Path: path,
                    Relative: Path.GetRelativePath(repository, path)
                        .Replace(Path.DirectorySeparatorChar, '/')
                ))
                .Where(static item =>
                    !item.Relative.StartsWith(
                        "derived/",
                        StringComparison.Ordinal
                    )
                )
                .Select(static item =>
                    item.Relative
                        + ":"
                        + Convert.ToHexStringLower(
                            SHA256.HashData(File.ReadAllBytes(item.Path))
                        )
                )
                .Order(StringComparer.Ordinal)
        ];
        return new RawAuthoritySnapshot(
            lineage.CapturedHead.ToString(),
            [
                .. lineage.HeadToRoot.Select(static header =>
                    header.Address.ToString()
                )
            ],
            hashes
        );
    }

    private static void AssertRawAuthorityUnchanged(
        RawAuthoritySnapshot expected,
        string repository
    ) {
        RawAuthoritySnapshot actual = CaptureRawAuthority(repository);
        Assert.Equal(expected.CapturedHead, actual.CapturedHead);
        Assert.Equal(expected.SelectedLineage, actual.SelectedLineage);
        Assert.Equal(expected.NonDerivedFileHashes, actual.NonDerivedFileHashes);
    }

    private sealed record RawAuthoritySnapshot(
        string CapturedHead,
        string[] SelectedLineage,
        string[] NonDerivedFileHashes
    );

    private sealed class ScriptedRecapClientFactory(bool valid)
        : ICompletionClientFactory {
        private int _createCallCount;
        private int _maintainerCallCount;

        internal int CreateCallCount => Volatile.Read(
            ref _createCallCount
        );

        internal int MaintainerCallCount => Volatile.Read(
            ref _maintainerCallCount
        );

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            Interlocked.Increment(ref _createCallCount);
            return new ScriptedRecapClient(this, connection.Id, valid);
        }

        private sealed class ScriptedRecapClient(
            ScriptedRecapClientFactory owner,
            string connectionId,
            bool valid
        ) : ICompletionClient {
            public string Name => $"scripted/{connectionId}";

            public string ApiSpecId => "openai-chat-v1";

            public Task<CompletionResult> StreamCompletionAsync(
                CompletionRequest request,
                CompletionStreamObserver? observer,
                CancellationToken cancellationToken = default
            ) => Complete(request, observer, cancellationToken);

            public Task<CompletionResult> StreamCompletionAsync(
                CompletionRequest request,
                CompletionInvocationOptions invocationOptions,
                CompletionStreamObserver? observer,
                CancellationToken cancellationToken = default
            ) {
                invocationOptions.Validate();
                return Complete(request, observer, cancellationToken);
            }

            private Task<CompletionResult> Complete(
                CompletionRequest request,
                CompletionStreamObserver? observer,
                CancellationToken cancellationToken
            ) {
                cancellationToken.ThrowIfCancellationRequested();
                int call = Interlocked.Increment(
                    ref owner._maintainerCallCount
                );
                ActionMessage message;
                if (valid) {
                    var toolCall = new RawToolCall(
                        StructuredRecapMaintainerOutputProtocol
                            .SubmitToolName,
                        $"call-{call}",
                        "{\"outcome\":\"updated\",\"content\":"
                            + JsonSerializer.Serialize($"recap-{call}")
                            + "}"
                    );
                    observer?.OnToolCall(toolCall);
                    message = new ActionMessage([
                        new ActionBlock.ToolCall(toolCall)
                    ]);
                }
                else {
                    const string invalidText = "invalid plain text";
                    observer?.OnTextDelta(invalidText);
                    message = new ActionMessage([
                        new ActionBlock.Text(invalidText)
                    ]);
                }
                return Task.FromResult(new CompletionResult(
                    message,
                    CompletionDescriptor.From(this, request)
                ));
            }
        }
    }

    private sealed class ThrowingCompletionClientFactory
        : ICompletionClientFactory {
        internal static ThrowingCompletionClientFactory Instance { get; } =
            new();

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => throw new InvalidOperationException(
            $"NoBuild recap command must not create '{connection.Id}'."
        );
    }
}
