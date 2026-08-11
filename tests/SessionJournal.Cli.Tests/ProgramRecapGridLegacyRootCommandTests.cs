using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.Cli.LegacyRoot.CrashHarness;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

[Collection(ConsoleSerialCollection.Name)]
public sealed class ProgramRecapGridLegacyRootCommandTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-grid-legacy-root-tests",
        Guid.NewGuid().ToString("N")
    );
    private readonly List<string> _externalPaths = [];

    [Fact]
    public void InspectAndArchiveExactSevenSlotsWithoutChangingSource() {
        CreateSevenSlots();
        var hashed = new List<string>();
        RecapGridCommands.LegacyBeforeFileHashForTest.Value = hashed.Add;
        try {
            RecapGridCommands.LegacyMaximumTotalBytesForTest.Value = 7;
            Assert.Equal(0, RunCaptured(
                "legacy-root", "inspect", "--input", _root
            ).ExitCode);
            Assert.Equal(7, hashed.Count);

            hashed.Clear();
            RecapGridCommands.LegacyMaximumTotalBytesForTest.Value = 6;
            (int capCode, JsonElement capReport) = RunCaptured(
                "legacy-root", "inspect", "--input", _root
            );
            Assert.Equal(2, capCode);
            Assert.Equal("invalid", capReport.GetProperty("status").GetString());
            Assert.Equal("LegacyTotalBytes", capReport.GetProperty("detail")
                .GetProperty("code").GetString());
            Assert.Equal(6, hashed.Count);
        }
        finally {
            RecapGridCommands.LegacyMaximumTotalBytesForTest.Value = null;
            RecapGridCommands.LegacyBeforeFileHashForTest.Value = null;
        }
        IReadOnlyDictionary<string, string> before = SnapshotFiles(_root);

        (int inspectCode, JsonElement inspect) = RunCaptured(
            "legacy-root", "inspect", "--input", _root
        );
        Assert.Equal(0, inspectCode);
        Assert.Equal("available", inspect.GetProperty("status").GetString());
        JsonElement inspectedManifest = inspect.GetProperty("detail");
        Assert.Equal(Path.GetFullPath(_root), inspectedManifest
            .GetProperty("repository").GetString());
        Assert.Equal(SessionJournalDefaults.MainBranchName, inspectedManifest
            .GetProperty("branch").GetString());
        Assert.Equal(16, inspectedManifest.GetProperty("refId")
            .GetString()!.Length);
        Assert.False(string.IsNullOrWhiteSpace(inspectedManifest
            .GetProperty("rawHead").GetString()));
        Assert.Equal(13, inspectedManifest
            .GetProperty("entryCount").GetInt32());
        Assert.Equal(7, inspectedManifest
            .GetProperty("totalBytes").GetInt64());
        Assert.Equal(64, inspectedManifest
            .GetProperty("contentSha256").GetString()!.Length);

        string archive = _root + "-archive";
        _externalPaths.Add(archive);
        (int archiveCode, JsonElement archived) = RunCaptured(
            "legacy-root", "archive",
            "--input", _root,
            "--archive", archive
        );
        Assert.Equal(0, archiveCode);
        Assert.Equal("archived", archived.GetProperty("status").GetString());
        JsonElement detail = archived.GetProperty("detail");
        Assert.Equal(64, detail.GetProperty("manifestSha256")
            .GetString()!.Length);
        Assert.True(File.Exists(Path.Combine(archive, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(
            archive,
            "payload",
            "config",
            "recap-planner-config.json"
        )));
        for (int version = 4; version <= 8; version++) {
            Assert.True(File.Exists(Path.Combine(
                archive,
                "payload",
                "derived",
                "recap",
                $"v{version}",
                "sentinel.bin"
            )));
        }
        Assert.True(File.Exists(Path.Combine(
            archive,
            "payload",
            "derived",
            "recap",
            "rebuild",
            "v1",
            "sentinel.bin"
        )));
        Assert.Equal(before, SnapshotFiles(_root));
        Assert.Equal(1, Run(
            "legacy-root", "archive",
            "--input", _root,
            "--archive", archive
        ));
        Assert.Equal(before, SnapshotFiles(_root));
    }

    [Fact]
    public void V9AndSymlinkAreRejectedWithoutArchiveOrMutation() {
        CreateSevenSlots();
        string v9 = Path.Combine(_root, "derived", "recap", "v9");
        Directory.CreateDirectory(v9);
        File.WriteAllBytes(Path.Combine(v9, "do-not-touch.bin"), [9]);
        IReadOnlyDictionary<string, string> beforeV9 = SnapshotFiles(_root);
        string v9Archive = _root + "-v9-archive";
        _externalPaths.Add(v9Archive);

        (int v9Code, JsonElement v9Result) = RunCaptured(
            "legacy-root", "archive",
            "--input", _root,
            "--archive", v9Archive
        );
        Assert.Equal(2, v9Code);
        Assert.Equal("v9-present", v9Result
            .GetProperty("status").GetString());
        Assert.False(Directory.Exists(v9Archive));
        Assert.Equal(beforeV9, SnapshotFiles(_root));

        Directory.Delete(v9, recursive: true);
        string external = _root + "-external.bin";
        _externalPaths.Add(external);
        File.WriteAllBytes(external, [4, 2]);
        string link = Path.Combine(
            _root,
            "derived",
            "recap",
            "v8",
            "external-link.bin"
        );
        File.CreateSymbolicLink(link, external);
        string symlinkArchive = _root + "-symlink-archive";
        _externalPaths.Add(symlinkArchive);

        (int inspectCode, JsonElement inspected) = RunCaptured(
            "legacy-root", "inspect", "--input", _root
        );
        Assert.Equal(2, inspectCode);
        Assert.Equal("invalid", inspected.GetProperty("status").GetString());
        Assert.Equal("LegacyRootReparsePoint", inspected
            .GetProperty("detail").GetProperty("code").GetString());
        (int archiveCode, JsonElement archived) = RunCaptured(
            "legacy-root", "archive",
            "--input", _root,
            "--archive", symlinkArchive
        );
        Assert.Equal(2, archiveCode);
        Assert.Equal("invalid", archived.GetProperty("status").GetString());
        Assert.False(Directory.Exists(symlinkArchive));
        Assert.Equal(new byte[] { 4, 2 }, File.ReadAllBytes(external));
    }

    [Fact]
    public void WrongConfirmationAndSourceOrArchiveDriftNeverDelete() {
        CreateSevenSlots();
        (string archive, LegacyWitness witness) = CreateArchive();
        IReadOnlyDictionary<string, string> original = SnapshotFiles(_root);

        Assert.Equal(2, RunDelete(
            archive,
            witness with { SourceSha256 = new string('0', 64) }
        ).ExitCode);
        Assert.Equal(original, SnapshotFiles(_root));

        string sourceFile = Path.Combine(
            _root,
            "derived",
            "recap",
            "v8",
            "sentinel.bin"
        );
        File.WriteAllBytes(sourceFile, [8, 8]);
        IReadOnlyDictionary<string, string> drifted = SnapshotFiles(_root);
        Assert.Equal(2, RunDelete(archive, witness).ExitCode);
        Assert.Equal(drifted, SnapshotFiles(_root));

        File.WriteAllBytes(sourceFile, [8]);
        string archivedFile = Path.Combine(
            archive,
            "payload",
            "derived",
            "recap",
            "v7",
            "sentinel.bin"
        );
        File.WriteAllBytes(archivedFile, [7, 7]);
        Assert.Equal(2, RunDelete(archive, witness).ExitCode);
        Assert.Equal(original, SnapshotFiles(_root));
    }

    [Fact]
    public void ArchiveAuthorityRejectsStaleHeadAndForeignBranchWithoutDelete() {
        CreateSevenSlots();
        (string archive, LegacyWitness witness) = CreateArchive();
        IReadOnlyDictionary<string, string> before = SnapshotLegacyFiles(_root);
        string[] archivedAuthority = LegacyAuthorityArguments(_root);

        using (SessionJournalEngine owner = SessionJournalEngine.Open(_root)) {
            owner.AppendObservation("advance after archive");
            owner.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("settled")]),
                new CompletionDescriptor("import", "v1", "model")
            );
        }
        string[] staleDelete = [
            "legacy-root", "delete",
            "--input", _root,
            "--archive", archive,
            .. archivedAuthority,
            "--confirm-source-sha256", witness.SourceSha256,
            "--confirm-entry-count", witness.EntryCount.ToString(),
            "--confirm-total-bytes", witness.TotalBytes.ToString(),
            "--confirm-archive-sha256", witness.ArchiveSha256
        ];
        Assert.Equal("raw-head-mismatch", RunCapturedExact(staleDelete).Json
            .GetProperty("status").GetString());
        Assert.Equal(before, SnapshotLegacyFiles(_root));

        string[] currentAuthority = LegacyAuthorityArguments(_root);
        string[] staleArchiveDelete = [
            "legacy-root", "delete",
            "--input", _root,
            "--archive", archive,
            .. currentAuthority,
            "--confirm-source-sha256", witness.SourceSha256,
            "--confirm-entry-count", witness.EntryCount.ToString(),
            "--confirm-total-bytes", witness.TotalBytes.ToString(),
            "--confirm-archive-sha256", witness.ArchiveSha256
        ];
        Assert.Equal("archive-authority-mismatch",
            RunCapturedExact(staleArchiveDelete).Json
                .GetProperty("status").GetString());
        Assert.Equal(before, SnapshotLegacyFiles(_root));

        Atelia.EventJournal.EventAddress mainHead;
        using (Atelia.EventJournal.EventJournal journal =
               Atelia.EventJournal.EventJournal.OpenExisting(_root)) {
            Atelia.EventJournal.RefId main = journal.OpenBranch(
                SessionJournalDefaults.MainBranchName
            ).Value;
            mainHead = journal.GetHead(main)!.Value;
            _ = journal.CreateBranch("foreign", mainHead).Value;
        }
        string[] foreignAuthority = LegacyAuthorityArguments(
            _root,
            "foreign"
        );
        string[] foreignDelete = [
            "legacy-root", "delete",
            "--input", _root,
            "--archive", archive,
            .. foreignAuthority,
            "--confirm-source-sha256", witness.SourceSha256,
            "--confirm-entry-count", witness.EntryCount.ToString(),
            "--confirm-total-bytes", witness.TotalBytes.ToString(),
            "--confirm-archive-sha256", witness.ArchiveSha256
        ];
        Assert.Equal("archive-authority-mismatch",
            RunCapturedExact(foreignDelete).Json
                .GetProperty("status").GetString());
        Assert.Equal(before, SnapshotLegacyFiles(_root));
    }

    [Fact]
    public void DeleteLeavesUnknownSiblingsAndPartialCanRetry() {
        CreateSevenSlots();
        (string archive, LegacyWitness witness) = CreateArchive();
        string unknownRecap = Path.Combine(
            _root,
            "derived",
            "recap",
            "keep.bin"
        );
        string unknownConfig = Path.Combine(_root, "config", "keep.bin");
        File.WriteAllBytes(unknownRecap, [4, 2]);
        File.WriteAllBytes(unknownConfig, [2, 4]);

        RecapGridCommands.LegacyDeleteAfterFileForTest.Value = count => {
            if (count == 1) {
                throw new IOException("deterministic partial delete");
            }
        };
        (int partialCode, JsonElement partial) partial;
        try {
            partial = RunDelete(archive, witness);
        }
        finally {
            RecapGridCommands.LegacyDeleteAfterFileForTest.Value = null;
        }
        Assert.Equal(2, partial.partialCode);
        Assert.Equal("partial", partial.partial
            .GetProperty("status").GetString());
        JsonElement remaining = partial.partial.GetProperty("detail")
            .GetProperty("remaining");
        var retryWitness = new LegacyWitness(
            remaining.GetProperty("contentSha256").GetString()!,
            remaining.GetProperty("entryCount").GetInt32(),
            remaining.GetProperty("totalBytes").GetInt64(),
            witness.ArchiveSha256
        );

        (int retryCode, JsonElement retry) = RunDelete(
            archive,
            retryWitness
        );
        Assert.Equal(0, retryCode);
        Assert.Equal("deleted", retry.GetProperty("status").GetString());
        Assert.Equal(new byte[] { 4, 2 }, File.ReadAllBytes(unknownRecap));
        Assert.Equal(new byte[] { 2, 4 }, File.ReadAllBytes(unknownConfig));
        for (int version = 4; version <= 8; version++) {
            Assert.False(Directory.Exists(Path.Combine(
                _root,
                "derived",
                "recap",
                $"v{version}"
            )));
        }
        Assert.False(Directory.Exists(Path.Combine(
            _root,
            "derived",
            "recap",
            "rebuild",
            "v1"
        )));
        Assert.False(File.Exists(Path.Combine(
            _root,
            "config",
            "recap-planner-config.json"
        )));
    }

    [Fact]
    public void PostPublishFailureIsIndeterminateAndNeverDeletesReoccupiedTemp() {
        CreateSevenSlots();
        string archive = _root + "-post-publish";
        _externalPaths.Add(archive);
        string? reoccupied = null;
        RecapGridCommands.LegacyArchiveStageForTest.Value =
            (stage, temporary) => {
                if (!string.Equals(
                        stage,
                        "after-rename",
                        StringComparison.Ordinal)) {
                    return;
                }
                reoccupied = temporary;
                Directory.CreateDirectory(temporary);
                File.WriteAllBytes(
                    Path.Combine(temporary, "new-owner.bin"),
                    [4, 2]
                );
                throw new IOException("deterministic parent fsync window");
            };
        (int ExitCode, JsonElement Json) result;
        try {
            result = RunCaptured(
                "legacy-root", "archive",
                "--input", _root,
                "--archive", archive
            );
        }
        finally {
            RecapGridCommands.LegacyArchiveStageForTest.Value = null;
        }
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("publication-indeterminate", result.Json
            .GetProperty("status").GetString());
        JsonElement observed = result.Json.GetProperty("detail")
            .GetProperty("observed");
        Assert.Equal("archived", observed.GetProperty("status").GetString());
        Assert.True(File.Exists(Path.Combine(archive, "manifest.json")));
        Assert.NotNull(reoccupied);
        Assert.Equal(new byte[] { 4, 2 }, File.ReadAllBytes(Path.Combine(
            reoccupied!,
            "new-owner.bin"
        )));
        _externalPaths.Add(reoccupied!);
    }

    [Fact]
    public void PrePublishCleanupLeavesReoccupiedDifferentDirectory() {
        CreateSevenSlots();
        string archive = _root + "-pre-publish";
        _externalPaths.Add(archive);
        string? reoccupied = null;
        RecapGridCommands.LegacyArchiveStageForTest.Value =
            (stage, temporary) => {
                if (!string.Equals(
                        stage,
                        "before-rename",
                        StringComparison.Ordinal)) {
                    return;
                }
                Directory.Delete(temporary, recursive: true);
                Directory.CreateDirectory(temporary);
                File.WriteAllBytes(
                    Path.Combine(temporary, "different-owner.bin"),
                    [9]
                );
                reoccupied = temporary;
                throw new IOException("deterministic prepublish failure");
            };
        try {
            Assert.Equal(1, Run(
                "legacy-root", "archive",
                "--input", _root,
                "--archive", archive
            ));
        }
        finally {
            RecapGridCommands.LegacyArchiveStageForTest.Value = null;
        }
        Assert.False(Directory.Exists(archive));
        Assert.NotNull(reoccupied);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(
            reoccupied!,
            "different-owner.bin"
        )));
        _externalPaths.Add(reoccupied!);
    }

    [Fact]
    public void FifoAndNonCanonicalManifestFailClosed() {
        if (!OperatingSystem.IsLinux()) { return; }
        CreateSevenSlots();
        string fifo = Path.Combine(
            _root,
            "derived",
            "recap",
            "v8",
            "fifo"
        );
        Assert.Equal(0, CreateFifo(fifo, 0x180));
        (int fifoCode, JsonElement fifoResult) = RunCaptured(
            "legacy-root", "inspect", "--input", _root
        );
        Assert.Equal(2, fifoCode);
        Assert.Equal("invalid", fifoResult.GetProperty("status").GetString());
        File.Delete(fifo);

        (string archive, LegacyWitness witness) = CreateArchive();
        string manifest = Path.Combine(archive, "manifest.json");
        File.AppendAllText(manifest, "\n");
        IReadOnlyDictionary<string, string> before = SnapshotFiles(_root);
        (int deleteCode, JsonElement rejected) = RunDelete(archive, witness);
        Assert.Equal(2, deleteCode);
        Assert.Equal("archive-invalid", rejected
            .GetProperty("status").GetString());
        Assert.Equal("LegacyManifestCanonical", rejected
            .GetProperty("detail").GetProperty("code").GetString());
        Assert.Equal(before, SnapshotFiles(_root));
    }

    [Fact]
    public void MutationAuthorityFailuresAreTypedAndLeaveLegacyBytesExact() {
        CreateSevenSlots();
        IReadOnlyDictionary<string, string> legacyBefore =
            SnapshotLegacyFiles(_root);
        string archive = _root + "-authority";
        _externalPaths.Add(archive);
        string[] authority = LegacyAuthorityArguments(_root);

        string[] wrongRef = [
            "legacy-root", "archive", "--input", _root,
            "--archive", archive,
            "--branch", SessionJournalDefaults.MainBranchName,
            "--confirm-ref", new string('0', 32),
            "--confirm-raw-head", authority[^1]
        ];
        Assert.Equal("ref-mismatch", RunCapturedExact(wrongRef).Json
            .GetProperty("status").GetString());

        string[] wrongHead = [
            "legacy-root", "archive", "--input", _root,
            "--archive", archive,
            "--branch", SessionJournalDefaults.MainBranchName,
            "--confirm-ref", authority[^3],
            "--confirm-raw-head", "not-a-canonical-head"
        ];
        Assert.Equal("raw-head-mismatch", RunCapturedExact(wrongHead).Json
            .GetProperty("status").GetString());

        string[] exact = [
            "legacy-root", "archive", "--input", _root,
            "--archive", archive,
            .. authority
        ];
        using (SessionJournalEngine busy = SessionJournalEngine.Open(_root)) {
            Assert.Equal("busy", RunCapturedExact(exact).Json
                .GetProperty("status").GetString());
        }

        RecapGridCommands.LegacyBeforeAuthorityFenceForTest.Value = owner =>
            owner.AppendObservation("deterministic authority drift");
        try {
            Assert.Equal("raw-head-changed", RunCapturedExact(exact).Json
                .GetProperty("status").GetString());
        }
        finally {
            RecapGridCommands.LegacyBeforeAuthorityFenceForTest.Value = null;
        }
        Assert.False(Directory.Exists(archive));
        Assert.Equal(legacyBefore, SnapshotLegacyFiles(_root));

        string[] current = LegacyAuthorityArguments(_root);
        string[] nonIdle = [
            "legacy-root", "archive", "--input", _root,
            "--archive", archive,
            .. current
        ];
        Assert.Equal("not-idle", RunCapturedExact(nonIdle).Json
            .GetProperty("status").GetString());
        Assert.False(Directory.Exists(archive));
        Assert.Equal(legacyBefore, SnapshotLegacyFiles(_root));
    }

    [Fact]
    public void ArchiveCrashWindowsPublishOnlyOldOrExactArchive() {
        foreach (string stage in new[] {
                     "payload-flushed",
                     "manifest-flushed",
                     "before-rename",
                     "after-rename"
                 }) {
            string container = _root + "-crash-" + stage;
            _externalPaths.Add(container);
            string repository = Path.Combine(container, "repository");
            string archive = Path.Combine(container, "archive");
            CreateSevenSlots(repository);
            IReadOnlyDictionary<string, string> expected = SnapshotFiles(
                repository
            );
            IReadOnlyDictionary<string, string> expectedPayload =
                SnapshotLegacyFiles(repository);

            int exitCode = RunCrashHarness([
                $"archive:{stage}",
                "recap-grid", "legacy-root", "archive",
                "--input", repository,
                "--archive", archive,
                .. LegacyAuthorityArguments(repository)
            ]);

            Assert.NotEqual(0, exitCode);
            Assert.Equal(expected, SnapshotFiles(repository));
            if (stage == "after-rename") {
                Assert.True(File.Exists(Path.Combine(
                    archive,
                    "manifest.json"
                )));
                Assert.Equal(expectedPayload, SnapshotFiles(Path.Combine(
                    archive,
                    "payload"
                )));
            }
            else {
                Assert.False(Directory.Exists(archive));
            }
        }
    }

    [Fact]
    public void DeleteCrashLeavesDurablePartialThatCanRetry() {
        CreateSevenSlots();
        (string archive, LegacyWitness witness) = CreateArchive();

        int exitCode = RunCrashHarness([
            "delete:1",
            "recap-grid", "legacy-root", "delete",
            "--input", _root,
            "--archive", archive,
            .. LegacyAuthorityArguments(_root),
            "--confirm-source-sha256", witness.SourceSha256,
            "--confirm-entry-count", witness.EntryCount.ToString(),
            "--confirm-total-bytes", witness.TotalBytes.ToString(),
            "--confirm-archive-sha256", witness.ArchiveSha256
        ]);

        Assert.NotEqual(0, exitCode);
        (int inspectCode, JsonElement inspect) = RunCaptured(
            "legacy-root", "inspect", "--input", _root
        );
        Assert.Equal(0, inspectCode);
        JsonElement remaining = inspect.GetProperty("detail");
        var retryWitness = new LegacyWitness(
            remaining.GetProperty("contentSha256").GetString()!,
            remaining.GetProperty("entryCount").GetInt32(),
            remaining.GetProperty("totalBytes").GetInt64(),
            witness.ArchiveSha256
        );
        Assert.Equal(0, RunDelete(archive, retryWitness).ExitCode);
    }

    private void CreateSevenSlots() => CreateSevenSlots(_root);

    private static void CreateSevenSlots(string root) {
        if (!Directory.Exists(root)) {
            using SessionJournalEngine engine = SessionJournalEngine.Create(
                root,
                new SessionCreateOptions(
                    "legacy-test-model",
                    "legacy-test-surface",
                    "legacy-test-system"
                )
            );
        }
        for (int version = 4; version <= 8; version++) {
            string path = Path.Combine(
                root,
                "derived",
                "recap",
                $"v{version}"
            );
            Directory.CreateDirectory(path);
            File.WriteAllBytes(
                Path.Combine(path, "sentinel.bin"),
                [(byte)version]
            );
        }
        string rebuild = Path.Combine(
            root,
            "derived",
            "recap",
            "rebuild",
            "v1"
        );
        Directory.CreateDirectory(rebuild);
        File.WriteAllBytes(Path.Combine(rebuild, "sentinel.bin"), [1]);
        string config = Path.Combine(root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllBytes(
            Path.Combine(config, "recap-planner-config.json"),
            [3]
        );
    }

    private static IReadOnlyDictionary<string, string> SnapshotFiles(
        string root
    ) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(static path => !File.GetAttributes(path)
            .HasFlag(FileAttributes.ReparsePoint))
        .OrderBy(static path => path, StringComparer.Ordinal)
        .ToDictionary(
            path => Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/'),
            path => Convert.ToHexStringLower(
                SHA256.HashData(File.ReadAllBytes(path))
            ),
            StringComparer.Ordinal
        );

    private static IReadOnlyDictionary<string, string> SnapshotLegacyFiles(
        string root
    ) => SnapshotFiles(root)
        .Where(static item =>
            item.Key == "config/recap-planner-config.json"
            || item.Key.StartsWith("derived/recap/v4/", StringComparison.Ordinal)
            || item.Key.StartsWith("derived/recap/v5/", StringComparison.Ordinal)
            || item.Key.StartsWith("derived/recap/v6/", StringComparison.Ordinal)
            || item.Key.StartsWith("derived/recap/v7/", StringComparison.Ordinal)
            || item.Key.StartsWith("derived/recap/v8/", StringComparison.Ordinal)
            || item.Key.StartsWith(
                "derived/recap/rebuild/v1/",
                StringComparison.Ordinal))
        .ToDictionary(
            static item => item.Key,
            static item => item.Value,
            StringComparer.Ordinal
        );

    private (string Archive, LegacyWitness Witness) CreateArchive() {
        string archive = _root + "-delete-archive";
        _externalPaths.Add(archive);
        (int exitCode, JsonElement report) = RunCaptured(
            "legacy-root", "archive",
            "--input", _root,
            "--archive", archive
        );
        Assert.Equal(0, exitCode);
        JsonElement detail = report.GetProperty("detail");
        JsonElement manifest = detail.GetProperty("manifest");
        return (archive, new LegacyWitness(
            manifest.GetProperty("contentSha256").GetString()!,
            manifest.GetProperty("entryCount").GetInt32(),
            manifest.GetProperty("totalBytes").GetInt64(),
            detail.GetProperty("manifestSha256").GetString()!
        ));
    }

    private (int ExitCode, JsonElement Json) RunDelete(
        string archive,
        LegacyWitness witness
    ) => RunCaptured(
        "legacy-root", "delete",
        "--input", _root,
        "--archive", archive,
        "--confirm-source-sha256", witness.SourceSha256,
        "--confirm-entry-count", witness.EntryCount.ToString(),
        "--confirm-total-bytes", witness.TotalBytes.ToString(),
        "--confirm-archive-sha256", witness.ArchiveSha256
    );

    private static int Run(params string[] args) => Program.MainCore(
        ["recap-grid", .. AddLegacyAuthorityWhenNeeded(args)],
        ThrowingCompletionClientFactory.Instance
    );

    private static (int ExitCode, JsonElement Json) RunCaptured(
        params string[] args
    ) => RunCapturedCore(args, addAuthority: true);

    private static (int ExitCode, JsonElement Json) RunCapturedExact(
        params string[] args
    ) => RunCapturedCore(args, addAuthority: false);

    private static (int ExitCode, JsonElement Json) RunCapturedCore(
        string[] args,
        bool addAuthority
    ) {
        TextWriter original = Console.Out;
        using var output = new StringWriter();
        try {
            string[] normalized = addAuthority
                ? AddLegacyAuthorityWhenNeeded(args)
                : args;
            Console.SetOut(output);
            int exitCode = Program.MainCore(
                ["recap-grid", .. normalized],
                ThrowingCompletionClientFactory.Instance
            );
            string json = output.ToString().Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries
            )[^1];
            using JsonDocument document = JsonDocument.Parse(json);
            return (exitCode, document.RootElement.Clone());
        }
        finally {
            Console.SetOut(original);
        }
    }

    private static int RunCrashHarness(params string[] args) {
        string harness = typeof(CrashHarnessMarker).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(harness);
        foreach (string argument in args) {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Failed to start legacy-root crash harness."
            );
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        Assert.True(
            process.ExitCode != 0,
            $"Crash harness unexpectedly succeeded. stdout={stdout} stderr={stderr}"
        );
        return process.ExitCode;
    }

    private static string[] AddLegacyAuthorityWhenNeeded(string[] args) {
        if (args.Length < 2
            || args[0] != "legacy-root"
            || args[1] is not ("archive" or "delete")
            || args.Contains("--branch", StringComparer.Ordinal)) {
            return args;
        }
        int inputIndex = Array.IndexOf(args, "--input");
        Assert.InRange(inputIndex, 0, args.Length - 2);
        return [.. args, .. LegacyAuthorityArguments(args[inputIndex + 1])];
    }

    private static string[] LegacyAuthorityArguments(
        string repository,
        string branch = SessionJournalDefaults.MainBranchName
    ) {
        using SessionJournalEngine owner = SessionJournalEngine.OpenReadOnly(
            repository,
            branch
        );
        return [
            "--branch", branch,
            "--confirm-ref", owner.BranchRefId.ToHexString(),
            "--confirm-raw-head", EventAddressTextCodec.Format(
                owner.ReadCurrentHead()!.Value
            )
        ];
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
        foreach (string path in _externalPaths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path)) {
                File.Delete(path);
            }
        }
    }

    private sealed class ThrowingCompletionClientFactory
        : ICompletionClientFactory {
        internal static ThrowingCompletionClientFactory Instance { get; } =
            new();

        public ICompletionClient Create(CompletionConnectionConfig connection)
            => throw new InvalidOperationException(
                $"Legacy-root commands must not construct '{connection.Id}'."
            );
    }

    private sealed record LegacyWitness(
        string SourceSha256,
        int EntryCount,
        long TotalBytes,
        string ArchiveSha256
    );

    [System.Runtime.InteropServices.DllImport(
        "libc",
        EntryPoint = "mkfifo",
        SetLastError = true)]
    private static extern int CreateFifo(string path, uint mode);
}
