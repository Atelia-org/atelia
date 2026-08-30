using System.Buffers;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Galatea.Prompts;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Atelia.Galatea.Server.Tests;

internal sealed class GalateaTestHost : IAsyncDisposable {
    private const string TestUserId = "alice";
    private const string TestPassword = "pw1";
    private static string ComposeDefaultFinalizedSystemPrompt(
        bool outboundMailEnabled,
        bool characterNoteRequestEnabled
    ) => GalateaSystemPromptComposer.Compose(
        "test ${characterName} system prompt",
        new GalateaCharacterName("Galatea"),
        new GalateaPlayerName("刘世超"),
        outboundMailEnabled,
        characterNoteRequestEnabled,
        GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes
    );

    private readonly string _tempRoot;
    private readonly bool _deleteFilesOnDispose;
    private bool _disposeCompleted;
    private bool _restartCreated;

    private GalateaTestHost(
        string tempRoot,
        string sessionDirectory,
        string configPath,
        ICompletionClientFactory completionClientFactory,
        IGalateaUserMessageNormalizer? normalizer,
        IGalateaDurableDelegateTransport? delegateTransport,
        IGalateaPlayerTurnRecallProvider? playerTurnRecallProvider,
        bool deleteFilesOnDispose
    ) {
        _tempRoot = tempRoot;
        _deleteFilesOnDispose = deleteFilesOnDispose;
        SessionDirectory = sessionDirectory;
        ConfigPath = configPath;
        Factory = new GalateaWebApplicationFactory(
            configPath,
            completionClientFactory,
            normalizer,
            delegateTransport,
            playerTurnRecallProvider
        );
    }

    public GalateaWebApplicationFactory Factory { get; }

    internal string RootDirectory => _tempRoot;

    internal string SessionDirectory { get; }

    internal string DelegationStateDirectory => Path.Combine(
        Path.GetDirectoryName(ConfigPath)
            ?? throw new InvalidOperationException(
                "The test config path has no parent directory."
            ),
        "delegation-state",
        TestUserId
    );

    internal string ConfigPath { get; }

    public static GalateaTestHost Create(
        ICompletionClientFactory completionClientFactory,
        IGalateaUserMessageNormalizer? normalizer,
        bool deleteFilesOnDispose = true,
        string? callLogDirectory = null,
        bool maintenanceMode = false,
        RecapGridAgentControlProfile? agentControlProfile = null,
        bool provisionRawOnly = true,
        IReadOnlyList<CompletionConnectionConfig>? connections = null,
        IReadOnlyList<string>? selectableConnectionIds = null,
        string? inputNormalizerConnectionId = null,
        string? outboundMailExtractorConnectionId = null,
        string? characterNoteExtractorConnectionId = null,
        IGalateaDurableDelegateTransport? delegateTransport = null,
        IGalateaPlayerTurnRecallProvider? playerTurnRecallProvider = null
    ) {
        ArgumentNullException.ThrowIfNull(completionClientFactory);

        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-server-tests",
            Guid.NewGuid().ToString("N")
        );
        string configDirectory = Path.Combine(
            tempRoot,
            ".atelia",
            "galatea"
        );
        Directory.CreateDirectory(configDirectory);

        string sessionDirectory = Path.Combine(tempRoot, "session");
        using (SessionJournalEngine engine = SessionJournalEngine.Create(
                   sessionDirectory,
                   new SessionCreateOptions(
                       "model-a",
                       ComposeDefaultFinalizedSystemPrompt(
                           outboundMailExtractorConnectionId is not null,
                           characterNoteExtractorConnectionId is not null
                       ),
                       "openai-chat/strict"
                   ))) {
            if (provisionRawOnly) {
                ProvisionRawOnlyRecapGrid(engine);
            }
        }
        IReadOnlyList<CompletionConnectionConfig> configuredConnections =
            connections ?? [
                new CompletionConnectionConfig(
                    "test",
                    "openai-chat",
                    "model-a",
                    "openai-chat/strict",
                    "http://localhost:8000/",
                    ApiKey: "test-key"
                )
            ];
        string configPath = WriteConfiguration(
            configDirectory,
            Path.GetFullPath(sessionDirectory),
            configuredConnections,
            "test",
            "test ${characterName} system prompt",
            callLogDirectory,
            maintenanceMode,
            agentControlProfile,
            selectableConnectionIds: selectableConnectionIds,
            inputNormalizerConnectionId: inputNormalizerConnectionId,
            outboundMailExtractorConnectionId:
                outboundMailExtractorConnectionId,
            characterNoteExtractorConnectionId:
                characterNoteExtractorConnectionId
        );

        return new GalateaTestHost(
            tempRoot,
            sessionDirectory,
            configPath,
            completionClientFactory,
            normalizer,
            delegateTransport,
            playerTurnRecallProvider,
            deleteFilesOnDispose
        );
    }

    /// <summary>
    /// Creates an isolated Galatea configuration whose session path does not
    /// exist. The configured provisioning policy decides what the host does
    /// when that user's session is first requested.
    /// </summary>
    public static GalateaTestHost CreateMissingSession(
        ICompletionClientFactory completionClientFactory,
        IGalateaUserMessageNormalizer normalizer,
        GalateaSessionProvisioning sessionProvisioning =
            GalateaSessionProvisioning.CreateIfMissing,
        IReadOnlyList<CompletionConnectionConfig>? connections = null,
        string defaultConnectionId = "test",
        string characterContextTemplate =
            "test ${characterName} system prompt",
        bool maintenanceMode = false,
        bool deleteFilesOnDispose = true,
        RecapGridAgentControlProfile? agentControlProfile = null
    ) {
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultConnectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            characterContextTemplate
        );

        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-missing-session-tests",
            Guid.NewGuid().ToString("N")
        );
        string configDirectory = Path.Combine(
            tempRoot,
            ".atelia",
            "galatea"
        );
        Directory.CreateDirectory(configDirectory);
        string sessionDirectory = Path.Combine(tempRoot, "session");
        IReadOnlyList<CompletionConnectionConfig> configuredConnections =
            connections ?? [
                new CompletionConnectionConfig(
                    "test",
                    "openai-chat",
                    "model-a",
                    "openai-chat/strict",
                    "http://localhost:8000/",
                    ApiKey: "test-key"
                )
            ];

        try {
            string configPath = WriteConfiguration(
                configDirectory,
                Path.GetFullPath(sessionDirectory),
                configuredConnections,
                defaultConnectionId,
                characterContextTemplate,
                callLogDirectory: null,
                maintenanceMode,
                agentControlProfile,
                sessionProvisioning
            );
            return new GalateaTestHost(
                tempRoot,
                sessionDirectory,
                configPath,
                completionClientFactory,
                normalizer,
                delegateTransport: null,
                playerTurnRecallProvider: null,
                deleteFilesOnDispose
            );
        }
        catch {
            Directory.Delete(tempRoot, recursive: true);
            throw;
        }
    }

    /// <summary>
    /// Creates only an isolated Galatea configuration root and points it at an
    /// already provisioned SessionJournal repository. The repository is never
    /// created, initialized, or owned by this host and therefore is never
    /// removed during disposal.
    /// </summary>
    public static GalateaTestHost OpenExisting(
        string sessionDirectory,
        IReadOnlyList<CompletionConnectionConfig> connections,
        string defaultConnectionId,
        ICompletionClientFactory completionClientFactory,
        IGalateaUserMessageNormalizer normalizer,
        string characterContextTemplate =
            "test ${characterName} system prompt",
        string? callLogDirectory = null,
        bool maintenanceMode = false,
        RecapGridAgentControlProfile? agentControlProfile = null,
        GalateaSessionProvisioning sessionProvisioning =
            GalateaSessionProvisioning.ExistingOnly,
        IReadOnlyList<string>? selectableConnectionIds = null,
        string? inputNormalizerConnectionId = null,
        string? outboundMailExtractorConnectionId = null,
        string? characterNoteExtractorConnectionId = null
    ) => PointAtSessionCore(
        sessionDirectory,
        connections,
        defaultConnectionId,
        completionClientFactory,
        normalizer,
        characterContextTemplate,
        callLogDirectory,
        maintenanceMode,
        agentControlProfile,
        sessionProvisioning,
        requireExistingDirectory: true,
        selectableConnectionIds,
        inputNormalizerConnectionId,
        outboundMailExtractorConnectionId,
        characterNoteExtractorConnectionId
    );

    /// <summary>
    /// Creates an isolated configuration root pointing at an external session
    /// path without requiring that path to exist yet.
    /// </summary>
    public static GalateaTestHost PointAtSession(
        string sessionDirectory,
        IReadOnlyList<CompletionConnectionConfig> connections,
        string defaultConnectionId,
        ICompletionClientFactory completionClientFactory,
        IGalateaUserMessageNormalizer normalizer,
        string characterContextTemplate,
        GalateaSessionProvisioning sessionProvisioning,
        IReadOnlyList<string>? selectableConnectionIds = null,
        string? inputNormalizerConnectionId = null,
        string? outboundMailExtractorConnectionId = null,
        string? characterNoteExtractorConnectionId = null
    ) => PointAtSessionCore(
        sessionDirectory,
        connections,
        defaultConnectionId,
        completionClientFactory,
        normalizer,
        characterContextTemplate,
        callLogDirectory: null,
        maintenanceMode: false,
        agentControlProfile: null,
        sessionProvisioning,
        requireExistingDirectory: false,
        selectableConnectionIds,
        inputNormalizerConnectionId,
        outboundMailExtractorConnectionId,
        characterNoteExtractorConnectionId
    );

    private static GalateaTestHost PointAtSessionCore(
        string sessionDirectory,
        IReadOnlyList<CompletionConnectionConfig> connections,
        string defaultConnectionId,
        ICompletionClientFactory completionClientFactory,
        IGalateaUserMessageNormalizer normalizer,
        string characterContextTemplate,
        string? callLogDirectory,
        bool maintenanceMode,
        RecapGridAgentControlProfile? agentControlProfile,
        GalateaSessionProvisioning sessionProvisioning,
        bool requireExistingDirectory,
        IReadOnlyList<string>? selectableConnectionIds,
        string? inputNormalizerConnectionId,
        string? outboundMailExtractorConnectionId,
        string? characterNoteExtractorConnectionId
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultConnectionId);
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            characterContextTemplate
        );

        string absoluteSessionDirectory =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(sessionDirectory)
            );
        if (requireExistingDirectory
            && !Directory.Exists(absoluteSessionDirectory)) {
            throw new DirectoryNotFoundException(
                absoluteSessionDirectory
            );
        }

        string configurationRoot = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-existing-repo-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(configurationRoot);
        try {
            string configPath = WriteConfiguration(
                configurationRoot,
                absoluteSessionDirectory,
                connections,
                defaultConnectionId,
                characterContextTemplate,
                callLogDirectory,
                maintenanceMode,
                agentControlProfile,
                sessionProvisioning,
                selectableConnectionIds,
                inputNormalizerConnectionId,
                outboundMailExtractorConnectionId,
                characterNoteExtractorConnectionId
            );
            return new GalateaTestHost(
                configurationRoot,
                absoluteSessionDirectory,
                configPath,
            completionClientFactory,
            normalizer,
            delegateTransport: null,
            playerTurnRecallProvider: null,
            deleteFilesOnDispose: true
        );
        }
        catch {
            Directory.Delete(configurationRoot, recursive: true);
            throw;
        }
    }

    public HttpClient CreateClient() => Factory.CreateClient(
        new WebApplicationFactoryClientOptions {
            AllowAutoRedirect = false,
            HandleCookies = true
        }
    );

    /// <summary>
    /// Reopens this host's exact configuration and durable directories after
    /// the previous web host has released their process-lifetime locks. The
    /// previous host must have been created as a non-owner with
    /// <c>deleteFilesOnDispose: false</c>; the final restarted host normally
    /// becomes the single owner responsible for deleting the shared root.
    /// </summary>
    internal GalateaTestHost CreateRestarted(
        ICompletionClientFactory completionClientFactory,
        IGalateaUserMessageNormalizer? normalizer,
        IGalateaDurableDelegateTransport delegateTransport,
        bool deleteFilesOnDispose = true
    ) {
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        ArgumentNullException.ThrowIfNull(delegateTransport);
        if (!_disposeCompleted) {
            throw new InvalidOperationException(
                "The previous Galatea test host must be disposed before restart."
            );
        }
        if (_deleteFilesOnDispose) {
            throw new InvalidOperationException(
                "A Galatea test host that owned root deletion cannot be restarted."
            );
        }
        if (_restartCreated) {
            throw new InvalidOperationException(
                "This Galatea test host already created its restart successor."
            );
        }
        if (!File.Exists(ConfigPath)) {
            throw new FileNotFoundException(
                "The Galatea test configuration is unavailable for restart.",
                ConfigPath
            );
        }

        var restarted = new GalateaTestHost(
            _tempRoot,
            SessionDirectory,
            ConfigPath,
            completionClientFactory,
            normalizer,
            delegateTransport,
            playerTurnRecallProvider: null,
            deleteFilesOnDispose
        );
        _restartCreated = true;
        return restarted;
    }

    public static Task<HttpResponseMessage> LoginAsync(
        HttpClient client
    ) {
        ArgumentNullException.ThrowIfNull(client);
        return client.PostAsync(
            "/login",
            new FormUrlEncodedContent(new Dictionary<string, string> {
                ["userId"] = TestUserId,
                ["password"] = TestPassword
            })
        );
    }

    public async ValueTask DisposeAsync() {
        await Factory.DisposeAsync().ConfigureAwait(false);
        if (_deleteFilesOnDispose && Directory.Exists(_tempRoot)) {
            Directory.Delete(_tempRoot, recursive: true);
        }
        _disposeCompleted = true;
    }

    private static string WriteConfiguration(
        string configurationDirectory,
        string absoluteSessionDirectory,
        IReadOnlyList<CompletionConnectionConfig> connections,
        string defaultConnectionId,
        string characterContextTemplate,
        string? callLogDirectory,
        bool maintenanceMode,
        RecapGridAgentControlProfile? agentControlProfile,
        GalateaSessionProvisioning sessionProvisioning =
            GalateaSessionProvisioning.ExistingOnly,
        IReadOnlyList<string>? selectableConnectionIds = null,
        string? inputNormalizerConnectionId = null,
        string? outboundMailExtractorConnectionId = null,
        string? characterNoteExtractorConnectionId = null
    ) {
        string agentControlProfileFile = "recap-grid-profile.json";
        RecapGridAgentControlProfile profile = agentControlProfile
            ?? AssertBuiltInProfile();
        File.WriteAllBytes(
            Path.Combine(
                configurationDirectory,
                agentControlProfileFile
            ),
            profile.ToCanonicalBytes()
        );
        var users = new GalateaUsersFileConfig(
            Version: GalateaStrictConfigReader.CurrentConfigVersion,
            Users: [
                new GalateaUserFileConfig(
                    TestUserId,
                    TestPassword,
                    "Galatea",
                    "刘世超",
                    absoluteSessionDirectory,
                    Path.Combine(
                        configurationDirectory,
                        "delegation-state",
                        TestUserId
                    ),
                    sessionProvisioning,
                    CharacterContextTemplate: characterContextTemplate
                )
            ],
            CallLogDir: callLogDirectory,
            MaintenanceMode: maintenanceMode,
            RecapGrid: new GalateaRecapGridFileConfig(
                "recap-grid-routes.json",
                [agentControlProfileFile],
                profile.ProfileId
            )
        );
        var jsonOptions = new JsonSerializerOptions(
            JsonSerializerDefaults.Web
        );
        string configPath = Path.Combine(
            configurationDirectory,
            "config.json"
        );
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(users, jsonOptions)
        );
        WriteConnectionsFile(
            Path.Combine(
                configurationDirectory,
                GalateaConfigLoader.ConnectionsFileName
            ),
            connections,
            defaultConnectionId,
            selectableConnectionIds,
            inputNormalizerConnectionId,
            outboundMailExtractorConnectionId,
            characterNoteExtractorConnectionId
        );
        WriteDelegatesFile(configurationDirectory);
        return configPath;
    }

    internal static void WriteDelegatesFile(string configurationDirectory) {
        string processPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Environment.ProcessPath
                ?? throw new InvalidOperationException(
                    "The test process executable is unavailable."
                ))
        );
        string executable = new FileInfo(processPath)
            .ResolveLinkTarget(returnFinalTarget: true)?.FullName
            ?? processPath;
        string entryPoint = Path.GetFullPath(
            typeof(GalateaTestHost).Assembly.Location
        );
        string cwd = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(configurationDirectory)
        );
        File.WriteAllText(
            Path.Combine(
                configurationDirectory,
                GalateaConfigLoader.DelegatesFileName
            ),
            $$"""
            {
              "v": 2,
              "sidecar": {
                "nodeCommand": {{JsonSerializer.Serialize(executable)}},
                "entryPoint": {{JsonSerializer.Serialize(entryPoint)}},
                "codexCommand": {{JsonSerializer.Serialize(executable)}},
                "rpcTimeoutMs": 1000,
                "turnTimeoutMs": 1000,
                "shutdownGraceMs": 100,
                "maximumFrameUtf8Bytes": 1048576
              },
              "allowedRoots": [{{JsonSerializer.Serialize(cwd)}}],
              "routes": [
                {
                  "recipient": "Codex",
                  "kind": "codex-app-server",
                  "cwd": {{JsonSerializer.Serialize(cwd)}},
                  "mode": "work",
                  "localCommandNetwork": false,
                  "tools": {
                    "webSearch": "live",
                    "imageGeneration": true,
                    "viewImage": true
                  },
                  "maximumQueuedMails": 16,
                  "maximumTaskUtf8Bytes": 100000,
                  "maximumReplyUtf8Bytes": 100000,
                  "maximumInboxReplies": 16,
                  "maximumInboxUtf8Bytes": 1048576
                }
              ]
            }
            """
        );
    }

    internal static void WriteConnectionsFile(
        string path,
        IReadOnlyList<CompletionConnectionConfig> connections,
        string defaultConnectionId,
        IReadOnlyList<string>? selectableConnectionIds = null,
        string? inputNormalizerConnectionId = null,
        string? outboundMailExtractorConnectionId = null,
        string? characterNoteExtractorConnectionId = null
    ) {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output)) {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteStartArray("connections");
            foreach (CompletionConnectionConfig connection in connections) {
                if (!string.IsNullOrWhiteSpace(connection.BaseAddressEnv)
                    || !string.IsNullOrWhiteSpace(connection.ApiKeyEnv)) {
                    throw new InvalidOperationException(
                        "Galatea test fixtures use explicit inline sources."
                    );
                }
                writer.WriteStartObject();
                writer.WriteString("id", connection.Id);
                writer.WriteString("kind", connection.Kind);
                writer.WriteString("modelId", connection.ModelId);
                writer.WriteString(
                    "completionSurfaceId",
                    connection.CompletionSurfaceId
                );
                writer.WriteString("baseAddress", connection.BaseAddress);
                if (!string.IsNullOrWhiteSpace(connection.ApiKey)) {
                    writer.WriteString("apiKey", connection.ApiKey);
                }
                if (connection.MaxTokens is int maxTokens) {
                    writer.WriteNumber("maxTokens", maxTokens);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("defaultConnectionId", defaultConnectionId);
            writer.WriteStartArray("selectableConnectionIds");
            foreach (string connectionId
                     in selectableConnectionIds
                        ?? connections.Select(static value => value.Id)) {
                writer.WriteStringValue(connectionId);
            }
            writer.WriteEndArray();
            writer.WriteStartObject("bindings");
            if (inputNormalizerConnectionId is null) {
                writer.WriteNull(
                    GalateaCompletionOwner.InputNormalizerBindingKey
                );
            }
            else {
                writer.WriteString(
                    GalateaCompletionOwner.InputNormalizerBindingKey,
                    inputNormalizerConnectionId
                );
            }
            if (outboundMailExtractorConnectionId is null) {
                writer.WriteNull(
                    GalateaCompletionOwner
                        .OutboundMailExtractorBindingKey
                );
            }
            else {
                writer.WriteString(
                    GalateaCompletionOwner
                        .OutboundMailExtractorBindingKey,
                    outboundMailExtractorConnectionId
                );
            }
            if (characterNoteExtractorConnectionId is null) {
                writer.WriteNull(
                    GalateaCompletionOwner
                        .CharacterNoteExtractorBindingKey
                );
            }
            else {
                writer.WriteString(
                    GalateaCompletionOwner
                        .CharacterNoteExtractorBindingKey,
                    characterNoteExtractorConnectionId
                );
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        byte[] bytes = output.WrittenSpan.ToArray();
        _ = CompletionConnectionConfigLoader.Decode(bytes);
        File.WriteAllBytes(path, bytes);
    }

    private static RecapGridAgentControlProfile AssertBuiltInProfile() {
        if (!RecapGridAgentControlBuiltIns.TryCreateRegistrationBundle(
                RecapGridAgentControlBuiltIns.MysteryInvestigationV4,
                out RecapGridControlRegistrationBundle? bundle)
            || bundle is null) {
            throw new InvalidOperationException(
                "The code-owned RecapGrid test asset is unavailable."
            );
        }
        return RecapGridAgentControlProfile.Create(
            "test-profile",
            new RecapGridControlAdmission(
                RecapGridControlPermission.All,
                bundle.Families.Select(static value => value.Digest),
                bundle.Definitions.Select(static value =>
                    value.Capability.CapabilityFingerprint).Distinct(),
                [ContextHeaderCarrier.System],
                ["case."],
                maximumBootstrapRows: 64,
                maximumProjectedCalls: 1_024
            )
        );
    }

    private static void ProvisionRawOnlyRecapGrid(
        SessionJournalEngine engine
    ) {
        HistoryTimelineCreateResult timeline =
            HistoryTimelineFactory.Create(
                engine.ReadView,
                new HistoryTimelineInitialPolicySpec(
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    new HistoryLoadUnit(1),
                    maxRawEvents: 64,
                    maxRenderedBytes: 1024 * 1024
                ),
                new O200kBaseHistoryUnitLoadEstimator()
            );
        if (timeline is not HistoryTimelineCreateResult.Created) {
            throw new InvalidOperationException(
                $"The Galatea test Timeline could not be provisioned: "
                + timeline.GetType().Name
            );
        }

        RecapGridCadenceCreateResult cadence =
            RecapGridCadenceFactory.Create(
                engine,
                new RecapGridCadencePolicySpec(
                    minimumRecentHistoryLoad: 1,
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    targetHistoryLoad: 1,
                    maxRawEvents: 64,
                    maxRenderedBytes: 1024 * 1024
                )
            );
        if (cadence is not RecapGridCadenceCreateResult.Created) {
            throw new InvalidOperationException(
                $"The Galatea test Cadence could not be provisioned: "
                + cadence.GetType().Name
            );
        }

        RecapGridControlCreateResult control =
            RecapGridControlFactory.Create(
                engine.Path,
                engine.BranchRefId,
                new RecapGridControlAdmission(
                    RecapGridControlPermission.Create,
                    Array.Empty<FamilyDefinitionDigest>(),
                    Array.Empty<string>(),
                    Array.Empty<ContextHeaderCarrier>(),
                    ["test."],
                    maximumBootstrapRows: 64,
                    maximumProjectedCalls: 1_024
                )
            );
        if (control is not RecapGridControlCreateResult.Created) {
            throw new InvalidOperationException(
                $"The Galatea test Control could not be provisioned: "
                + control.GetType().Name
            );
        }
    }
}

internal sealed class GalateaWebApplicationFactory(
    string configPath,
    ICompletionClientFactory completionClientFactory,
    IGalateaUserMessageNormalizer? normalizer,
    IGalateaDurableDelegateTransport? delegateTransport,
    IGalateaPlayerTurnRecallProvider? playerTurnRecallProvider
) : WebApplicationFactory<Program> {
    protected override void ConfigureWebHost(IWebHostBuilder builder) {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Galatea:ConfigPath", configPath);
        builder.ConfigureTestServices(services => {
            services.RemoveAll<ICompletionClientFactory>();
            services.AddSingleton(completionClientFactory);
            if (normalizer is not null) {
                services.RemoveAll<IGalateaUserMessageNormalizerFactory>();
                services.AddSingleton<IGalateaUserMessageNormalizerFactory>(
                    new FixedNormalizerFactory(normalizer)
                );
            }
            if (delegateTransport is not null
                || playerTurnRecallProvider is not null) {
                services.RemoveAll<GalateaHostService>();
                services.AddSingleton(provider =>
                    new GalateaHostService(
                        provider.GetRequiredService<GalateaConfig>(),
                        provider.GetRequiredService<
                            ICompletionClientFactory>(),
                        provider.GetRequiredService<
                            IGalateaUserMessageNormalizerFactory>(),
                        delegateTransport,
                        playerTurnRecallProvider
                    ));
            }
        });
    }

    private sealed class FixedNormalizerFactory(
        IGalateaUserMessageNormalizer normalizer
    ) : IGalateaUserMessageNormalizerFactory {
        public IGalateaUserMessageNormalizer Create(
            CompletionConnectionConfig? connection,
            Func<Atelia.Completion.Abstractions.ICompletionClient> getClient
        ) {
            _ = connection;
            ArgumentNullException.ThrowIfNull(getClient);
            return normalizer;
        }
    }
}
