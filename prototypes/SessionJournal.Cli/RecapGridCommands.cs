using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;

namespace Atelia.SessionJournal.Cli;

internal static partial class RecapGridCommands {
    private const string ReportSchema =
        "atelia.session-journal.recap-grid-cli.v1";
    private const int MaximumReportUtf8Bytes = 16 * 1024 * 1024;
    private const int MaximumInputUtf8Bytes = 1024 * 1024;

    internal static ValueTask<int> RunAsync(
        string[] args,
        ICompletionClientFactory completionClientFactory
    ) {
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        if (args.Length == 0) {
            throw new ArgumentException(
                "recap-grid requires a subcommand."
            );
        }
        string command = args[0];
        string[] tail = args.Skip(1).ToArray();
        return command switch {
            "inspect" or "export" or "verify" or "reset"
                => ValueTask.FromResult(RecapGridStoreCommands.Run(args)),
            "init" => ValueTask.FromResult(Init(CliOptions.Parse(tail))),
            "scaffold" => ValueTask.FromResult(
                Scaffold(CliOptions.Parse(tail))
            ),
            "timeline" => ValueTask.FromResult(
                Timeline(tail)
            ),
            "cadence" => ValueTask.FromResult(
                Cadence(tail)
            ),
            "control" => ValueTask.FromResult(
                Control(tail)
            ),
            "build" => BuildAsync(
                CliOptions.Parse(tail),
                completionClientFactory
            ),
            "progress" => ValueTask.FromResult(
                Progress(CliOptions.Parse(tail))
            ),
            "materialize" => ValueTask.FromResult(
                Materialize(CliOptions.Parse(tail))
            ),
            "legacy-root" => ValueTask.FromResult(
                LegacyRoot(tail)
            ),
            _ => throw new ArgumentException(
                $"Unknown recap-grid command '{command}'."
            )
        };
    }

    private static int Timeline(string[] args) {
        if (args.Length == 0) {
            throw new ArgumentException(
                "recap-grid timeline requires a subcommand."
            );
        }
        string action = args[0];
        if (string.Equals(action, "history-load", StringComparison.Ordinal)) {
            return RecapHistoryLoadCommands.RunAsync(
                    args.Skip(1).ToArray()
                )
                .GetAwaiter()
                .GetResult();
        }
        CliOptions options = CliOptions.Parse(args.Skip(1).ToArray());
        return action switch {
            "create" => TimelineCreate(options),
            "sync" => TimelineSync(options),
            "inspect" => TimelineInspect(options, verify: false),
            "verify" => TimelineInspect(options, verify: true),
            "export" => TimelineExport(options),
            "backup" => TimelineBackup(options),
            "restore" => TimelineRestore(options),
            "abandon" => TimelineAbandon(options),
            _ => throw new ArgumentException(
                $"Unknown recap-grid timeline command '{action}'."
            )
        };
    }

    private static int Control(string[] args) {
        if (args.Length == 0) {
            throw new ArgumentException(
                "recap-grid control requires a subcommand."
            );
        }
        string action = args[0];
        CliOptions options = CliOptions.Parse(args.Skip(1).ToArray());
        return action switch {
            "create" => ControlCreate(options),
            "inspect" => ControlInspect(options, verify: false),
            "verify" => ControlInspect(options, verify: true),
            "export" => ControlExport(options),
            "put-family" => ControlPutFamily(options),
            "put-definition" => ControlPutDefinition(options),
            "compose-full-recipe" => ControlComposeFullRecipe(options),
            "put-recipe" => ControlPutRecipe(options),
            "provision-asset" => ControlProvisionAsset(options),
            "activate" => ControlActivate(options),
            "promote" => ControlPromote(options),
            "backup" => ControlBackup(options),
            "restore" => ControlRestore(options),
            "reinitialize" => ControlReinitialize(options),
            _ => throw new ArgumentException(
                $"Unknown recap-grid control command '{action}'."
            )
        };
    }

    private static int Cadence(string[] args) {
        if (args.Length == 0) {
            throw new ArgumentException(
                "recap-grid cadence requires a subcommand."
            );
        }
        string action = args[0];
        CliOptions options = CliOptions.Parse(args.Skip(1).ToArray());
        return action switch {
            "inspect" => CadenceInspect(options),
            "set-reserve" => CadenceSetReserve(options),
            _ => throw new ArgumentException(
                $"Unknown recap-grid cadence command '{action}'."
            )
        };
    }

    private static int Print(
        string command,
        string status,
        object? detail = null,
        int exitCode = 0
    ) {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new {
            schema = ReportSchema,
            command,
            status,
            detail
        });
        if (bytes.Length > MaximumReportUtf8Bytes) {
            bytes = JsonSerializer.SerializeToUtf8Bytes(new {
                schema = ReportSchema,
                command,
                status = "limit-exceeded",
                detail = new { limit = "RecapGridReportUtf8Bytes" }
            });
            exitCode = 2;
        }
        Console.WriteLine(Encoding.UTF8.GetString(bytes));
        return exitCode;
    }

    private static SessionJournalEngine OpenBranch(CliOptions options) {
        string repository = options.RequireSingle("input");
        string branch = options.GetOptionalSingle("branch")
            ?? SessionJournalDefaults.MainBranchName;
        CliIo.EnsurePathChainHasNoReparsePoint(repository, "--input");
        return SessionJournalEngine.OpenReadOnly(repository, branch);
    }

    private static SessionJournalEngine OpenMutableBranch(
        CliOptions options
    ) {
        string repository = options.RequireSingle("input");
        string branch = options.GetOptionalSingle("branch")
            ?? SessionJournalDefaults.MainBranchName;
        CliIo.EnsurePathChainHasNoReparsePoint(repository, "--input");
        return SessionJournalEngine.Open(repository, branch);
    }

    private static void RequireConfirmedRef(
        CliOptions options,
        RefId actual
    ) {
        string confirmation = options.RequireSingle("confirm-ref");
        if (!string.Equals(
                confirmation,
                actual.ToHexString(),
                StringComparison.Ordinal)) {
            throw new ArgumentException(
                "--confirm-ref differs from the selected branch RefId."
            );
        }
    }

    private static HistoryTimelineInitialPolicySpec ReadInitialPolicy(
        CliOptions options
    ) {
        string algorithm = options.RequireSingle("partition-algorithm");
        string estimator = options.RequireSingle("history-load-estimator");
        if (!string.Equals(
                algorithm,
                HistoryPartitionAlgorithms
                    .FirstReplaySafeBoundaryAtTargetV1,
                StringComparison.Ordinal)) {
            throw new ArgumentException(
                "The RecapGrid CLI supports only the V1 replay-safe partition algorithm.");
        }
        if (!string.Equals(
                estimator,
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                StringComparison.Ordinal)) {
            throw new ArgumentException(
                "The RecapGrid CLI supports only the O200k base estimator.");
        }
        return new HistoryTimelineInitialPolicySpec(
            algorithm,
            estimator,
            new HistoryLoadUnit(RequirePositiveLong(
                options,
                "target-history-load")),
            RequirePositiveInt(options, "max-raw-events"),
            RequirePositiveInt(options, "max-rendered-bytes"));
    }

    private static RecapGridCadencePolicySpec ReadCadencePolicy(
        CliOptions options
    ) {
        string algorithm = options.RequireSingle("partition-algorithm");
        string estimator = options.RequireSingle("history-load-estimator");
        if (!string.Equals(
                algorithm,
                HistoryPartitionAlgorithms
                    .FirstReplaySafeBoundaryAtTargetV1,
                StringComparison.Ordinal)) {
            throw new ArgumentException(
                "The RecapGrid CLI supports only the V1 replay-safe partition algorithm."
            );
        }
        if (!string.Equals(
                estimator,
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                StringComparison.Ordinal)) {
            throw new ArgumentException(
                "The RecapGrid CLI supports only the O200k base estimator."
            );
        }
        return new RecapGridCadencePolicySpec(
            RequirePositiveLong(options, "minimum-recent-history-load"),
            algorithm,
            estimator,
            RequirePositiveLong(options, "target-history-load"),
            RequirePositiveInt(options, "max-raw-events"),
            RequirePositiveInt(options, "max-rendered-bytes")
        );
    }

    private static HistoryTimelineInitialPolicySpec ToTimelineInitialPolicy(
        RecapGridCadencePolicySpec policy
    ) => new(
        policy.PartitionAlgorithmId,
        policy.HistoryLoadEstimatorId,
        new HistoryLoadUnit(policy.TargetHistoryLoad),
        policy.MaxRawEvents,
        policy.MaxRenderedBytes);

    private static long RequirePositiveLong(CliOptions options, string key) {
        string value = options.RequireSingle(key);
        return long.TryParse(value, out long parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException(
                $"--{key} must be a positive integer.");
    }

    private static int RequirePositiveInt(CliOptions options, string key) {
        string value = options.RequireSingle(key);
        return int.TryParse(value, out int parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException(
                $"--{key} must be a positive integer."
            );
    }

    private static long RequireNonNegativeLong(
        CliOptions options,
        string key
    ) {
        string value = options.RequireSingle(key);
        return long.TryParse(value, out long parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException(
                $"--{key} must be a non-negative integer."
            );
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes) {
        CliIo.EnsurePathChainHasNoReparsePoint(path, "input file");
        using var stream = new FileStream(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan
        );
        if (stream.Length is < 1 || stream.Length > maximumBytes) {
            throw new InvalidDataException(
                "Input file is empty or exceeds its code-owned bound."
            );
        }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static RecapGridControlAdmission ReadAdmission(
        CliOptions options
    ) => RecapGridControlAdmission.DecodeCanonical(ReadBoundedFile(
        options.RequireSingle("admission"),
        MaximumInputUtf8Bytes
    ));
}
