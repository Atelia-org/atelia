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
        "atelia.session-journal.cli.derived-memory-validation.v1";

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
            validation.ExactArtifactSetKeyCount
        );
        WriteOptionalReport(reportPath, report);
        Console.WriteLine($"artifacts: {report.ArtifactCount}");
        Console.WriteLine($"sets: {report.ArtifactSetCount}");
        Console.WriteLine($"latestPointers: {report.LatestPointerCount}");
        Console.WriteLine($"exactKeys: {report.ExactArtifactSetKeyCount}");
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
    int ExactArtifactSetKeyCount
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
