using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;

namespace Atelia.SessionJournal.Cli;

internal static partial class RecapGridCommands {
    private static int ControlCreate(CliOptions options) {
        options.EnsureOnly("input", "branch", "confirm-ref", "admission");
        using SessionJournalEngine engine = OpenBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        RecapGridControlCreateResult result = RecapGridControlFactory.Create(
            engine.ReadView.Path,
            engine.BranchRefId,
            ReadAdmission(options)
        );
        bool success = IsControlCreated(result);
        return Print(
            "control.create",
            success ? "ready" : "failed",
            DescribeControlCreate(result),
            success ? 0 : 2
        );
    }

    private static int ControlInspect(CliOptions options, bool verify) {
        options.EnsureOnly("input", "branch");
        using SessionJournalEngine engine = OpenBranch(options);
        RecapGridControlInspectResult result = verify
            ? RecapGridControlMaintenance.Verify(
                engine.ReadView.Path,
                engine.BranchRefId
            )
            : RecapGridControlMaintenance.Inspect(
                engine.ReadView.Path,
                engine.BranchRefId
            );
        string command = verify ? "control.verify" : "control.inspect";
        return result switch {
            RecapGridControlInspectResult.Available available => Print(
                command, "available", DescribeControlSnapshot(available.Snapshot)),
            RecapGridControlInspectResult.Absent => Print(
                command, "absent", exitCode: 2),
            RecapGridControlInspectResult.TimelineAbsent => Print(
                command, "timeline-absent", exitCode: 2),
            RecapGridControlInspectResult.TimelineUnsupportedSchema schema =>
                Print(command, "timeline-unsupported", schema, 2),
            RecapGridControlInspectResult.Busy => Print(
                command, "busy", exitCode: 2),
            RecapGridControlInspectResult.UnsupportedSchema schema => Print(
                command, "control-unsupported", schema, 2),
            RecapGridControlInspectResult.Invalid invalid => Print(
                command, "invalid", invalid, 2),
            _ => Print(command, "invalid-outcome", exitCode: 2)
        };
    }

    private static int ControlExport(CliOptions options) {
        options.EnsureOnly("input", "branch", "output");
        using SessionJournalEngine engine = OpenBranch(options);
        RecapGridControlExportResult result = RecapGridControlMaintenance.Export(
            engine.ReadView.Path,
            engine.BranchRefId
        );
        if (result is RecapGridControlExportResult.Available available) {
            string output = options.RequireSingle("output");
            CliIo.ValidateFileOutputPath(
                engine.ReadView.Path,
                output,
                "--output"
            );
            WriteExternalCreateNew(output, available.CanonicalState);
            return Print(
                "control.export",
                "created",
                new {
                    output = Path.GetFullPath(output),
                    snapshot = DescribeControlSnapshot(available.Snapshot)
                }
            );
        }
        return result switch {
            RecapGridControlExportResult.Absent => Print(
                "control.export", "absent", exitCode: 2),
            RecapGridControlExportResult.TimelineAbsent => Print(
                "control.export", "timeline-absent", exitCode: 2),
            RecapGridControlExportResult.TimelineUnsupportedSchema schema =>
                Print("control.export", "timeline-unsupported", schema, 2),
            RecapGridControlExportResult.Busy => Print(
                "control.export", "busy", exitCode: 2),
            RecapGridControlExportResult.UnsupportedSchema schema => Print(
                "control.export", "control-unsupported", schema, 2),
            RecapGridControlExportResult.Invalid invalid => Print(
                "control.export", "invalid", invalid, 2),
            _ => Print("control.export", "invalid-outcome", exitCode: 2)
        };
    }

    private static int ControlPutFamily(CliOptions options) {
        options.EnsureOnly(
            "input", "branch", "confirm-ref", "admission", "family"
        );
        FamilyDefinition value = FamilyDefinition.DecodeCanonical(
            ReadBoundedFile(options.RequireSingle("family"), MaximumInputUtf8Bytes)
        );
        return WithControlMutation(
            options,
            "control.put-family",
            static (handle, controlHead, _, state) =>
                handle.Coordinator.PutFamilyDefinition(controlHead, state),
            value
        );
    }

    private static int ControlPutDefinition(CliOptions options) {
        options.EnsureOnly(
            "input", "branch", "confirm-ref", "admission", "definition"
        );
        MaintainerDefinitionRevision value =
            MaintainerDefinitionRevision.DecodeCanonical(
                ReadBoundedFile(
                    options.RequireSingle("definition"),
                    MaximumInputUtf8Bytes
                )
            );
        return WithControlMutation(
            options,
            "control.put-definition",
            static (handle, controlHead, _, state) =>
                handle.Coordinator.PutMaintainerDefinition(controlHead, state),
            value
        );
    }

    private static int ControlPutRecipe(CliOptions options) {
        options.EnsureOnly(
            "input", "branch", "confirm-ref", "admission", "recipe"
        );
        GridBuildRecipe recipe = GridBuildRecipe.DecodeCanonical(
            ReadBoundedFile(options.RequireSingle("recipe"), MaximumInputUtf8Bytes)
        );
        using SessionJournalEngine engine = OpenBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        RecapGridControlOpenResult opened = RecapGridControlFactory.Open(
            engine.ReadView.Path,
            engine.BranchRefId,
            ReadAdmission(options)
        );
        if (opened is not RecapGridControlOpenResult.Opened controlOpened) {
            return PrintControlOpenFailure("control.put-recipe", opened);
        }
        using (controlOpened.Handle) {
            RecapGridControlSnapshotResult snapshotResult =
                controlOpened.Handle.Reader.ReadSnapshot();
            if (snapshotResult is not RecapGridControlSnapshotResult.Available
                    control) {
                return PrintControlSnapshotFailure(
                    "control.put-recipe", snapshotResult
                );
            }
            HistoryTimelineReaderOpenResult timelineOpened =
                HistoryTimelineMaintenance.OpenReader(
                    engine.ReadView.Path,
                    engine.BranchRefId
                );
            if (timelineOpened is not HistoryTimelineReaderOpenResult.Opened
                    timeline) {
                return PrintTimelineReaderOpenFailure(
                    "control.put-recipe", timelineOpened
                );
            }
            using (timeline.Handle) {
                HistoryTimelineSnapshotResult timelineSnapshot =
                    timeline.Handle.Reader.ReadSnapshot();
                if (timelineSnapshot is not HistoryTimelineSnapshotResult
                        .Available head) {
                    return PrintTimelineSnapshotFailure(
                        "control.put-recipe", timelineSnapshot
                    );
                }
                HistoryTimelineAncestorWitness? witness = null;
                if (recipe.BootstrapThroughRowId is { } rowId) {
                    HistoryTimelineReaderRowResult row = timeline.Handle.Reader
                        .ReadSelectedRow(head.Head, rowId);
                    if (row is not HistoryTimelineReaderRowResult.Selected
                            selected) {
                        return PrintTimelineRowFailure(
                            "control.put-recipe", row
                        );
                    }
                    witness = selected.Row.Witness;
                }
                RecapGridControlPutResult result = controlOpened.Handle
                    .Coordinator.PutBuildRecipe(
                        control.Snapshot.Head,
                        head.Head,
                        recipe,
                        witness
                    );
                return PrintControlPut("control.put-recipe", result);
            }
        }
    }

    private static int ControlComposeFullRecipe(CliOptions options) {
        options.EnsureOnly("input", "branch", "definition", "output");
        string output = options.RequireSingle("output");
        string input = options.RequireSingle("input");
        CliIo.ValidateFileOutputPath(input, output, "--output");
        MaintainerDefinitionDigest[] orderedDigests = options
            .GetAll("definition")
            .Select(static value => string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException(
                    "Every --definition value must be non-empty."
                )
                : new MaintainerDefinitionDigest(value))
            .Take(RecapGridLimits.MaximumColumnCount + 1)
            .ToArray();
        if (orderedDigests.Length > RecapGridLimits.MaximumColumnCount
            || orderedDigests.Distinct().Count()
                != orderedDigests.Length) {
            throw new ArgumentException(
                "--definition values must be an ordered unique bounded list."
            );
        }
        using SessionJournalEngine engine = OpenBranch(options);
        RecapGridControlReaderOpenResult controlOpened =
            RecapGridControlFactory.OpenReader(
                engine.ReadView.Path,
                engine.BranchRefId
            );
        if (controlOpened is not RecapGridControlReaderOpenResult.Opened
                control) {
            return PrintControlReaderOpenFailure(
                "control.compose-full-recipe",
                controlOpened
            );
        }
        using (control.Handle) {
            RecapGridControlSnapshotResult controlRead =
                control.Handle.Reader.ReadSnapshot();
            if (controlRead is not RecapGridControlSnapshotResult.Available
                    current) {
                return PrintControlSnapshotFailure(
                    "control.compose-full-recipe",
                    controlRead
                );
            }
            var definitions = current.Snapshot.Definitions.ToDictionary(
                static value => value.Digest
            );
            var targetColumns = new List<BuildTargetColumn>(
                orderedDigests.Length
            );
            foreach (MaintainerDefinitionDigest digest in orderedDigests) {
                if (!definitions.TryGetValue(
                        digest,
                        out MaintainerDefinitionRevision? definition)) {
                    return Print(
                        "control.compose-full-recipe",
                        "definition-absent",
                        new { definitionDigest = digest.Value },
                        2
                    );
                }
                targetColumns.Add(new BuildTargetColumn(
                    definition.LogicalColumnId,
                    definition.Digest
                ));
            }
            HistoryTimelineReaderOpenResult timelineOpened =
                HistoryTimelineMaintenance.OpenReader(
                    engine.ReadView.Path,
                    engine.BranchRefId
                );
            if (timelineOpened is not HistoryTimelineReaderOpenResult.Opened
                    timeline) {
                return PrintTimelineReaderOpenFailure(
                    "control.compose-full-recipe",
                    timelineOpened
                );
            }
            using (timeline.Handle) {
                HistoryTimelineSnapshotResult timelineRead =
                    timeline.Handle.Reader.ReadSnapshot();
                if (timelineRead is not HistoryTimelineSnapshotResult
                        .Available available) {
                    return PrintTimelineSnapshotFailure(
                        "control.compose-full-recipe",
                        timelineRead
                    );
                }
                GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
                    available.Head.TimelineId,
                    available.Head.HeadRowId,
                    BuildTarget.Create(targetColumns)
                );
                WriteExternalCreateNew(output, recipe.ToCanonicalBytes());
                return Print(
                    "control.compose-full-recipe",
                    "created",
                    new {
                        output = Path.GetFullPath(output),
                        recipeDigest = recipe.Digest.Value,
                        bootstrapThroughRowId =
                            recipe.BootstrapThroughRowId?.Value,
                        orderedDefinitions = orderedDigests.Select(
                            static value => value.Value
                        )
                    }
                );
            }
        }
    }

    private static int ControlProvisionAsset(CliOptions options) {
        options.EnsureOnly(
            "input", "branch", "confirm-ref", "admission", "asset"
        );
        string assetId = options.RequireSingle("asset");
        if (!RecapGridOperatorAssetCatalog.TryCreateRegistrationBundle(
                assetId,
                out RecapGridControlRegistrationBundle? bundle)
            || bundle is null) {
            return Print(
                "control.provision-asset",
                "operator-asset-absent",
                new { assetId },
                2
            );
        }
        RecapGridControlAdmission admission = ReadAdmission(options);
        using SessionJournalEngine engine = OpenBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        RecapGridControlOpenResult controlResult =
            RecapGridControlFactory.Open(
                engine.ReadView.Path,
                engine.BranchRefId,
                admission
            );
        if (controlResult is not RecapGridControlOpenResult.Opened control) {
            return PrintControlOpenFailure(
                "control.provision-asset",
                controlResult
            );
        }
        using (control.Handle) {
            RecapGridControlSnapshotResult controlSnapshot =
                control.Handle.Reader.ReadSnapshot();
            if (controlSnapshot is not RecapGridControlSnapshotResult
                    .Available currentControl) {
                return PrintControlSnapshotFailure(
                    "control.provision-asset",
                    controlSnapshot
                );
            }
            RecapGridControlOperation operation =
                RecapGridOperatorAssetCatalog.CreateProvisionOperation(
                    assetId,
                    currentControl.Snapshot.Head.InstanceId
                );
            HistoryTimelineReaderOpenResult timelineResult =
                HistoryTimelineMaintenance.OpenReader(
                    engine.ReadView.Path,
                    engine.BranchRefId
                );
            if (timelineResult is not HistoryTimelineReaderOpenResult.Opened
                    timeline) {
                return PrintTimelineReaderOpenFailure(
                    "control.provision-asset",
                    timelineResult
                );
            }
            using (timeline.Handle) {
                HistoryTimelineSnapshotResult timelineSnapshot =
                    timeline.Handle.Reader.ReadSnapshot();
                if (timelineSnapshot is not HistoryTimelineSnapshotResult
                        .Available currentTimeline) {
                    return PrintTimelineSnapshotFailure(
                        "control.provision-asset",
                        timelineSnapshot
                    );
                }
                return PrintControlOperation(
                    "control.provision-asset",
                    control.Handle.Coordinator.ApplyRegistrationBundle(
                        currentControl.Snapshot.Head,
                        currentTimeline.Head,
                        operation,
                        bundle
                    )
                );
            }
        }
    }

    private static int ControlActivate(CliOptions options) {
        options.EnsureOnly(
            "input", "branch", "confirm-ref", "admission", "recipe",
            "deactivate", "confirm-instance", "confirm-timeline",
            "confirm-generation", "confirm-state", "confirm-active",
            "confirm-timeline-head"
        );
        bool deactivate = options.HasSingleFlag("deactivate");
        string? recipeText = options.GetOptionalSingle("recipe");
        if (deactivate == (recipeText is not null)) {
            throw new ArgumentException(
                "Specify exactly one of --recipe or --deactivate."
            );
        }
        return Activate(
            options,
            recipeText is null ? null : new GridBuildRecipeDigest(recipeText),
            RecapGridControlActivationPurpose.Direct,
            "control.activate"
        );
    }

    private static int ControlBackup(CliOptions options) {
        options.EnsureOnly(
            "input", "branch", "confirm-ref", "confirm-instance",
            "confirm-timeline", "confirm-generation", "confirm-state",
            "confirm-active", "output"
        );
        using SessionJournalEngine engine = OpenBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        ControlHeadRef expected = ReadControlHeadConfirmation(
            options,
            engine.BranchRefId
        );
        string output = options.RequireSingle("output");
        CliIo.EnsurePathChainHasNoReparsePoint(output, "--output");
        RecapGridControlBackupResult result =
            RecapGridControlMaintenance.Backup(
                engine.ReadView.Path,
                engine.BranchRefId,
                expected,
                output
            );
        return result switch {
            RecapGridControlBackupResult.Created created => Print(
                "control.backup", "created", created),
            RecapGridControlBackupResult.PublishIndeterminate uncertain =>
                PrintIndeterminate(
                    "control.backup",
                    "publish-indeterminate",
                    uncertain.Intended,
                    uncertain.Observed
                ),
            _ => Print(
                "control.backup",
                ControlBackupStatus(result),
                result,
                2
            )
        };
    }

    private static int ControlRestore(CliOptions options) {
        options.EnsureOnly(
            "input", "branch", "confirm-ref", "confirm-instance",
            "confirm-timeline", "confirm-generation", "confirm-state",
            "confirm-active", "backup"
        );
        using SessionJournalEngine engine = OpenBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        RecapGridControlAdminResult result = RecapGridControlMaintenance.Restore(
            engine.ReadView.Path,
            engine.BranchRefId,
            ReadControlHeadConfirmation(options, engine.BranchRefId),
            options.RequireSingle("backup")
        );
        return PrintControlAdmin("control.restore", result);
    }

    private static int ControlReinitialize(CliOptions options) {
        options.EnsureOnly(
            "input", "branch", "confirm-ref", "confirm-instance",
            "confirm-timeline", "confirm-generation", "confirm-state",
            "confirm-active"
        );
        using SessionJournalEngine engine = OpenBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        RecapGridControlAdminResult result =
            RecapGridControlMaintenance.Reinitialize(
                engine.ReadView.Path,
                engine.BranchRefId,
                ReadControlHeadConfirmation(options, engine.BranchRefId)
            );
        return PrintControlAdmin("control.reinitialize", result);
    }

    private static ControlHeadRef ReadControlHeadConfirmation(
        CliOptions options,
        RefId refId
    ) {
        string active = options.RequireSingle("confirm-active");
        GridBuildRecipeDigest? activeDigest = string.Equals(
            active,
            "none",
            StringComparison.Ordinal
        ) ? null : new GridBuildRecipeDigest(active);
        return new ControlHeadRef(
            new ControlInstanceId(options.RequireSingle("confirm-instance")),
            refId,
            new TimelineId(options.RequireSingle("confirm-timeline")),
            RequireNonNegativeLong(options, "confirm-generation"),
            new ControlStateDigest(options.RequireSingle("confirm-state")),
            activeDigest
        );
    }

    private static int PrintControlAdmin(
        string command,
        RecapGridControlAdminResult result
    ) => result switch {
        RecapGridControlAdminResult.Applied applied => Print(
            command, "applied", applied),
        RecapGridControlAdminResult.CommitIndeterminate uncertain => Print(
            command,
            "commit-indeterminate",
            new {
                uncertain.Intended,
                uncertain.Observed,
                nextAction = "inspect"
            },
            2),
        _ => Print(command, ControlAdminStatus(result), result, 2)
    };

    private static int Activate(
        CliOptions options,
        GridBuildRecipeDigest? recipe,
        RecapGridControlActivationPurpose purpose,
        string command
    ) {
        using SessionJournalEngine engine = OpenBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        RecapGridControlAdmission admission = ReadAdmission(options);
        RecapGridControlPermission requiredPermission = purpose
            is RecapGridControlActivationPurpose.Promotion
                ? RecapGridControlPermission.Promote
                : RecapGridControlPermission.Activate;
        if ((admission.Permissions & requiredPermission)
            != requiredPermission) {
            throw new ArgumentException(
                $"The admission does not authorize {purpose}."
            );
        }
        ControlHeadRef expectedControl = ReadControlHeadConfirmation(
            options,
            engine.BranchRefId
        );
        TimelineHeadRef expectedTimeline =
            HistoryTimelineCanonicalCodec.DecodeTimelineHead(
                ReadBoundedFile(
                    options.RequireSingle("confirm-timeline-head"),
                    MaximumInputUtf8Bytes
                )
            );
        if (expectedTimeline.RefId != engine.BranchRefId
            || expectedTimeline.TimelineId != expectedControl.TimelineId) {
            throw new ArgumentException(
                "The Timeline head confirmation differs from the selected Control scope."
            );
        }
        RecapGridControlOpenResult opened = RecapGridControlFactory.Open(
            engine.ReadView.Path,
            engine.BranchRefId,
            admission
        );
        if (opened is not RecapGridControlOpenResult.Opened controlOpened) {
            return PrintControlOpenFailure(command, opened);
        }
        using (controlOpened.Handle) {
            RecapGridControlActivateResult result = controlOpened.Handle
                .Coordinator.CompareExchangeActiveRecipe(
                    expectedControl,
                    expectedTimeline,
                    recipe,
                    purpose
                );
            return PrintControlActivate(command, result);
        }
    }

    private delegate RecapGridControlPutResult ControlMutation<T>(
        RecapGridControlHandle handle,
        ControlHeadRef controlHead,
        TimelineHeadRef timelineHead,
        T value
    );

    private static int WithControlMutation<T>(
        CliOptions options,
        string command,
        ControlMutation<T> mutation,
        T value
    ) {
        using SessionJournalEngine engine = OpenBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        RecapGridControlOpenResult opened = RecapGridControlFactory.Open(
            engine.ReadView.Path,
            engine.BranchRefId,
            ReadAdmission(options)
        );
        if (opened is not RecapGridControlOpenResult.Opened success) {
            return PrintControlOpenFailure(command, opened);
        }
        using (success.Handle) {
            RecapGridControlSnapshotResult snapshot =
                success.Handle.Reader.ReadSnapshot();
            if (snapshot is not RecapGridControlSnapshotResult.Available current) {
                return PrintControlSnapshotFailure(command, snapshot);
            }
            return PrintControlPut(
                command,
                mutation(
                    success.Handle,
                    current.Snapshot.Head,
                    null!,
                    value
                )
            );
        }
    }

    private static object DescribeControlSnapshot(
        RecapGridControlSnapshot snapshot
    ) => new {
        snapshot.Head,
        families = snapshot.Families.Select(static item => item.Digest.Value),
        definitions = snapshot.Definitions.Select(static item => item.Digest.Value),
        recipes = snapshot.Recipes.Select(static item => item.Recipe.Digest.Value)
    };

    private static void WriteExternalCreateNew(string path, byte[] bytes) {
        CliIo.EnsurePathChainHasNoReparsePoint(path, "--output");
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var stream = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None
        );
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static int PrintControlOpenFailure(
        string command,
        RecapGridControlOpenResult result
    ) => Print(command, ControlOpenStatus(result), result, 2);

    private static int PrintControlReaderOpenFailure(
        string command,
        RecapGridControlReaderOpenResult result
    ) => Print(command, ControlReaderOpenStatus(result), result, 2);

    private static int PrintControlSnapshotFailure(
        string command,
        RecapGridControlSnapshotResult result
    ) => Print(command, ControlSnapshotStatus(result), result, 2);

    private static int PrintTimelineReaderOpenFailure(
        string command,
        HistoryTimelineReaderOpenResult result
    ) => Print(command, TimelineReaderOpenStatus(result), result, 2);

    private static int PrintTimelineSnapshotFailure(
        string command,
        HistoryTimelineSnapshotResult result
    ) => Print(command, TimelineSnapshotStatus(result), result, 2);

    private static int PrintTimelineRowFailure(
        string command,
        HistoryTimelineReaderRowResult result
    ) => Print(command, TimelineRowStatus(result), result, 2);

    private static int PrintControlPut(
        string command,
        RecapGridControlPutResult result
    ) => result switch {
        RecapGridControlPutResult.Stored stored => Print(
            command, "stored", stored),
        RecapGridControlPutResult.AlreadyPresent present => Print(
            command, "already-present", present),
        RecapGridControlPutResult.CommitIndeterminate uncertain
            => PrintIndeterminate(
                command,
                "commit-indeterminate",
                uncertain.Intended,
                uncertain.Observed
            ),
        _ => Print(command, ControlPutStatus(result), result, 2)
    };

    private static int PrintControlActivate(
        string command,
        RecapGridControlActivateResult result
    ) => result switch {
        RecapGridControlActivateResult.Applied applied => Print(
            command, "applied", applied),
        RecapGridControlActivateResult.AlreadyActive active => Print(
            command, "already-active", active),
        RecapGridControlActivateResult.CommitIndeterminate uncertain
            => PrintIndeterminate(
                command,
                "commit-indeterminate",
                uncertain.Intended,
                uncertain.Observed
            ),
        _ => Print(command, ControlActivateStatus(result), result, 2)
    };

    private static int PrintControlOperation(
        string command,
        RecapGridControlOperationResult result
    ) => result switch {
        RecapGridControlOperationResult.Applied applied => Print(
            command,
            "applied",
            applied
        ),
        RecapGridControlOperationResult.Replayed replayed => Print(
            command,
            "replayed",
            replayed
        ),
        RecapGridControlOperationResult.CommitIndeterminate uncertain
            => PrintIndeterminate(
                command,
                "commit-indeterminate",
                uncertain.Intended,
                uncertain.Observed
            ),
        _ => Print(command, ControlOperationStatus(result), result, 2)
    };

    private static int PrintIndeterminate(
        string command,
        string status,
        object intended,
        object? observed
    ) => Print(
        command,
        status,
        new { intended, observed, nextAction = "inspect" },
        2
    );

    private static string ControlOpenStatus(RecapGridControlOpenResult result)
        => result switch {
            RecapGridControlOpenResult.Absent => "absent",
            RecapGridControlOpenResult.TimelineAbsent => "timeline-absent",
            RecapGridControlOpenResult.TimelineUnsupportedSchema
                => "timeline-unsupported-schema",
            RecapGridControlOpenResult.Busy => "busy",
            RecapGridControlOpenResult.UnsupportedSchema
                => "control-unsupported-schema",
            RecapGridControlOpenResult.Invalid => "invalid",
            _ => "invalid-outcome"
        };

    private static string ControlSnapshotStatus(
        RecapGridControlSnapshotResult result
    ) => result switch {
        RecapGridControlSnapshotResult.Busy => "busy",
        RecapGridControlSnapshotResult.UnsupportedSchema
            => "control-unsupported-schema",
        RecapGridControlSnapshotResult.Disposed => "disposed",
        RecapGridControlSnapshotResult.Invalid => "invalid",
        _ => "invalid-outcome"
    };

    private static string ControlReaderOpenStatus(
        RecapGridControlReaderOpenResult result
    ) => result switch {
        RecapGridControlReaderOpenResult.Absent => "absent",
        RecapGridControlReaderOpenResult.TimelineAbsent
            => "timeline-absent",
        RecapGridControlReaderOpenResult.TimelineUnsupportedSchema
            => "timeline-unsupported-schema",
        RecapGridControlReaderOpenResult.Busy => "busy",
        RecapGridControlReaderOpenResult.UnsupportedSchema
            => "control-unsupported-schema",
        RecapGridControlReaderOpenResult.Invalid => "invalid",
        _ => "invalid-outcome"
    };

    private static string TimelineReaderOpenStatus(
        HistoryTimelineReaderOpenResult result
    ) => result switch {
        HistoryTimelineReaderOpenResult.Absent => "absent",
        HistoryTimelineReaderOpenResult.Busy => "busy",
        HistoryTimelineReaderOpenResult.UnsupportedSchema
            => "timeline-unsupported-schema",
        HistoryTimelineReaderOpenResult.Invalid => "invalid",
        _ => "invalid-outcome"
    };

    private static string TimelineSnapshotStatus(
        HistoryTimelineSnapshotResult result
    ) => result switch {
        HistoryTimelineSnapshotResult.Busy => "busy",
        HistoryTimelineSnapshotResult.UnsupportedSchema
            => "timeline-unsupported-schema",
        HistoryTimelineSnapshotResult.Invalid => "invalid",
        _ => "invalid-outcome"
    };

    private static string TimelineRowStatus(
        HistoryTimelineReaderRowResult result
    ) => result switch {
        HistoryTimelineReaderRowResult.NotOnSelectedPath
            => "not-on-selected-path",
        HistoryTimelineReaderRowResult.StaleTimelineHead
            => "stale-timeline-head",
        HistoryTimelineReaderRowResult.Busy => "busy",
        HistoryTimelineReaderRowResult.Invalid => "invalid",
        _ => "invalid-outcome"
    };

    private static string ControlPutStatus(RecapGridControlPutResult result)
        => result switch {
            RecapGridControlPutResult.Unauthorized => "unauthorized",
            RecapGridControlPutResult.StaleControlHead
                => "stale-control-head",
            RecapGridControlPutResult.StaleTimelineHead
                => "stale-timeline-head",
            RecapGridControlPutResult.NotOnSelectedPath
                => "not-on-selected-path",
            RecapGridControlPutResult.Busy => "busy",
            RecapGridControlPutResult.TimelineUnsupportedSchema
                => "timeline-unsupported-schema",
            RecapGridControlPutResult.Disposed => "disposed",
            RecapGridControlPutResult.LimitExceeded => "limit-exceeded",
            RecapGridControlPutResult.Invalid => "invalid",
            _ => "invalid-outcome"
        };

    private static string ControlActivateStatus(
        RecapGridControlActivateResult result
    ) => result switch {
        RecapGridControlActivateResult.Unauthorized => "unauthorized",
        RecapGridControlActivateResult.RecipeAbsent => "recipe-absent",
        RecapGridControlActivateResult.StaleControlHead
            => "stale-control-head",
        RecapGridControlActivateResult.StaleTimelineHead
            => "stale-timeline-head",
        RecapGridControlActivateResult.BootstrapNotSelected
            => "bootstrap-not-selected",
        RecapGridControlActivateResult.Busy => "busy",
        RecapGridControlActivateResult.TimelineUnsupportedSchema
            => "timeline-unsupported-schema",
        RecapGridControlActivateResult.Disposed => "disposed",
        RecapGridControlActivateResult.Invalid => "invalid",
        _ => "invalid-outcome"
        };

    private static string ControlOperationStatus(
        RecapGridControlOperationResult result
    ) => result switch {
        RecapGridControlOperationResult.Conflict => "operation-conflict",
        RecapGridControlOperationResult.Unauthorized => "unauthorized",
        RecapGridControlOperationResult.RecipeAbsent => "recipe-absent",
        RecapGridControlOperationResult.StaleControlHead
            => "stale-control-head",
        RecapGridControlOperationResult.StaleTimelineHead
            => "stale-timeline-head",
        RecapGridControlOperationResult.NotOnSelectedPath
            => "not-on-selected-path",
        RecapGridControlOperationResult.Busy => "busy",
        RecapGridControlOperationResult.TimelineUnsupportedSchema
            => "timeline-unsupported-schema",
        RecapGridControlOperationResult.Disposed => "disposed",
        RecapGridControlOperationResult.LimitExceeded => "limit-exceeded",
        RecapGridControlOperationResult.Invalid => "invalid",
        _ => "invalid-outcome"
    };

    private static string ControlBackupStatus(
        RecapGridControlBackupResult result
    ) => result switch {
        RecapGridControlBackupResult.Absent => "absent",
        RecapGridControlBackupResult.TimelineAbsent => "timeline-absent",
        RecapGridControlBackupResult.TimelineUnsupportedSchema
            => "timeline-unsupported-schema",
        RecapGridControlBackupResult.ControlUnsupportedSchema
            => "control-unsupported-schema",
        RecapGridControlBackupResult.StaleControlHead
            => "stale-control-head",
        RecapGridControlBackupResult.Busy => "busy",
        RecapGridControlBackupResult.LimitExceeded => "limit-exceeded",
        RecapGridControlBackupResult.Invalid => "invalid",
        _ => "invalid-outcome"
    };

    private static string ControlAdminStatus(
        RecapGridControlAdminResult result
    ) => result switch {
        RecapGridControlAdminResult.Absent => "absent",
        RecapGridControlAdminResult.TimelineAbsent => "timeline-absent",
        RecapGridControlAdminResult.TimelineUnsupportedSchema
            => "timeline-unsupported-schema",
        RecapGridControlAdminResult.ControlUnsupportedSchema
            => "control-unsupported-schema",
        RecapGridControlAdminResult.StaleControlHead
            => "stale-control-head",
        RecapGridControlAdminResult.Busy => "busy",
        RecapGridControlAdminResult.LimitExceeded => "limit-exceeded",
        RecapGridControlAdminResult.Invalid => "invalid",
        _ => "invalid-outcome"
    };
}
