using Atelia.SessionJournal.RecapGrid.Store;
using static Atelia.SessionJournal.Cli.RecapGridCommands;

namespace Atelia.SessionJournal.Cli;

internal static class RecapGridStoreCommands {
    internal static int Run(string[] args) {
        if (args.Length == 0) {
            throw new ArgumentException(
                "recap-grid requires inspect, export, verify, or reset."
            );
        }
        string subcommand = args[0];
        CliOptions options = CliOptions.Parse(args.Skip(1).ToArray());
        return subcommand switch {
            "inspect" => Inspect(options),
            "export" => Export(options),
            "verify" => Verify(options),
            "reset" => Reset(options),
            _ => throw new ArgumentException(
                $"Unknown recap-grid subcommand '{subcommand}'."
            )
        };
    }

    private static int Inspect(CliOptions options) {
        options.EnsureOnly("input");
        string repository = options.RequireSingle("input");
        RecapGridStoreInspectResult result =
            RecapGridStoreMaintenance.Inspect(repository);
        return result switch {
            RecapGridStoreInspectResult.Available available => Print(
                "inspect",
                "available",
                new {
                    instanceId = available.Info.Identity.InstanceId.Value,
                    schemaVersion = available.Info.Identity.SchemaVersion,
                    databaseBytes = available.Info.DatabaseBytes,
                    available.Info.CellCount,
                    available.Info.RowViewCount,
                    available.Info.RowViewMemberCount,
                    available.Info.FulfilledViewCount,
                    available.Info.SqliteVersion,
                    available.Info.SqliteSourceId,
                    available.Info.CompileOptions
                }
            ),
            RecapGridStoreInspectResult.Absent => Print("inspect", "absent"),
            RecapGridStoreInspectResult.Busy => Print(
                "inspect", "busy", exitCode: 2
            ),
            RecapGridStoreInspectResult.UnsupportedSchema unsupported =>
                Print(
                    "inspect",
                    "unsupported-schema",
                    new { unsupported.SchemaVersion },
                    2
                ),
            RecapGridStoreInspectResult.PlatformUnsupported =>
                Print("inspect", "platform-unsupported", exitCode: 2),
            RecapGridStoreInspectResult.Invalid invalid => Print(
                "inspect",
                "invalid",
                new { invalid.Code, invalid.Detail },
                2
            ),
            _ => throw new InvalidOperationException(
                "Unknown RecapGrid Store inspect result."
            )
        };
    }

    private static int Verify(CliOptions options) {
        options.EnsureOnly("input");
        string repository = options.RequireSingle("input");
        RecapGridStoreVerifyResult result =
            RecapGridStoreMaintenance.Verify(repository);
        return result switch {
            RecapGridStoreVerifyResult.Healthy healthy => Print(
                "verify",
                "healthy",
                new {
                    instanceId = healthy.Info.Identity.InstanceId.Value,
                    schemaVersion = healthy.Info.Identity.SchemaVersion,
                    healthy.Info.CellCount,
                    healthy.Info.RowViewCount,
                    healthy.Info.RowViewMemberCount,
                    healthy.Info.FulfilledViewCount
                }
            ),
            RecapGridStoreVerifyResult.Absent => Print("verify", "absent"),
            RecapGridStoreVerifyResult.Busy => Print(
                "verify", "busy", exitCode: 2
            ),
            RecapGridStoreVerifyResult.UnsupportedSchema unsupported =>
                Print(
                    "verify",
                    "unsupported-schema",
                    new { unsupported.SchemaVersion },
                    2
                ),
            RecapGridStoreVerifyResult.PlatformUnsupported =>
                Print("verify", "platform-unsupported", exitCode: 2),
            RecapGridStoreVerifyResult.Unhealthy unhealthy => Print(
                "verify",
                "unhealthy",
                new { unhealthy.Errors, unhealthy.Incomplete },
                2
            ),
            _ => throw new InvalidOperationException(
                "Unknown RecapGrid Store verify result."
            )
        };
    }

    private static int Export(CliOptions options) {
        options.EnsureOnly("input", "after", "include-content");
        string repository = options.RequireSingle("input");
        string? afterValue = options.GetOptionalSingle("after");
        if (options.GetAll("include-content").Count > 1) {
            throw new ArgumentException(
                "Option --include-content must be specified at most once."
            );
        }
        RecapGridStoreExportCursor? after = afterValue is null
            ? null
            : RecapGridStoreExportCursor.Parse(afterValue);
        RecapGridStoreExportResult result =
            RecapGridStoreMaintenance.Export(
                repository,
                after,
                options.HasFlag("include-content")
            );
        return result switch {
            RecapGridStoreExportResult.Page page => Print(
                "export",
                "page",
                new {
                    items = page.Value.Items.Select(static item => new {
                        item.Kind,
                        item.Key,
                        item.CanonicalBytes,
                        fulfilledViewDigest =
                            item.FulfilledViewDigest?.Value,
                        canonicalBase64 = item.Canonical is null
                            ? null
                            : Convert.ToBase64String(item.Canonical)
                    }),
                    nextCursor = page.Value.NextCursor?.Value,
                    page.Value.Incomplete
                }
            ),
            RecapGridStoreExportResult.Absent => Print("export", "absent"),
            RecapGridStoreExportResult.Busy => Print(
                "export", "busy", exitCode: 2
            ),
            RecapGridStoreExportResult.UnsupportedSchema unsupported =>
                Print(
                    "export",
                    "unsupported-schema",
                    new { unsupported.SchemaVersion },
                    2
                ),
            RecapGridStoreExportResult.PlatformUnsupported =>
                Print("export", "platform-unsupported", exitCode: 2),
            RecapGridStoreExportResult.Invalid invalid => Print(
                "export",
                "invalid",
                new { invalid.Code, invalid.Detail },
                2
            ),
            _ => throw new InvalidOperationException(
                "Unknown RecapGrid Store export result."
            )
        };
    }

    private static int Reset(CliOptions options) {
        options.EnsureOnly(
            "input",
            "prepare",
            "confirm-length",
            "confirm-sha256"
        );
        string repository = options.RequireSingle("input");
        if (options.GetAll("prepare").Count > 1) {
            throw new ArgumentException(
                "Option --prepare must be specified at most once."
            );
        }
        if (options.HasFlag("prepare")) {
            if (options.GetOptionalSingle("confirm-length") is not null
                || options.GetOptionalSingle("confirm-sha256") is not null) {
                throw new ArgumentException(
                    "--prepare cannot be combined with reset confirmation."
                );
            }
            return PrepareReset(repository);
        }
        string lengthText = options.RequireSingle("confirm-length");
        if (!long.TryParse(lengthText, out long length) || length < 1) {
            throw new ArgumentException(
                "--confirm-length must be a positive integer."
            );
        }
        var witness = new RecapGridStorePhysicalWitness(
            length,
            options.RequireSingle("confirm-sha256")
        );
        RecapGridStoreResetResult result =
            RecapGridStoreMaintenance.Reset(repository, witness);
        return result switch {
            RecapGridStoreResetResult.Reset reset => Print(
                "reset",
                "reset",
                new {
                    instanceId = reset.Identity.InstanceId.Value,
                    schemaVersion = reset.Identity.SchemaVersion
                }
            ),
            RecapGridStoreResetResult.Absent => Print("reset", "absent"),
            RecapGridStoreResetResult.Busy => Print(
                "reset", "busy", exitCode: 2
            ),
            RecapGridStoreResetResult.StaleConfirmation stale => Print(
                "reset",
                "stale-confirmation",
                new {
                    actualLength = stale.Actual.Length,
                    actualSha256 = stale.Actual.Sha256
                },
                2
            ),
            RecapGridStoreResetResult.OfflineCleanupRequired cleanup =>
                Print(
                    "reset",
                    "offline-cleanup-required",
                    new { cleanup.Slot },
                    2
                ),
            RecapGridStoreResetResult.Limit limit =>
                Print("reset", "limit", new { limit.Name }, 2),
            RecapGridStoreResetResult.CommitIndeterminate settlement =>
                Print(
                    "reset",
                    "commit-indeterminate",
                    new {
                        intendedInstanceId =
                            settlement.Intended.InstanceId.Value,
                        observedInstanceId =
                            settlement.Observed?.InstanceId.Value
                    },
                    2
                ),
            RecapGridStoreResetResult.PlatformUnsupported =>
                Print("reset", "platform-unsupported", exitCode: 2),
            RecapGridStoreResetResult.Invalid invalid => Print(
                "reset",
                "invalid",
                new { invalid.Code, invalid.Detail },
                2
            ),
            _ => throw new InvalidOperationException(
                "Unknown RecapGrid Store reset result."
            )
        };
    }

    private static int PrepareReset(string repository) {
        RecapGridStorePrepareResetResult result =
            RecapGridStoreMaintenance.PrepareReset(repository);
        return result switch {
            RecapGridStorePrepareResetResult.Prepared prepared => Print(
                "reset",
                "prepared",
                new {
                    length = prepared.Witness.Length,
                    sha256 = prepared.Witness.Sha256
                }
            ),
            RecapGridStorePrepareResetResult.Absent => Print(
                "reset", "absent"
            ),
            RecapGridStorePrepareResetResult.Busy =>
                Print("reset", "busy", exitCode: 2),
            RecapGridStorePrepareResetResult.OfflineCleanupRequired cleanup =>
                Print(
                    "reset",
                    "offline-cleanup-required",
                    new { cleanup.Slot },
                    2
                ),
            RecapGridStorePrepareResetResult.Limit limit =>
                Print("reset", "limit", new { limit.Name }, 2),
            RecapGridStorePrepareResetResult.PlatformUnsupported =>
                Print("reset", "platform-unsupported", exitCode: 2),
            RecapGridStorePrepareResetResult.Invalid invalid => Print(
                "reset",
                "invalid",
                new { invalid.Code, invalid.Detail },
                2
            ),
            _ => throw new InvalidOperationException(
                "Unknown RecapGrid Store prepare-reset result."
            )
        };
    }
}
