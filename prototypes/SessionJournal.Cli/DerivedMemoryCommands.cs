using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedMemory;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class DerivedMemoryCommands {
    private const string OperationSchema =
        "atelia.session-journal.cli.derived-artifact-set-operation.v1";
    private const string InventorySchema =
        "atelia.session-journal.cli.derived-artifact-set-inventory.v1";
    private const string ValidationSchema =
        "atelia.session-journal.cli.derived-memory-validation.v2";
    private const string PlannerConfigOperationSchema =
        "atelia.session-journal.cli.derived-artifact-planner-config-operation.v1";
    private const string EpochOperationSchema =
        "atelia.session-journal.cli.derived-artifact-epoch-operation.v1";
    private const string EpochInventorySchema =
        "atelia.session-journal.cli.derived-artifact-epoch-inventory.v1";

    public static async Task<int> ConfigurePlannerAsync(
        CliOptions options
    ) {
        options.EnsureOnly(
            "input",
            "lineage",
            "coherence-group",
            "topology-version",
            "minimum-recent-tokens",
            "epoch-trigger-tokens",
            "scheduling-headroom-tokens",
            "hard-limit-tokens",
            "expected-current",
            "report-json"
        );
        string inputPath = options.RequireSingle("input");
        string? reportPath = PreparePaths(
            inputPath,
            options.GetOptionalSingle("report-json")
        );
        string? expectedCurrent = ParseOptionalIdentity(
            options.RequireSingle("expected-current")
        );
        var definition = new DerivedArtifactPlannerConfigDefinition(
            options.RequireSingle("lineage"),
            options.RequireSingle("coherence-group"),
            options.RequireSingle("topology-version"),
            ParsePositiveLong(options, "minimum-recent-tokens"),
            ParsePositiveLong(options, "epoch-trigger-tokens"),
            ParseNonNegativeLong(
                options,
                "scheduling-headroom-tokens"
            ),
            ParsePositiveLong(options, "hard-limit-tokens")
        );
        DerivedArtifactPlannerConfig config =
            await DerivedMemoryRepository.Open(inputPath)
                .EpochPlanner.ConfigureAsync(
                    definition,
                    expectedCurrent
                )
                .ConfigureAwait(false);
        var report = new PlannerConfigOperationReport(
            PlannerConfigOperationSchema,
            ToSummary(config)
        );
        WriteOptionalReport(reportPath, report);
        Console.WriteLine($"configId: {config.ConfigId}");
        Console.WriteLine(
            $"previousConfigId: {config.PreviousConfigId ?? "none"}"
        );
        Console.WriteLine(
            $"key: {config.LineageKey}|{config.CoherenceGroup}"
        );
        PrintReportPath(reportPath);
        return 0;
    }

    public static async Task<int> PlanEpochAsync(CliOptions options) {
        options.EnsureOnly(
            "input",
            "lineage",
            "coherence-group",
            "expected-previous",
            "input-set",
            "report-json"
        );
        string inputPath = options.RequireSingle("input");
        string? reportPath = PreparePaths(
            inputPath,
            options.GetOptionalSingle("report-json")
        );
        string? expectedPrevious = ParseOptionalIdentity(
            options.RequireSingle("expected-previous")
        );
        string? inputSet = ParseOptionalIdentity(
            options.RequireSingle("input-set")
        );
        if ((expectedPrevious is null) != (inputSet is null)) {
            throw new ArgumentException(
                "Genesis requires --expected-previous none and --input-set none; non-genesis requires both exact ids."
            );
        }
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(inputPath);
        using SJ.SessionJournalEngine engine =
            SJ.SessionJournalEngine.Open(inputPath);
        DerivedArtifactEpochPlanningResult result =
            await repository.EpochPlanner.PlanAsync(
                    engine,
                    new DerivedArtifactEpochPlanningRequest(
                        options.RequireSingle("lineage"),
                        options.RequireSingle("coherence-group"),
                        expectedPrevious,
                        inputSet
                    )
                )
                .ConfigureAwait(false);
        var report = new EpochOperationReport(
            EpochOperationSchema,
            result.Status.ToString(),
            ToSummary(result.Config),
            result.Epoch is null ? null : ToSummary(result.Epoch),
            ToSummary(result.Diagnostics)
        );
        WriteOptionalReport(reportPath, report);
        Console.WriteLine($"status: {report.Status}");
        Console.WriteLine(
            $"epochId: {report.Epoch?.EpochId ?? "none"}"
        );
        Console.WriteLine(
            $"headers: {report.Diagnostics.HeaderVisits}"
        );
        Console.WriteLine(
            $"payloads: {report.Diagnostics.PayloadReads}"
        );
        Console.WriteLine(
            $"decodedBytes: {report.Diagnostics.DecodedPayloadBytes}"
        );
        Console.WriteLine(
            $"eligibleTokens: {report.Diagnostics.EligibleTokens}"
        );
        Console.WriteLine(
            $"retainedRecentTokens: {report.Diagnostics.RetainedRecentTokens}"
        );
        PrintReportPath(reportPath);
        return 0;
    }

    public static async Task<int> ListEpochsAsync(CliOptions options) {
        options.EnsureOnly("input", "report-json");
        string inputPath = options.RequireSingle("input");
        string? reportPath = PreparePaths(
            inputPath,
            options.GetOptionalSingle("report-json")
        );
        DerivedArtifactEpochInventory inventory =
            await DerivedMemoryRepository.Open(inputPath)
                .EpochPlanner.ReadInventoryAsync()
                .ConfigureAwait(false);
        var report = new EpochInventoryReport(
            EpochInventorySchema,
            [.. inventory.Configs.Select(ToSummary)],
            [
                .. inventory.CurrentConfigs.Select(
                    static pointer => new PlannerConfigPointerSummary(
                        pointer.LineageKey,
                        pointer.CoherenceGroup,
                        pointer.ConfigId
                    )
                )
            ],
            [.. inventory.Epochs.Select(ToSummary)],
            [
                .. inventory.LatestEpochs.Select(
                    static pointer => new EpochPointerSummary(
                        pointer.LineageKey,
                        pointer.CoherenceGroup,
                        pointer.EpochId
                    )
                )
            ]
        );
        WriteOptionalReport(reportPath, report);
        Console.WriteLine($"plannerConfigs: {report.Configs.Count}");
        Console.WriteLine(
            $"currentPlannerConfigs: {report.CurrentConfigs.Count}"
        );
        Console.WriteLine($"epochs: {report.Epochs.Count}");
        Console.WriteLine($"latestEpochs: {report.LatestEpochs.Count}");
        PrintReportPath(reportPath);
        return 0;
    }

    public static async Task<int> PublishAsync(CliOptions options) {
        options.EnsureOnly(
            "input",
            "lineage",
            "coherence-group",
            "policy-id",
            "policy-fingerprint",
            "required-role",
            "optional-role",
            "member",
            "expected-previous",
            "report-json"
        );
        string inputPath = options.RequireSingle("input");
        string lineageKey = options.RequireSingle("lineage");
        DerivedArtifactSetPolicy policy = ParsePolicy(options);
        IReadOnlyList<DerivedArtifactSetMemberSelection> members =
            ParseMembers(options.RequireRepeated("member"));
        string expectedPreviousText =
            options.RequireSingle("expected-previous");
        string? expectedPrevious = string.Equals(
            expectedPreviousText,
            "none",
            StringComparison.Ordinal
        )
            ? null
            : expectedPreviousText;
        string? reportPath = PreparePaths(
            inputPath,
            options.GetOptionalSingle("report-json")
        );

        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(inputPath);
        EventAddress? commonAnchor = null;
        foreach (DerivedArtifactSetMemberSelection member in members) {
            DerivedRecapArtifact artifact = await repository.Recaps
                .TryReadArtifactAsync(member.ArtifactId)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Exact derived artifact '{member.ArtifactId}' is missing or unusable."
                );
            commonAnchor ??= artifact.AnchorRawEvent;
            if (artifact.AnchorRawEvent != commonAnchor) {
                throw new InvalidDataException(
                    "ArtifactSet members do not share one exact common anchor."
                );
            }
        }
        if (commonAnchor is null) {
            throw new InvalidDataException(
                "ArtifactSet publication requires at least one member."
            );
        }

        SJ.SessionContextAnchorSetupReferences setups;
        using (SJ.SessionJournalEngine engine =
               SJ.SessionJournalEngine.Open(inputPath)) {
            setups = engine.ResolveContextAnchorSetupReferences(
                commonAnchor.Value
            );
        }
        DerivedArtifactSet set =
            await repository.ArtifactSets.PublishAsync(
                new DerivedArtifactSetPublicationRequest(
                    policy,
                    lineageKey,
                    setups,
                    members,
                    expectedPrevious
                )
            ).ConfigureAwait(false);
        var report = new DerivedArtifactSetOperationReport(
            OperationSchema,
            "publish",
            ToSummary(set)
        );
        WriteOptionalReport(reportPath, report);
        PrintOperation(report, reportPath);
        return 0;
    }

    public static async Task<int> ListAsync(CliOptions options) {
        options.EnsureOnly("input", "report-json");
        string inputPath = options.RequireSingle("input");
        string? reportPath = PreparePaths(
            inputPath,
            options.GetOptionalSingle("report-json")
        );
        DerivedArtifactSetInventory inventory =
            await DerivedMemoryRepository.Open(inputPath)
                .ArtifactSets.ReadInventoryAsync()
                .ConfigureAwait(false);
        var report = new DerivedArtifactSetInventoryReport(
            InventorySchema,
            [.. inventory.Sets.Select(ToSummary)],
            [
                .. inventory.LatestPointers.Select(
                    static pointer => new DerivedArtifactSetPointerReport(
                        pointer.LineageKey,
                        pointer.CoherenceGroup,
                        pointer.PolicyId,
                        pointer.PolicyFingerprint,
                        pointer.SetId
                    )
                )
            ]
        );
        WriteOptionalReport(reportPath, report);
        Console.WriteLine($"sets: {report.Sets.Count}");
        Console.WriteLine($"latestPointers: {report.LatestPointers.Count}");
        foreach (DerivedArtifactSetSummaryReport set in report.Sets) {
            Console.WriteLine(
                $"set: {set.SetId} key={FormatKey(set)} previous={set.PreviousSetId ?? "none"}"
            );
        }
        foreach (DerivedArtifactSetPointerReport pointer in
                 report.LatestPointers) {
            Console.WriteLine(
                $"pointer: {pointer.SetId} key={FormatKey(pointer)}"
            );
        }
        PrintReportPath(reportPath);
        return 0;
    }

    public static async Task<int> ValidateAsync(CliOptions options) {
        options.EnsureOnly("input", "report-json");
        string inputPath = options.RequireSingle("input");
        string? reportPath = PreparePaths(
            inputPath,
            options.GetOptionalSingle("report-json")
        );
        DerivedMemoryValidationReport validation =
            await DerivedMemoryRepository.Open(inputPath)
                .ValidateAsync()
                .ConfigureAwait(false);
        var report = new DerivedMemoryValidationCliReport(
            ValidationSchema,
            validation.ArtifactCount,
            validation.ArtifactSetCount,
            validation.LatestPointerCount,
            validation.ExactArtifactSetKeyCount,
            validation.PlannerConfigCount,
            validation.CurrentPlannerConfigCount,
            validation.ArtifactEpochCount,
            validation.LatestArtifactEpochCount
        );
        WriteOptionalReport(reportPath, report);
        Console.WriteLine($"artifacts: {report.ArtifactCount}");
        Console.WriteLine($"sets: {report.ArtifactSetCount}");
        Console.WriteLine($"latestPointers: {report.LatestPointerCount}");
        Console.WriteLine($"exactKeys: {report.ExactArtifactSetKeyCount}");
        Console.WriteLine(
            $"plannerConfigs: {report.PlannerConfigCount}"
        );
        Console.WriteLine(
            $"currentPlannerConfigs: {report.CurrentPlannerConfigCount}"
        );
        Console.WriteLine($"artifactEpochs: {report.ArtifactEpochCount}");
        Console.WriteLine(
            $"latestArtifactEpochs: {report.LatestArtifactEpochCount}"
        );
        PrintReportPath(reportPath);
        return 0;
    }

    public static async Task<int> RebuildLatestAsync(CliOptions options) {
        options.EnsureOnly(
            "input",
            "lineage",
            "coherence-group",
            "policy-id",
            "policy-fingerprint",
            "required-role",
            "optional-role",
            "report-json"
        );
        string inputPath = options.RequireSingle("input");
        string lineageKey = options.RequireSingle("lineage");
        DerivedArtifactSetPolicy policy = ParsePolicy(options);
        string? reportPath = PreparePaths(
            inputPath,
            options.GetOptionalSingle("report-json")
        );
        DerivedArtifactSet? set =
            await DerivedMemoryRepository.Open(inputPath)
                .ArtifactSets.RebuildLatestPointerAsync(
                    policy,
                    lineageKey
                )
                .ConfigureAwait(false);
        if (set is null) {
            throw new InvalidDataException(
                "No Derived ArtifactSet matches the exact lineage/policy key."
            );
        }
        var report = new DerivedArtifactSetOperationReport(
            OperationSchema,
            "rebuild-latest",
            ToSummary(set)
        );
        WriteOptionalReport(reportPath, report);
        PrintOperation(report, reportPath);
        return 0;
    }

    private static DerivedArtifactSetPolicy ParsePolicy(
        CliOptions options
    ) {
        var roles = new List<DerivedArtifactSetRoleRequirement>();
        roles.AddRange(
            options.RequireRepeated("required-role")
                .Select(value => ParseRole(value, required: true))
        );
        roles.AddRange(
            options.GetRepeated("optional-role")
                .Select(value => ParseRole(value, required: false))
        );
        return new DerivedArtifactSetPolicy(
            options.RequireSingle("policy-id"),
            options.RequireSingle("policy-fingerprint"),
            options.RequireSingle("coherence-group"),
            roles.AsReadOnly()
        );
    }

    private static DerivedArtifactSetRoleRequirement ParseRole(
        string value,
        bool required
    ) {
        (string roleId, string right) = SplitAssignment(
            value,
            required ? "--required-role" : "--optional-role"
        );
        int slash = right.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == right.Length - 1) {
            throw new ArgumentException(
                "Role target must use role=carrier/block-key syntax."
            );
        }
        string carrierText = right[..slash];
        string blockKey = right[(slash + 1)..];
        SJ.MemoryPackCarrier carrier = carrierText switch {
            "system" => SJ.MemoryPackCarrier.System,
            "observation" => SJ.MemoryPackCarrier.Observation,
            "action" => SJ.MemoryPackCarrier.Action,
            _ => throw new ArgumentException(
                $"Unknown MemoryPack carrier '{carrierText}'."
            )
        };
        return new DerivedArtifactSetRoleRequirement(
            roleId,
            new SJ.MemoryPackBlockPath(carrier, blockKey),
            required
        );
    }

    private static IReadOnlyList<DerivedArtifactSetMemberSelection>
        ParseMembers(
        IReadOnlyList<string> values
    ) => Array.AsReadOnly([
        .. values.Select(value => {
            (string roleId, string artifactId) = SplitAssignment(
                value,
                "--member"
            );
            return new DerivedArtifactSetMemberSelection(
                roleId,
                artifactId
            );
        })
    ]);

    private static (string Left, string Right) SplitAssignment(
        string value,
        string optionName
    ) {
        int equals = value.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0 || equals == value.Length - 1) {
            throw new ArgumentException(
                $"{optionName} requires left=right syntax."
            );
        }
        return (value[..equals], value[(equals + 1)..]);
    }

    private static string? PreparePaths(
        string inputPath,
        string? reportPath
    ) {
        Program.EnsurePathChainHasNoReparsePoint(inputPath, "--input");
        if (reportPath is not null) {
            Program.EnsurePathChainHasNoReparsePoint(
                reportPath,
                "--report-json"
            );
            Program.EnsurePathIsOutsideRepository(
                inputPath,
                reportPath,
                "--report-json"
            );
        }
        return reportPath;
    }

    private static string? ParseOptionalIdentity(string value) =>
        string.Equals(value, "none", StringComparison.Ordinal)
            ? null
            : value;

    private static long ParsePositiveLong(
        CliOptions options,
        string name
    ) {
        string value = options.RequireSingle(name);
        return long.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out long parsed
            )
            && parsed > 0
            ? parsed
            : throw new ArgumentException(
                $"--{name} must be a positive base-10 integer."
            );
    }

    private static long ParseNonNegativeLong(
        CliOptions options,
        string name
    ) {
        string value = options.RequireSingle(name);
        return long.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out long parsed
            )
            && parsed >= 0
            ? parsed
            : throw new ArgumentException(
                $"--{name} must be a non-negative base-10 integer."
            );
    }

    private static void WriteOptionalReport<T>(
        string? reportPath,
        T report
    ) {
        if (reportPath is not null) {
            Program.WriteJsonAtomically(reportPath, report);
        }
    }

    private static void PrintOperation(
        DerivedArtifactSetOperationReport report,
        string? reportPath
    ) {
        Console.WriteLine($"operation: {report.Operation}");
        Console.WriteLine($"setId: {report.Set.SetId}");
        Console.WriteLine(
            $"previousSetId: {report.Set.PreviousSetId ?? "none"}"
        );
        Console.WriteLine($"commonAnchor: {report.Set.CommonAnchor}");
        Console.WriteLine($"members: {report.Set.Members.Count}");
        PrintReportPath(reportPath);
    }

    private static void PrintReportPath(string? reportPath) {
        if (reportPath is not null) {
            Console.WriteLine($"report: {Path.GetFullPath(reportPath)}");
        }
    }

    private static DerivedArtifactSetSummaryReport ToSummary(
        DerivedArtifactSet set
    ) => new(
        set.SetId,
        set.LineageKey,
        set.CoherenceGroup,
        set.PolicyId,
        set.PolicyFingerprint,
        [
            .. set.RoleRequirements.Select(
                static role => new DerivedArtifactSetRoleReport(
                    role.RoleId,
                    CarrierToken(role.Target.Carrier),
                    role.Target.BlockKey,
                    role.Required
                )
            )
        ],
        set.PreviousSetId,
        EventAddressTextCodec.Format(set.CommonAnchor),
        new DerivedArtifactSetSetupReferencesReport(
            ToSetup(set.AnchorSetups.RuntimeConfig),
            ToSetup(set.AnchorSetups.SystemPrompt)
        ),
        [
            .. set.Members.Select(
                static member => new DerivedArtifactSetMemberReport(
                    member.RoleId,
                    member.ArtifactId,
                    member.ArtifactKind,
                    CarrierToken(member.Target.Carrier),
                    member.Target.BlockKey,
                    member.ContentCodecId,
                    member.ContentSha256,
                    EventAddressTextCodec.Format(member.SourceRawHead)
                )
            )
        ]
    );

    private static DerivedArtifactSetSetupReferenceReport ToSetup(
        SJ.SessionContextSetupReference reference
    ) => new(
        EventAddressTextCodec.Format(reference.Address),
        reference.BodySchemaVersion,
        reference.PayloadSha256
    );

    private static string CarrierToken(SJ.MemoryPackCarrier carrier) =>
        carrier switch {
            SJ.MemoryPackCarrier.System => "system",
            SJ.MemoryPackCarrier.Observation => "observation",
            SJ.MemoryPackCarrier.Action => "action",
            _ => throw new InvalidDataException(
                $"Unknown MemoryPack carrier '{carrier}'."
            )
        };

    private static string FormatKey(DerivedArtifactSetSummaryReport set) =>
        $"{set.LineageKey}|{set.CoherenceGroup}|{set.PolicyId}|{set.PolicyFingerprint}";

    private static string FormatKey(DerivedArtifactSetPointerReport pointer) =>
        $"{pointer.LineageKey}|{pointer.CoherenceGroup}|{pointer.PolicyId}|{pointer.PolicyFingerprint}";

    private static PlannerConfigSummary ToSummary(
        DerivedArtifactPlannerConfig config
    ) => new(
        config.ConfigId,
        config.LineageKey,
        config.CoherenceGroup,
        config.PreviousConfigId,
        config.TopologyVersion,
        config.MinimumRecentTokens,
        config.EpochTriggerTokens,
        config.SchedulingHeadroomTokens,
        config.HardLimitTokens,
        config.TokenEstimatorId,
        config.BoundaryPolicyId,
        config.HardLimitPolicyId,
        config.GenesisPolicyId
    );

    private static EpochSummary ToSummary(
        DerivedArtifactEpochPlan epoch
    ) => new(
        epoch.EpochId,
        epoch.LineageKey,
        epoch.CoherenceGroup,
        epoch.TopologyVersion,
        epoch.ConfigId,
        epoch.PreviousEpochId,
        epoch.InputSetId,
        EventAddressTextCodec.Format(epoch.PlannedAtRawHead),
        EventAddressTextCodec.Format(epoch.SourceStartExclusive),
        EventAddressTextCodec.Format(epoch.SourceEndInclusive),
        epoch.MeasuredTokens,
        ToSummary(epoch.PlanningDiagnostics)
    );

    private static EpochDiagnosticsSummary ToSummary(
        DerivedArtifactEpochPlanningDiagnostics diagnostics
    ) => new(
        diagnostics.HeaderVisits,
        diagnostics.PayloadReads,
        diagnostics.DecodedPayloadBytes,
        diagnostics.DecodedEventCount,
        diagnostics.DependencyClosedUnitCount,
        diagnostics.ReplaySafeBoundaryCount,
        diagnostics.TotalTokens,
        diagnostics.EligibleTokens,
        diagnostics.RetainedRecentTokens
    );
}

internal sealed record DerivedArtifactSetOperationReport(
    string Schema,
    string Operation,
    DerivedArtifactSetSummaryReport Set
);

internal sealed record DerivedArtifactSetInventoryReport(
    string Schema,
    IReadOnlyList<DerivedArtifactSetSummaryReport> Sets,
    IReadOnlyList<DerivedArtifactSetPointerReport> LatestPointers
);

internal sealed record DerivedMemoryValidationCliReport(
    string Schema,
    int ArtifactCount,
    int ArtifactSetCount,
    int LatestPointerCount,
    int ExactArtifactSetKeyCount,
    int PlannerConfigCount,
    int CurrentPlannerConfigCount,
    int ArtifactEpochCount,
    int LatestArtifactEpochCount
);

internal sealed record PlannerConfigOperationReport(
    string Schema,
    PlannerConfigSummary Config
);

internal sealed record EpochOperationReport(
    string Schema,
    string Status,
    PlannerConfigSummary Config,
    EpochSummary? Epoch,
    EpochDiagnosticsSummary Diagnostics
);

internal sealed record EpochInventoryReport(
    string Schema,
    IReadOnlyList<PlannerConfigSummary> Configs,
    IReadOnlyList<PlannerConfigPointerSummary> CurrentConfigs,
    IReadOnlyList<EpochSummary> Epochs,
    IReadOnlyList<EpochPointerSummary> LatestEpochs
);

internal sealed record PlannerConfigSummary(
    string ConfigId,
    string LineageKey,
    string CoherenceGroup,
    string? PreviousConfigId,
    string TopologyVersion,
    long MinimumRecentTokens,
    long EpochTriggerTokens,
    long SchedulingHeadroomTokens,
    long HardLimitTokens,
    string TokenEstimatorId,
    string BoundaryPolicyId,
    string HardLimitPolicyId,
    string GenesisPolicyId
);

internal sealed record PlannerConfigPointerSummary(
    string LineageKey,
    string CoherenceGroup,
    string ConfigId
);

internal sealed record EpochSummary(
    string EpochId,
    string LineageKey,
    string CoherenceGroup,
    string TopologyVersion,
    string ConfigId,
    string? PreviousEpochId,
    string? InputSetId,
    string PlannedAtRawHead,
    string SourceStartExclusive,
    string SourceEndInclusive,
    long MeasuredTokens,
    EpochDiagnosticsSummary PlanningDiagnostics
);

internal sealed record EpochPointerSummary(
    string LineageKey,
    string CoherenceGroup,
    string EpochId
);

internal sealed record EpochDiagnosticsSummary(
    long HeaderVisits,
    long PayloadReads,
    long DecodedPayloadBytes,
    int DecodedEventCount,
    int DependencyClosedUnitCount,
    int ReplaySafeBoundaryCount,
    long TotalTokens,
    long EligibleTokens,
    long RetainedRecentTokens
);

internal sealed record DerivedArtifactSetSummaryReport(
    string SetId,
    string LineageKey,
    string CoherenceGroup,
    string PolicyId,
    string PolicyFingerprint,
    IReadOnlyList<DerivedArtifactSetRoleReport> RoleRequirements,
    string? PreviousSetId,
    string CommonAnchor,
    DerivedArtifactSetSetupReferencesReport AnchorSetups,
    IReadOnlyList<DerivedArtifactSetMemberReport> Members
);

internal sealed record DerivedArtifactSetRoleReport(
    string RoleId,
    string Carrier,
    string BlockKey,
    bool Required
);

internal sealed record DerivedArtifactSetMemberReport(
    string RoleId,
    string ArtifactId,
    string ArtifactKind,
    string Carrier,
    string BlockKey,
    string ContentCodecId,
    string ContentSha256,
    string SourceRawHead
);

internal sealed record DerivedArtifactSetSetupReferencesReport(
    DerivedArtifactSetSetupReferenceReport RuntimeConfig,
    DerivedArtifactSetSetupReferenceReport SystemPrompt
);

internal sealed record DerivedArtifactSetSetupReferenceReport(
    string Address,
    int BodySchemaVersion,
    string PayloadSha256
);

internal sealed record DerivedArtifactSetPointerReport(
    string LineageKey,
    string CoherenceGroup,
    string PolicyId,
    string PolicyFingerprint,
    string SetId
);
