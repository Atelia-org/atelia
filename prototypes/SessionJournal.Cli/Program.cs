using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.DerivedMemory;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using SJ = Atelia.SessionJournal;
using SJO = Atelia.SessionJournal.Offline;

namespace Atelia.SessionJournal.Cli;

internal static class Program {
    private const string DefaultLlmSmokeCallLogDir =
        "gitignore/session-journal/llm-smoke-calls";
    private const string DefaultMaintainerCallLogDir =
        "gitignore/session-journal/memory-maintainer-calls";

    public static int Main(string[] args)
        => MainCore(args, new DefaultCompletionClientFactory());

    internal static int MainCore(
        string[] args,
        ICompletionClientFactory completionClientFactory
    ) {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        try {
            if (args.Length == 0 || args[0] is "-h" or "--help") {
                PrintHelp();
                return args.Length == 0 ? 1 : 0;
            }

            string command = args[0];
            if (string.Equals(
                    command,
                    "recap",
                    StringComparison.Ordinal
                )) {
                return RecapStoreCommands.RunAsync(
                        args.Skip(1).ToArray(),
                        completionClientFactory
                    )
                    .GetAwaiter()
                    .GetResult();
            }
            CliOptions options = CliOptions.Parse(args.Skip(1).ToArray());
            return command switch {
                "import-legacy-json" => RunImportLegacyJson(options),
                "validate" => RunValidateAsync(options)
                    .GetAwaiter()
                    .GetResult(),
                "llm-smoke" => RunLlmSmokeAsync(
                        options,
                        completionClientFactory
                    )
                    .GetAwaiter()
                    .GetResult(),
                "run-memory-maintainer" => RunMemoryMaintainerAsync(
                        options,
                        completionClientFactory
                    )
                    .GetAwaiter()
                    .GetResult(),
                "run-derived-memory-orchestration" =>
                    RunDerivedMemoryOrchestrationAsync(
                            options,
                            completionClientFactory
                        )
                        .GetAwaiter()
                        .GetResult(),
                "run-online-turn" => RunOnlineTurnAsync(
                        options,
                        completionClientFactory
                    )
                    .GetAwaiter()
                    .GetResult(),
                "publish-derived-artifact-set" =>
                    DerivedMemoryCommands.PublishAsync(options)
                        .GetAwaiter()
                        .GetResult(),
                "list-derived-artifact-sets" =>
                    DerivedMemoryCommands.ListAsync(options)
                        .GetAwaiter()
                        .GetResult(),
                "validate-derived-memory" =>
                    DerivedMemoryCommands.ValidateAsync(options)
                        .GetAwaiter()
                        .GetResult(),
                "rebuild-derived-artifact-set-latest" =>
                    DerivedMemoryCommands.RebuildLatestAsync(options)
                        .GetAwaiter()
                        .GetResult(),
                "configure-derived-artifact-planner" =>
                    DerivedMemoryCommands.ConfigurePlannerAsync(options)
                        .GetAwaiter()
                        .GetResult(),
                "plan-derived-artifact-epoch" =>
                    DerivedMemoryCommands.PlanEpochAsync(options)
                        .GetAwaiter()
                        .GetResult(),
                "list-derived-artifact-epochs" =>
                    DerivedMemoryCommands.ListEpochsAsync(options)
                        .GetAwaiter()
                        .GetResult(),
                _ => Fail($"Unknown command '{command}'.")
            };
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or HttpRequestException
                or InvalidDataException
                or InvalidOperationException
                or IOException
                or JsonException
                or NotSupportedException
                or TaskCanceledException
                or UnauthorizedAccessException
        ) {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int RunImportLegacyJson(CliOptions options) {
        string inputPath = options.Require("input");
        string outputPath = options.Require("output");
        string? reportPath = options.Get("report-md");
        bool force = options.HasFlag("force");

        CliIo.EnsurePathChainHasNoReparsePoint(inputPath, "--input");
        CliIo.EnsurePathChainHasNoReparsePoint(outputPath, "--output");
        CliIo.EnsurePathsDoNotOverlap(inputPath, outputPath);
        if (!string.IsNullOrWhiteSpace(reportPath)) {
            CliIo.EnsurePathChainHasNoReparsePoint(
                reportPath,
                "--report-md"
            );
            CliIo.EnsurePathsAreDifferent(
                inputPath,
                reportPath,
                "--report-md must not overwrite --input."
            );
            CliIo.EnsurePathIsOutsideRepository(
                outputPath,
                reportPath,
                "--report-md"
            );
            CliIo.EnsureFilePathIsNotAncestorOfDirectory(
                reportPath,
                outputPath,
                "--report-md must not contain --output."
            );
        }

        LegacyChatSessionExport export =
            LegacyChatSessionExportReader.Read(inputPath);
        SessionJournalLegacyImportResult result =
            SessionJournalLegacyImporter.Import(export, outputPath, force);
        SessionJournalLegacyImporter.VerifyImportedRepo(outputPath, result);

        Console.WriteLine($"schema: {export.Schema}");
        Console.WriteLine($"branchName: {export.BranchName ?? "(none)"}");
        Console.WriteLine($"output: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"sessionCreated: {result.SessionCreatedCount}");
        Console.WriteLine(
            $"runtimeConfigSetups: {result.RuntimeConfigSetupCount}"
        );
        Console.WriteLine(
            $"systemPromptSetups: {result.SystemPromptSetupCount}"
        );
        Console.WriteLine($"observations: {result.ObservationCount}");
        Console.WriteLine($"agentActions: {result.AgentActionCount}");
        Console.WriteLine(
            $"skippedCompactions: {result.SkippedCompactionCount}"
        );
        Console.WriteLine($"skippedRecaps: {result.SkippedRecapCount}");
        if (!string.IsNullOrWhiteSpace(reportPath)) {
            SessionJournalLegacyImporter.WriteReport(
                reportPath,
                inputPath,
                outputPath,
                result
            );
            Console.WriteLine($"report: {Path.GetFullPath(reportPath)}");
        }
        return 0;
    }

    private static async Task<int> RunValidateAsync(CliOptions options) {
        options.EnsureOnly("input", "branch", "report-json");
        string inputPath = options.RequireSingle("input");
        string branchName = options.GetOptionalSingle("branch")
            ?? SJ.SessionJournalDefaults.MainBranchName;
        string? reportPath =
            options.GetOptionalSingle("report-json");
        CliIo.EnsurePathChainHasNoReparsePoint(inputPath, "--input");
        if (!string.IsNullOrWhiteSpace(reportPath)) {
            CliIo.EnsurePathChainHasNoReparsePoint(
                reportPath,
                "--report-json"
            );
            CliIo.EnsurePathIsOutsideRepository(
                inputPath,
                reportPath,
                "--report-json"
            );
        }

        SJO.SessionJournalOfflineValidationReport report =
            await SJO.SessionJournalOfflineValidator.ValidateAsync(
                inputPath,
                branchName,
                CancellationToken.None
            ).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(reportPath)) {
            CliIo.WriteJsonAtomically(reportPath, report);
        }

        PrintValidation(report);
        if (!string.IsNullOrWhiteSpace(reportPath)) {
            Console.WriteLine($"report: {Path.GetFullPath(reportPath)}");
        }
        return 0;
    }

    private static void PrintValidation(
        SJO.SessionJournalOfflineValidationReport report
    ) {
        Console.WriteLine($"head: {report.Head ?? "(none)"}");
        Console.WriteLine($"events: {report.EventCount}");
        Console.WriteLine(
            $"logicalPayloadBytes: {report.LogicalPayloadBytes}"
        );
        Console.WriteLine($"phase: {report.ExecutionPhase}");
    }

    private static async Task<int> RunLlmSmokeAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) {
        string connectionsPath = options.Require("connections");
        string? requestedConnectionId = options.Get("connection");
        string callLogDir =
            options.Get("call-log-dir") ?? DefaultLlmSmokeCallLogDir;
        string message = options.Get("message")
            ?? "请用一句话回复：LLM smoke test ok。";

        CompletionConnectionsFileConfig connections =
            CompletionConnectionConfigLoader.LoadFile(connectionsPath);
        using var registry = new CompletionConnectionRegistry(
            connections,
            completionClientFactory
        );
        ValidateRequestedConnection(registry, requestedConnectionId);

        CompletionConnectionConfig connection =
            registry.Resolve(requestedConnectionId);
        ICompletionClient client = registry.GetClient(connection.Id);
        var loggingClient = new LoggingCompletionClient(
            client,
            connection,
            callLogDir,
            new CompletionCallLogContext(Command: "llm-smoke")
        );
        var request = new CompletionRequest(
            ModelId: connection.ModelId,
            SystemPrompt:
                "You are a concise smoke-test assistant. Reply briefly.",
            Context: [new ObservationMessage(message)],
            Tools: []
        );

        CompletionResult result =
            await loggingClient.StreamCompletionAsync(
                request,
                observer: null,
                CancellationToken.None
            ).ConfigureAwait(false);
        Console.WriteLine($"connection: {connection.Id}");
        Console.WriteLine(
            $"provider: {loggingClient.Name}/{loggingClient.ApiSpecId}"
        );
        Console.WriteLine($"callLogDir: {Path.GetFullPath(callLogDir)}");
        Console.WriteLine("response:");
        Console.WriteLine(result.Message.GetFlattenedText());
        if (result.Errors is { Count: > 0 }) {
            Console.WriteLine("errors:");
            foreach (string error in result.Errors) {
                Console.WriteLine($"- {error}");
            }
        }
        return 0;
    }

    private static async Task<int> RunMemoryMaintainerAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) {
        options.EnsureOnly(
            "input",
            "branch",
            "epoch",
            "profile",
            "output",
            "connections",
            "connection",
            "call-log-dir",
            "system-prompt",
            "prompt",
            "candidate-id",
            "attempt-id"
        );
        string inputPath = options.RequireSingle("input");
        string branchName = options.RequireSingle("branch");
        string epochId = options.RequireSingle("epoch");
        string profileName = options.RequireSingle("profile");
        string outputPath = options.RequireSingle("output");
        string connectionsPath = options.RequireSingle("connections");
        string? requestedConnectionId =
            options.GetOptionalSingle("connection");
        string callLogDir =
            options.GetOptionalSingle("call-log-dir")
            ?? DefaultMaintainerCallLogDir;
        string attemptId =
            options.GetOptionalSingle("attempt-id") ?? "attempt-1";
        string? systemPromptPath =
            options.GetOptionalSingle("system-prompt");
        string? userPromptPath =
            options.GetOptionalSingle("prompt");

        CliIo.ValidateReadOnlyWritablePaths(
            [
                (inputPath, "--input"),
                (connectionsPath, "--connections"),
                .. systemPromptPath is null
                    ? []
                    : new[] {
                        (systemPromptPath, "--system-prompt")
                    },
                .. userPromptPath is null
                    ? []
                    : new[] {
                        (userPromptPath, "--prompt")
                    }
            ],
            [
                (outputPath, "--output"),
                (callLogDir, "--call-log-dir")
            ]
        );
        CliIo.EnsurePathIsOutsideRepository(
            inputPath,
            outputPath,
            "--output"
        );
        CliIo.EnsurePathIsOutsideRepository(
            inputPath,
            callLogDir,
            "--call-log-dir"
        );
        CliIo.EnsurePathsDoNotNest(
            outputPath,
            callLogDir,
            "--output and --call-log-dir must be disjoint paths."
        );
        string fullOutputPath = Path.GetFullPath(outputPath);
        if (Directory.Exists(fullOutputPath)) {
            throw new ArgumentException(
                "--output must be a file path, not an existing directory."
            );
        }
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(inputPath);
        using var engine =
            SJ.SessionJournalEngine.Open(inputPath, branchName);
        DerivedMemoryBranchScope branchScope =
            repository.Bind(engine);

        string? systemPromptOverride =
            ReadPromptOrNull(systemPromptPath);
        string? userPromptOverride =
            ReadPromptOrNull(userPromptPath);

        CompletionConnectionsFileConfig connections =
            CompletionConnectionConfigLoader.LoadFile(connectionsPath);
        using var registry = new CompletionConnectionRegistry(
            connections,
            completionClientFactory
        );
        ValidateRequestedConnection(registry, requestedConnectionId);

        CompletionConnectionConfig connection =
            registry.Resolve(requestedConnectionId);
        ICompletionClient client = registry.GetClient(connection.Id);
        RecapMaintainerProfileDescriptor profile =
            RecapMaintainerProfileCatalog.Resolve(profileName)
                .WithPromptOverrides(
                    systemPromptOverride,
                    userPromptOverride
                );
        string candidateId =
            options.GetOptionalSingle("candidate-id")
            ?? $"prompt-{profile.PromptFingerprint[7..23]}";

        Directory.CreateDirectory(
            Path.GetDirectoryName(fullOutputPath) ?? "."
        );
        Directory.CreateDirectory(callLogDir);

        var loggingClient = new LoggingCompletionClient(
            client,
            connection,
            callLogDir,
            new CompletionCallLogContext(
                Command: "run-memory-maintainer",
                MaintainerId: profile.RewriteProfile.Id,
                TargetCarrier:
                    SJ.ContextHeaderCarrierTokens.ToStorageToken(
                        profile.RewriteProfile.Target.Carrier
                    ),
                TargetBlockId: profile.RewriteProfile.Target.BlockKey
            )
        );
        SJ.IRecapBlockMaintainer maintainer = profile.Create(
            loggingClient,
            connection.ModelId
        );
        var runner = new DerivedMemoryMaintainerRunner(repository);
        DerivedMemoryMaintainerRunResult result = await runner.RunAsync(
                engine,
                new DerivedMemoryMaintainerRunRequest(
                    epochId,
                    profile.RoleId,
                    profile.RewriteProfile.Id,
                    MemoryMaintainerProducerIdentity.Producer,
                    MemoryMaintainerProducerIdentity
                        .ComputeProducerFingerprint(
                            profile,
                            client,
                            connection
                        ),
                    profile.PromptFingerprint,
                    MemoryMaintainerProducerIdentity
                        .ComputeModelFingerprint(client, connection),
                    candidateId,
                    attemptId
                ),
                maintainer,
                () => loggingClient.WrittenCallLogPaths,
                CancellationToken.None
            )
            .ConfigureAwait(false);
        MemoryMaintainerRunRecord record =
            MemoryMaintainerRunRecord.FromResult(
                branchScope,
                profile,
                result,
                repository.Artifacts.ArtifactsDirectory
            );
        CliIo.WriteJsonAtomically(fullOutputPath, record);

        Console.WriteLine($"epoch: {record.EpochId}");
        Console.WriteLine($"artifact: {record.ArtifactId}");
        Console.WriteLine($"connection: {connection.Id}");
        Console.WriteLine($"profile: {profile.ProfileName}");
        Console.WriteLine($"output: {outputPath}");
        Console.WriteLine(
            $"callLogDir: {Path.GetFullPath(callLogDir)}"
        );
        return 0;
    }

    private static async Task<int> RunDerivedMemoryOrchestrationAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) {
        options.EnsureOnly(
            "input",
            "branch",
            "epoch",
            "role",
            "policy-id",
            "policy-fingerprint",
            "output",
            "connections",
            "connection",
            "call-log-dir",
            "candidate-prefix",
            "attempt-id"
        );
        string inputPath = options.RequireSingle("input");
        string branchName = options.RequireSingle("branch");
        string epochId = options.RequireSingle("epoch");
        string outputPath = options.RequireSingle("output");
        string candidatePrefix =
            options.GetOptionalSingle("candidate-prefix")
            ?? "daily";
        string attemptId =
            options.GetOptionalSingle("attempt-id") ?? "attempt-1";
        RoleSpec[] roleSpecs = [
            .. options.RequireRepeated("role")
                .Select(ParseRoleSpec)
        ];
        if (roleSpecs.Length == 0) {
            throw new ArgumentException(
                "At least one --role is required."
            );
        }
        bool needsProducer = roleSpecs.Any(spec => string.Equals(
            spec.ExecutionMode,
            DerivedMemoryRoleExecutionModes.Produce,
            StringComparison.Ordinal
        ));
        string? connectionsPath =
            options.GetOptionalSingle("connections");
        string? requestedConnectionId =
            options.GetOptionalSingle("connection");
        string? callLogDir = options.GetOptionalSingle("call-log-dir");
        if (needsProducer && connectionsPath is null) {
            throw new ArgumentException(
                "--connections is required when any role uses produce."
            );
        }
        if (needsProducer) {
            callLogDir ??= DefaultMaintainerCallLogDir;
        }

        CliIo.ValidateReadOnlyWritablePaths(
            connectionsPath is null
                ? [(inputPath, "--input")]
                : [
                    (inputPath, "--input"),
                    (connectionsPath, "--connections")
                ],
            callLogDir is null
                ? [(outputPath, "--output")]
                : [
                    (outputPath, "--output"),
                    (callLogDir, "--call-log-dir")
                ]
        );
        CliIo.EnsurePathIsOutsideRepository(
            inputPath,
            outputPath,
            "--output"
        );
        if (callLogDir is not null) {
            CliIo.EnsurePathIsOutsideRepository(
                inputPath,
                callLogDir,
                "--call-log-dir"
            );
            CliIo.EnsurePathsDoNotNest(
                outputPath,
                callLogDir,
                "--output and --call-log-dir must be disjoint paths."
            );
        }
        string fullOutputPath = Path.GetFullPath(outputPath);
        if (Directory.Exists(fullOutputPath)) {
            throw new ArgumentException(
                "--output must be a file path, not an existing directory."
            );
        }
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(inputPath);
        using var engine =
            SJ.SessionJournalEngine.Open(inputPath, branchName);
        DerivedMemoryBranchScope branchScope =
            repository.Bind(engine);

        RecapMaintainerProfileDescriptor[] profiles = [
            .. roleSpecs.Select(spec =>
                RecapMaintainerProfileCatalog.Resolve(spec.ProfileName))
        ];
        DerivedArtifactEpochPlan epoch =
            await repository.EpochPlanner.TryReadEpochAsync(epochId)
                .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Derived artifact epoch '{epochId}' does not exist."
            );
        var policy = new DerivedArtifactSetPolicy(
            options.RequireSingle("policy-id"),
            options.RequireSingle("policy-fingerprint"),
            epoch.CoherenceGroup,
            roleSpecs.Zip(
                profiles,
                static (spec, profile) =>
                    new DerivedArtifactSetRoleRequirement(
                        profile.RoleId,
                        profile.RewriteProfile.Target,
                        spec.Required
                    )
            ).ToArray()
        );
        const string preflightFingerprint =
            "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        DerivedMemoryOrchestrationStore
            .ValidateProvisioningStructure(
                policy,
                roleSpecs.Zip(
                    profiles,
                    (spec, profile) =>
                        new DerivedMemoryRoleProvisioning(
                            profile.RoleId,
                            profile.RewriteProfile.Id,
                            profile.RewriteProfile.Target,
                            spec.Required,
                            "preflight",
                            preflightFingerprint,
                            preflightFingerprint,
                            preflightFingerprint,
                            spec.ExecutionMode,
                            $"{candidatePrefix}-{profile.RoleId}",
                            attemptId,
                            spec.SelectedArtifactId
                        )
                ).ToArray()
            );

        Directory.CreateDirectory(
            Path.GetDirectoryName(fullOutputPath) ?? "."
        );
        if (callLogDir is not null) {
            Directory.CreateDirectory(callLogDir);
        }
        using CompletionConnectionRegistry? registry = needsProducer
            ? new CompletionConnectionRegistry(
                CompletionConnectionConfigLoader.LoadFile(
                    connectionsPath!
                ),
                completionClientFactory
            )
            : null;
        if (registry is not null) {
            ValidateRequestedConnection(
                registry,
                requestedConnectionId
            );
        }
        CompletionConnectionConfig? connection =
            registry?.Resolve(requestedConnectionId);
        ICompletionClient? client = connection is null
            ? null
            : registry!.GetClient(connection.Id);
        var executions = new List<DerivedMemoryRoleExecution>(
            profiles.Length
        );
        for (int index = 0; index < profiles.Length; index++) {
            RoleSpec spec = roleSpecs[index];
            RecapMaintainerProfileDescriptor profile = profiles[index];
            bool produce = string.Equals(
                spec.ExecutionMode,
                DerivedMemoryRoleExecutionModes.Produce,
                StringComparison.Ordinal
            );
            DerivedMemoryArtifact? selectedArtifact =
                spec.SelectedArtifactId is null
                    ? null
                    : await repository.Artifacts
                        .TryReadArtifactAsync(spec.SelectedArtifactId)
                        .ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        $"Selected artifact '{spec.SelectedArtifactId}' does not exist."
                    );
            if (selectedArtifact is not null
                && (!string.Equals(
                        selectedArtifact.RoleId,
                        profile.RoleId,
                        StringComparison.Ordinal
                    )
                    || !string.Equals(
                        selectedArtifact.ProfileId,
                        profile.RewriteProfile.Id,
                        StringComparison.Ordinal
                    )
                    || selectedArtifact.Target
                        != profile.RewriteProfile.Target)) {
                throw new InvalidDataException(
                    $"Selected artifact '{selectedArtifact.ArtifactId}' does not match profile '{profile.ProfileName}'."
                );
            }
            LoggingCompletionClient? loggingClient = produce
                ? new LoggingCompletionClient(
                    client!,
                    connection!,
                    callLogDir!,
                    new CompletionCallLogContext(
                        Command:
                            "run-derived-memory-orchestration",
                        MaintainerId: profile.RewriteProfile.Id,
                        TargetCarrier:
                            SJ.ContextHeaderCarrierTokens
                                .ToStorageToken(
                                    profile.RewriteProfile.Target.Carrier
                                ),
                        TargetBlockId:
                            profile.RewriteProfile.Target.BlockKey
                    )
                )
                : null;
            string producer = selectedArtifact?.Producer
                ?? (produce
                    ? MemoryMaintainerProducerIdentity.Producer
                    : MemoryMaintainerProducerIdentity.IdentityProducer);
            string producerFingerprint =
                selectedArtifact?.ProducerFingerprint
                ?? (produce
                    ? MemoryMaintainerProducerIdentity
                        .ComputeProducerFingerprint(
                            profile,
                            client!,
                            connection!
                        )
                    : MemoryMaintainerProducerIdentity
                        .ComputeIdentityProducerFingerprint(profile));
            var provisioning = new DerivedMemoryRoleProvisioning(
                profile.RoleId,
                profile.RewriteProfile.Id,
                profile.RewriteProfile.Target,
                spec.Required,
                producer,
                producerFingerprint,
                selectedArtifact?.PromptFingerprint
                    ?? profile.PromptFingerprint,
                selectedArtifact?.ModelFingerprint
                    ?? (produce
                        ? MemoryMaintainerProducerIdentity
                            .ComputeModelFingerprint(
                                client!,
                                connection!
                            )
                        : MemoryMaintainerProducerIdentity
                            .ComputeIdentityModelFingerprint()),
                spec.ExecutionMode,
                selectedArtifact?.CandidateId
                    ?? $"{candidatePrefix}-{profile.RoleId}",
                selectedArtifact?.AttemptId ?? attemptId,
                spec.SelectedArtifactId
            );
            executions.Add(new DerivedMemoryRoleExecution(
                provisioning,
                produce
                    ? profile.Create(
                        loggingClient!,
                        connection!.ModelId
                    )
                    : null,
                loggingClient is null
                    ? null
                    : () => loggingClient.WrittenCallLogPaths
            ));
        }

        DerivedMemoryOrchestrationResult result =
            await new DerivedMemoryOrchestrator(repository).RunAsync(
                    engine,
                    new DerivedMemoryOrchestrationRequest(
                        epochId,
                        policy,
                        executions.AsReadOnly()
                    ),
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        var record = new DerivedMemoryOrchestrationRunRecord(
            "atelia.session-journal.derived-memory-orchestration-run.v2",
            branchScope.BranchName,
            branchScope.BranchRefId.ToHexString(),
            result.Status.ToString(),
            result.Transaction.TransactionId,
            result.Transaction.JobFingerprint,
            result.Transaction.EpochId,
            result.PublishedSet?.SetId,
            result.Settlements,
            result.Failures
        );
        CliIo.WriteJsonAtomically(fullOutputPath, record);
        Console.WriteLine($"transaction: {record.TransactionId}");
        Console.WriteLine($"status: {record.Status}");
        Console.WriteLine(
            $"set: {record.PublishedSetId ?? "<none>"}"
        );
        Console.WriteLine($"output: {fullOutputPath}");
        return result.Status == DerivedMemoryOrchestrationStatus.Published
            ? 0
            : 2;
    }

    private static async Task<int> RunOnlineTurnAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) {
        options.EnsureOnly(
            "input",
            "branch",
            "message",
            "role",
            "policy-id",
            "policy-fingerprint",
            "connections",
            "connection",
            "call-log-dir",
            "output",
            "maximum-canonical-request-bytes",
            "coherence-group",
            "uncertain-recovery"
        );
        string inputPath = options.RequireSingle("input");
        string branchName = options.RequireSingle("branch");
        string message = options.RequireSingle("message");
        string connectionsPath =
            options.RequireSingle("connections");
        string outputPath = options.RequireSingle("output");
        string callLogDir =
            options.GetOptionalSingle("call-log-dir")
            ?? DefaultMaintainerCallLogDir;
        string? requestedConnectionId =
            options.GetOptionalSingle("connection");
        RoleSpec[] roleSpecs = [
            .. options.RequireRepeated("role")
                .Select(ParseRoleSpec)
        ];
        if (roleSpecs.Length == 0
            || roleSpecs.Any(static role =>
                !string.Equals(
                    role.ExecutionMode,
                    DerivedMemoryRoleExecutionModes.Produce,
                    StringComparison.Ordinal
                ))) {
            throw new ArgumentException(
                "run-online-turn requires at least one --role and currently accepts produce roles only."
            );
        }
        CliIo.ValidateReadOnlyWritablePaths(
            [
                (inputPath, "--input"),
                (connectionsPath, "--connections")
            ],
            [
                (outputPath, "--output"),
                (callLogDir, "--call-log-dir")
            ]
        );
        CliIo.EnsurePathIsOutsideRepository(
            inputPath,
            outputPath,
            "--output"
        );
        CliIo.EnsurePathIsOutsideRepository(
            inputPath,
            callLogDir,
            "--call-log-dir"
        );
        CliIo.EnsurePathsDoNotNest(
            outputPath,
            callLogDir,
            "--output and --call-log-dir must be disjoint paths."
        );
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(inputPath);
        using var engine =
            SJ.SessionJournalEngine.Open(inputPath, branchName);
        DerivedMemoryBranchScope branchScope =
            repository.Bind(engine);

        RecapMaintainerProfileDescriptor[] profiles = [
            .. roleSpecs.Select(spec =>
                RecapMaintainerProfileCatalog.Resolve(
                    spec.ProfileName
                ))
        ];
        DerivedArtifactPlannerKey key = new(
            branchScope.BranchRefId,
            options.GetOptionalSingle("coherence-group")
                ?? "memory-pack"
        );
        DerivedArtifactPlannerConfig config =
            await repository.EpochPlanner
                .TryReadCurrentConfigAsync(
                    branchScope,
                    key.CoherenceGroup
                )
                .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"No current planner config exists for '{key.BranchRefId}/{key.CoherenceGroup}'."
            );
        var policy = new DerivedArtifactSetPolicy(
            options.RequireSingle("policy-id"),
            options.RequireSingle("policy-fingerprint"),
            config.CoherenceGroup,
            roleSpecs.Zip(
                profiles,
                static (spec, profile) =>
                    new DerivedArtifactSetRoleRequirement(
                        profile.RoleId,
                        profile.RewriteProfile.Target,
                        spec.Required
                    )
            ).ToArray()
        );
        const string preflightFingerprint =
            "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        DerivedMemoryOrchestrationStore
            .ValidateProvisioningStructure(
                policy,
                roleSpecs.Zip(
                    profiles,
                    static (spec, profile) =>
                        new DerivedMemoryRoleProvisioning(
                            profile.RoleId,
                            profile.RewriteProfile.Id,
                            profile.RewriteProfile.Target,
                            spec.Required,
                            "preflight",
                            preflightFingerprint,
                            preflightFingerprint,
                            preflightFingerprint,
                            spec.ExecutionMode,
                            $"online-{profile.RoleId}",
                            "online-attempt-1"
                        )
                ).ToArray()
            );

        Directory.CreateDirectory(
            Path.GetDirectoryName(
                Path.GetFullPath(outputPath)
            ) ?? "."
        );
        Directory.CreateDirectory(callLogDir);
        using var registry =
            new CompletionConnectionRegistry(
                CompletionConnectionConfigLoader.LoadFile(
                    connectionsPath
                ),
                completionClientFactory
            );
        ValidateRequestedConnection(
            registry,
            requestedConnectionId
        );
        CompletionConnectionConfig connection =
            registry.Resolve(requestedConnectionId);
        ICompletionClient client =
            registry.GetClient(connection.Id);
        var executions =
            new List<DerivedMemoryRoleExecution>(
                profiles.Length
            );
        for (int index = 0; index < profiles.Length; index++) {
            RecapMaintainerProfileDescriptor profile =
                profiles[index];
            RoleSpec spec = roleSpecs[index];
            var maintainerClient =
                new LoggingCompletionClient(
                    client,
                    connection,
                    callLogDir,
                    new CompletionCallLogContext(
                        Command: "run-online-turn/maintenance",
                        MaintainerId:
                            profile.RewriteProfile.Id,
                        TargetCarrier:
                            SJ.ContextHeaderCarrierTokens
                                .ToStorageToken(
                                    profile.RewriteProfile
                                        .Target.Carrier
                                ),
                        TargetBlockId:
                            profile.RewriteProfile
                                .Target.BlockKey
                    )
                );
            executions.Add(
                new DerivedMemoryRoleExecution(
                    new DerivedMemoryRoleProvisioning(
                        profile.RoleId,
                        profile.RewriteProfile.Id,
                        profile.RewriteProfile.Target,
                        spec.Required,
                        MemoryMaintainerProducerIdentity
                            .Producer,
                        MemoryMaintainerProducerIdentity
                            .ComputeProducerFingerprint(
                                profile,
                                client,
                                connection
                            ),
                        profile.PromptFingerprint,
                        MemoryMaintainerProducerIdentity
                            .ComputeModelFingerprint(
                                client,
                                connection
                            ),
                        DerivedMemoryRoleExecutionModes
                            .Produce,
                        $"online-{profile.RoleId}",
                        "online-attempt-1"
                    ),
                    profile.Create(
                        maintainerClient,
                        connection.ModelId
                    ),
                    () => maintainerClient
                        .WrittenCallLogPaths
                )
            );
        }
        var coordinator =
            new DerivedMemoryOnlineLifecycleCoordinator(
                repository,
                policy,
                branchScope,
                executions.AsReadOnly()
            );
        var agentClient = new LoggingCompletionClient(
            client,
            connection,
            callLogDir,
            new CompletionCallLogContext(
                Command: "run-online-turn/agent"
            )
        );
        long? maximumCanonicalRequestBytes = ParsePositiveLong(
            options.GetOptionalSingle(
                "maximum-canonical-request-bytes"
            ),
            "--maximum-canonical-request-bytes"
        );
        SJ.SessionUncertainCompletionRecoveryPolicy recoveryPolicy =
            ParseUncertainCompletionRecoveryPolicy(
                options.GetOptionalSingle("uncertain-recovery")
            );
        var runtime = new SJ.SessionRuntime(
            agentClient,
            CompletionTarget: CompletionTargetIdentityFactory.Create(
                connection,
                client
            ),
            MaxTokens: connection.MaxTokens,
            UncertainCompletionRecoveryPolicy:
            recoveryPolicy,
            ContextCandidateSource: coordinator,
            MaximumCanonicalRequestBytes:
            maximumCanonicalRequestBytes,
            ContextLifecycle: coordinator
        );
        engine.UseRuntime(runtime);
        SJ.SessionExecutionBoundaryInspection initialBoundary =
            engine.InspectExecutionBoundary();
        ActionMessage resultMessage;
        CompletionDescriptor resultInvocation;
        IReadOnlyList<string>? resultErrors;
        if (initialBoundary.Phase is
            SJ.SessionExecutionPhase.Idle
            or SJ.SessionExecutionPhase.TurnFailed) {
            SJ.TurnResult result = await engine.SendAsync(
                    message,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            resultMessage = result.Message;
            resultInvocation = result.Invocation;
            resultErrors = result.Errors;
        }
        else {
            SJ.ResumeOutcome resumed = await engine.ResumeAsync(
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            if (!resumed.Advanced
                || resumed.Message is null
                || resumed.Invocation is null) {
                throw new InvalidOperationException(
                    $"run-online-turn could not advance restart phase '{initialBoundary.Phase}'."
                );
            }
            resultMessage = resumed.Message;
            resultInvocation = resumed.Invocation;
            resultErrors = resumed.Errors;
        }
        SJ.SessionExecutionBoundaryInspection boundary =
            engine.InspectExecutionBoundary();
        var record = new OnlineTurnRunRecord(
            "atelia.session-journal.online-turn-run.v2",
            branchScope.BranchName,
            branchScope.BranchRefId.ToHexString(),
            boundary.Head is { } head
                ? SJ.EventAddressTextCodec
                    .Format(head)
                : null,
            boundary.Phase.ToString(),
            resultInvocation.ProviderId,
            resultInvocation.ApiSpecId,
            resultInvocation.Model,
            Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256
                    .HashData(
                        Encoding.UTF8.GetBytes(
                            resultMessage
                                .GetFlattenedText()
                        )
                    )
            ),
            resultErrors?.Count ?? 0
        );
        CliIo.WriteJsonAtomically(outputPath, record);
        Console.WriteLine($"head: {record.Head}");
        Console.WriteLine($"phase: {record.Phase}");
        Console.WriteLine($"output: {Path.GetFullPath(outputPath)}");
        return 0;
    }

    private static SJ.SessionUncertainCompletionRecoveryPolicy
        ParseUncertainCompletionRecoveryPolicy(string? value) {
        value ??= "refuse";
        return value switch {
            "refuse" =>
                SJ.SessionUncertainCompletionRecoveryPolicy.Refuse,
            "restart-new-attempt" =>
                SJ.SessionUncertainCompletionRecoveryPolicy
                    .RestartWithNewAttempt,
            _ => throw new ArgumentException(
                "--uncertain-recovery must be refuse or restart-new-attempt."
            )
        };
    }

    private static long? ParsePositiveLong(
        string? value,
        string option
    ) {
        if (value is null) {
            return null;
        }
        if (!long.TryParse(value, out long parsed)
            || parsed <= 0) {
            throw new ArgumentException(
                $"{option} must be a positive Int64."
            );
        }
        return parsed;
    }

    private static RoleSpec ParseRoleSpec(string value) {
        string[] parts = value.Split(':', 4);
        if (parts.Length is < 3 or > 4) {
            throw new ArgumentException(
                "--role must be required|optional:<profile>:produce|identity|select-existing[:artifact-id]."
            );
        }
        bool required = parts[0] switch {
            "required" => true,
            "optional" => false,
            _ => throw new ArgumentException(
                "--role requirement must be 'required' or 'optional'."
            )
        };
        string mode = parts[2];
        if (!DerivedMemoryRoleExecutionModes.IsDefined(mode)) {
            throw new ArgumentException(
                $"Unsupported --role execution mode '{mode}'."
            );
        }
        bool select = string.Equals(
            mode,
            DerivedMemoryRoleExecutionModes.SelectExisting,
            StringComparison.Ordinal
        );
        if (select != (parts.Length == 4)) {
            throw new ArgumentException(
                "select-existing requires one exact artifact id; other modes forbid it."
            );
        }
        return new RoleSpec(
            required,
            parts[1],
            mode,
            parts.Length == 4 ? parts[3] : null
        );
    }

    private static void ValidateRequestedConnection(
        CompletionConnectionRegistry registry,
        string? requestedConnectionId
    ) {
        if (!string.IsNullOrWhiteSpace(requestedConnectionId)
            && !registry.TryGet(requestedConnectionId, out _)) {
            throw new ArgumentException(
                $"Unknown completion connection "
                + $"'{requestedConnectionId}'."
            );
        }
    }

    private static string? ReadPromptOrNull(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? null
            : File.ReadAllText(path, Encoding.UTF8);

    private sealed record RoleSpec(
        bool Required,
        string ProfileName,
        string ExecutionMode,
        string? SelectedArtifactId
    );

    private static int Fail(string message) {
        Console.Error.WriteLine($"error: {message}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp() {
        Console.WriteLine("SessionJournal.Cli");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine(
            "  recap create --input <repo-dir> --branch <name> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  recap inspect --input <repo-dir> --branch <name> "
            + "--anchor <event-address> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  recap run --input <repo-dir> --branch <name> "
            + "--connections <path> [--connection <id>] "
            + "[--call-log-dir <dir>] "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  recap resume --input <repo-dir> --branch <name> "
            + "--anchor <event-address> --connections <path> "
            + "[--connection <id>] [--call-log-dir <dir>] "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  recap restore --input <repo-dir> --branch <name> "
            + "--anchor <event-address> "
            + "--expected-raw-head <event-address> "
            + "--connections <path> [--connection <id>] "
            + "[--call-log-dir <dir>] "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  recap abandon-building --input <repo-dir> "
            + "--branch <name> --anchor <event-address> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  recap reset --input <repo-dir> --branch <name> "
            + "--confirm-ref <exact-ref-id> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  import-legacy-json --input <json> --output <repo-dir> "
            + "[--force] [--report-md <path>]"
        );
        Console.WriteLine(
            "  validate --input <repo-dir> [--branch <name>] "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  llm-smoke --connections <path> [--connection <id>] "
            + "[--call-log-dir <dir>] [--message <text>]"
        );
        Console.WriteLine(
            "  run-memory-maintainer --input <repo-dir> --branch <name> "
            + "--epoch <epoch-id> "
            + "--profile <"
            + "autobiographical-rewrite"
            + "|world-understanding-rewrite> "
            + "--output <json> --connections <path> "
            + "[--connection <id>] [--call-log-dir <dir>] "
            + "[--candidate-id <token>] [--attempt-id <token>] "
            + "[--system-prompt <path>] [--prompt <path>]"
        );
        Console.WriteLine(
            "  run-derived-memory-orchestration --input <repo-dir> "
            + "--branch <name> --epoch <epoch-id> "
            + "--role <required|optional:profile:produce|identity"
            + "|select-existing[:artifact-id]> "
            + "--policy-id <token> --policy-fingerprint <token> "
            + "--output <json> "
            + "[--connections <path> --connection <id> "
            + "--call-log-dir <dir>] "
            + "[--candidate-prefix <token>] [--attempt-id <token>]"
        );
        Console.WriteLine(
            "  run-online-turn --input <repo-dir> --branch <name> "
            + "--message <text> "
            + "--role <required|optional:profile:produce> "
            + "--policy-id <token> --policy-fingerprint <token> "
            + "--connections <path> [--connection <id>] "
            + "--output <json> [--call-log-dir <dir>] "
            + "[--maximum-canonical-request-bytes <n>] "
            + "[--coherence-group <token>] "
            + "[--uncertain-recovery refuse|restart-new-attempt]"
        );
        Console.WriteLine(
            "  publish-derived-artifact-set --input <repo-dir> "
            + "--branch <name> --transaction <dmt-id> "
            + "--member <role=artifact-id> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  list-derived-artifact-sets --input <repo-dir> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  validate-derived-memory --input <repo-dir> "
            + "[--branch <name>] "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  rebuild-derived-artifact-set-latest --input <repo-dir> "
            + "--branch <name> --coherence-group <token> "
            + "--policy-id <token> --policy-fingerprint <token> "
            + "--required-role <role=carrier/block> "
            + "[--optional-role <role=carrier/block>] "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  configure-derived-artifact-planner --input <repo-dir> "
            + "--branch <name> --coherence-group <token> "
            + "--topology-version <token> "
            + "--minimum-recent-tokens <n> --epoch-trigger-tokens <n> "
            + "--scheduling-headroom-tokens <n> --hard-limit-tokens <n> "
            + "--expected-current <none|config-id> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  plan-derived-artifact-epoch --input <repo-dir> "
            + "--branch <name> --coherence-group <token> "
            + "--expected-previous <none|epoch-id> "
            + "--input-set <none|set-id> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  list-derived-artifact-epochs --input <repo-dir> "
            + "[--report-json <path-outside-repo>]"
        );
    }
}
