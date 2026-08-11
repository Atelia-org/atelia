using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static partial class RecapGridCommands {
    private const string LegacyManifestSchema =
        "atelia.session-journal.recap-grid-legacy-root-manifest.v2";
    private const int MaximumLegacyEntryCount = 65_536;
    private const long MaximumLegacyTotalBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumLegacyManifestBytes = 16 * 1024 * 1024;
    private const int OpenReadOnly = 0;
    private const int OpenNonBlocking = 0x800;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const uint LinuxFileTypeMask = 0xF000;
    private const uint LinuxDirectoryType = 0x4000;
    private const uint LinuxRegularFileType = 0x8000;
    private const int MaximumLegacyArchiveInventoryCount =
        checked((MaximumLegacyEntryCount * 2) + 2);

    private static readonly string[] LegacyDirectorySlots = [
        "derived/recap/v4",
        "derived/recap/v5",
        "derived/recap/v6",
        "derived/recap/v7",
        "derived/recap/v8",
        "derived/recap/rebuild/v1"
    ];
    private const string LegacyConfigSlot =
        "config/recap-planner-config.json";
    private const string ForbiddenV9Slot = "derived/recap/v9";
    internal static readonly AsyncLocal<Action<int>?>
        LegacyDeleteAfterFileForTest = new();
    internal static readonly AsyncLocal<Action<string, string>?>
        LegacyArchiveStageForTest = new();
    internal static readonly AsyncLocal<Action<SessionJournalEngine>?>
        LegacyBeforeAuthorityFenceForTest = new();
    internal static readonly AsyncLocal<long?>
        LegacyMaximumTotalBytesForTest = new();
    internal static readonly AsyncLocal<Action<string>?>
        LegacyBeforeFileHashForTest = new();

    private static int LegacyRoot(string[] args) {
        if (args.Length == 0) {
            throw new ArgumentException(
                "recap-grid legacy-root requires a subcommand."
            );
        }
        string action = args[0];
        CliOptions options = CliOptions.Parse(args.Skip(1).ToArray());
        return action switch {
            "inspect" => LegacyInspect(options),
            "archive" => LegacyArchive(options),
            "delete" => LegacyDelete(options),
            _ => throw new ArgumentException(
                $"Unknown recap-grid legacy-root command '{action}'."
            )
        };
    }

    private static int LegacyInspect(CliOptions options) {
        options.EnsureOnly("input", "branch");
        string repository = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(options.RequireSingle("input"))
        );
        string branch = options.GetOptionalSingle("branch")
            ?? SessionJournalDefaults.MainBranchName;
        using SessionJournalEngine owner = SessionJournalEngine.OpenReadOnly(
            repository,
            branch
        );
        EventAddress rawHead = owner.ReadCurrentHead()
            ?? throw new InvalidDataException(
                "The selected SessionJournal branch has no raw head."
            );
        LegacyRootCaptureResult captured = CaptureLegacyRoot(repository);
        return captured switch {
            LegacyRootCaptureResult.Available available => Print(
                "legacy-root.inspect",
                "available",
                new {
                    repository,
                    branch,
                    refId = owner.BranchRefId.ToHexString(),
                    rawHead = EventAddressTextCodec.Format(rawHead),
                    schema = available.Manifest.Schema,
                    entryCount = available.Manifest.EntryCount,
                    totalBytes = available.Manifest.TotalBytes,
                    contentSha256 = available.Manifest.ContentSha256,
                    entries = available.Manifest.Entries.Select(
                        static entry => new {
                            path = entry.Path,
                            type = entry.Type,
                            length = entry.Length,
                            sha256 = entry.Sha256
                        })
                }
            ),
            LegacyRootCaptureResult.ForbiddenV9 present => Print(
                "legacy-root.inspect",
                "v9-present",
                new { path = present.RelativePath },
                2
            ),
            LegacyRootCaptureResult.Invalid invalid => Print(
                "legacy-root.inspect",
                "invalid",
                new { code = invalid.Code, detail = invalid.Detail },
                2
            ),
            _ => Print(
                "legacy-root.inspect",
                "invalid-outcome",
                exitCode: 2
            )
        };
    }

    private static int LegacyArchive(CliOptions options) {
        options.EnsureOnly(
            "input", "archive", "branch", "confirm-ref", "confirm-raw-head"
        );
        string repository = options.RequireSingle("input");
        string branch = options.RequireSingle("branch");
        if (!TryRequireLinuxLegacyCapability(
                "legacy-root.archive",
                out int unsupported)) {
            return unsupported;
        }
        SessionJournalEngine owner;
        try {
            owner = SessionJournalEngine.Open(repository, branch);
        }
        catch (IOException exception) {
            return Print(
                "legacy-root.archive",
                "busy",
                new { code = "SessionJournalOwnerBusy", detail = exception.Message },
                2
            );
        }
        using (owner) {
            if (!TryRequireLegacyAuthority(
                    options,
                    owner,
                    "legacy-root.archive",
                    out EventAddress expectedHead,
                    out int rejection)) {
                return rejection;
            }
            return LegacyArchiveCore(options, owner, branch, expectedHead);
        }
    }

    private static int LegacyArchiveCore(
        CliOptions options,
        SessionJournalEngine owner,
        string branch,
        EventAddress expectedHead
    ) {
        string repository = options.RequireSingle("input");
        string output = options.RequireSingle("archive");
        CliIo.ValidateDirectoryOutputPath(repository, output, "--archive");
        string repositoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repository)
        );
        string outputPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(output)
        );
        string parent = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("--archive has no parent path.");
        RequireDirectoryNoFollow(repositoryPath, "--input");
        RequireDirectoryNoFollow(parent, "--archive parent");
        if (File.Exists(outputPath) || Directory.Exists(outputPath)) {
            throw new ArgumentException(
                "--archive is create-only and must not already exist."
            );
        }
        LegacyRootCaptureResult captured = CaptureLegacyRoot(repositoryPath);
        if (captured is LegacyRootCaptureResult.ForbiddenV9 present) {
            return Print(
                "legacy-root.archive",
                "v9-present",
                new { path = present.RelativePath },
                2
            );
        }
        if (captured is LegacyRootCaptureResult.Invalid invalid) {
            return Print(
                "legacy-root.archive",
                "invalid",
                new { code = invalid.Code, detail = invalid.Detail },
                2
            );
        }
        LegacyRootManifest manifest = ((LegacyRootCaptureResult.Available)
            captured).Manifest.WithAuthority(
                branch,
                owner.BranchRefId.ToHexString(),
                EventAddressTextCodec.Format(expectedHead)
            );
        CliIo.EnsurePathChainHasNoReparsePoint(parent, "--archive");
        string temporary = Path.Combine(
            parent,
            $".{Path.GetFileName(outputPath)}.tmp-{Guid.NewGuid():N}"
        );
        LegacyFileIdentity? temporaryIdentity = null;
        int temporaryOwnerDescriptor = -1;
        bool published = false;
        try {
            Directory.CreateDirectory(temporary);
            (temporaryOwnerDescriptor, temporaryIdentity) =
                OpenDirectoryIdentityNoFollow(
                temporary,
                "LegacyTemporaryShape"
            );
            string payload = Path.Combine(temporary, "payload");
            Directory.CreateDirectory(payload);
            CopyLegacyEntries(repositoryPath, payload, manifest.Entries);
            FlushDirectoryTreePostOrder(payload);
            LegacyArchiveStageForTest.Value?.Invoke(
                "payload-flushed",
                temporary
            );
            byte[] manifestBytes = EncodeLegacyManifest(manifest);
            if (manifestBytes.Length > MaximumLegacyManifestBytes) {
                throw new LegacyRootException(
                    "LegacyManifestBytes",
                    "The legacy manifest exceeds its code-owned bound."
                );
            }
            string manifestPath = Path.Combine(
                temporary,
                "manifest.json"
            );
            using (var stream = new FileStream(
                       manifestPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None)) {
                stream.Write(manifestBytes);
                stream.Flush(flushToDisk: true);
            }
            LegacyArchiveStageForTest.Value?.Invoke(
                "manifest-flushed",
                temporary
            );
            LegacyRootCaptureResult sourceAfter = CaptureLegacyRoot(
                repositoryPath
            );
            if (sourceAfter is not LegacyRootCaptureResult.Available after
                || !manifest.EqualsContentExact(after.Manifest)) {
                return Print(
                    "legacy-root.archive",
                    "source-changed",
                    new { expected = manifest.ContentSha256 },
                    2
                );
            }
            LegacyRootCaptureResult archived = CaptureLegacyRoot(payload);
            if (archived is not LegacyRootCaptureResult.Available copied
                || !manifest.EqualsContentExact(copied.Manifest)) {
                throw new LegacyRootException(
                    "LegacyArchiveVerification",
                    "The copied archive does not match the source manifest."
                );
            }
            RequireExactArchiveInventory(temporary, manifest);
            byte[] writtenManifest = File.ReadAllBytes(manifestPath);
            if (!writtenManifest.AsSpan().SequenceEqual(manifestBytes)) {
                throw new LegacyRootException(
                    "LegacyArchiveManifestVerification",
                    "The archive manifest changed before publication."
                );
            }
            FlushDirectory(temporary);
            LegacyArchiveStageForTest.Value?.Invoke(
                "before-rename",
                temporary
            );
            LegacyBeforeAuthorityFenceForTest.Value?.Invoke(owner);
            if (!IsLegacyAuthorityCurrent(owner, expectedHead)) {
                return Print(
                    "legacy-root.archive",
                    "raw-head-changed",
                    new {
                        expected = EventAddressTextCodec.Format(expectedHead),
                        observed = owner.ReadCurrentHead() is { } current
                            ? EventAddressTextCodec.Format(current)
                            : null
                    },
                    2
                );
            }
            Directory.Move(temporary, outputPath);
            published = true;
            LegacyArchiveStageForTest.Value?.Invoke(
                "after-rename",
                temporary
            );
            FlushDirectory(parent);
            return Print(
                "legacy-root.archive",
                "archived",
                new {
                    archive = outputPath,
                    manifest = DescribeLegacyManifest(manifest),
                    manifestSha256 = Sha256(manifestBytes)
                }
            );
        }
        catch (LegacyRootException exception) {
            return Print(
                "legacy-root.archive",
                "invalid",
                new {
                    code = exception.Code,
                    detail = exception.Message
                },
                2
            );
        }
        catch (Exception exception) when (published
            && exception is IOException or UnauthorizedAccessException) {
            return PrintLegacyArchivePublicationIndeterminate(
                outputPath,
                manifest,
                exception
            );
        }
        finally {
            if (!published && temporaryIdentity is { } identity) {
                TryDeleteTemporaryArchive(
                    temporary,
                    identity,
                    temporaryOwnerDescriptor
                );
            }
            if (temporaryOwnerDescriptor >= 0) {
                _ = Close(temporaryOwnerDescriptor);
            }
        }
    }

    private static int LegacyDelete(CliOptions options) {
        options.EnsureOnly(
            "input", "archive", "branch", "confirm-ref", "confirm-raw-head",
            "confirm-source-sha256", "confirm-entry-count",
            "confirm-total-bytes", "confirm-archive-sha256"
        );
        string repository = options.RequireSingle("input");
        string branch = options.RequireSingle("branch");
        if (!TryRequireLinuxLegacyCapability(
                "legacy-root.delete",
                out int unsupported)) {
            return unsupported;
        }
        SessionJournalEngine owner;
        try {
            owner = SessionJournalEngine.Open(repository, branch);
        }
        catch (IOException exception) {
            return Print(
                "legacy-root.delete",
                "busy",
                new { code = "SessionJournalOwnerBusy", detail = exception.Message },
                2
            );
        }
        using (owner) {
            if (!TryRequireLegacyAuthority(
                    options,
                    owner,
                    "legacy-root.delete",
                    out EventAddress expectedHead,
                    out int rejection)) {
                return rejection;
            }
            return LegacyDeleteCore(options, owner, branch, expectedHead);
        }
    }

    private static int LegacyDeleteCore(
        CliOptions options,
        SessionJournalEngine owner,
        string branch,
        EventAddress expectedHead
    ) {
        options.EnsureOnly(
            "input",
            "archive",
            "branch",
            "confirm-ref",
            "confirm-raw-head",
            "confirm-source-sha256",
            "confirm-entry-count",
            "confirm-total-bytes",
            "confirm-archive-sha256"
        );
        string repository = options.RequireSingle("input");
        string archive = options.RequireSingle("archive");
        CliIo.ValidateDirectoryOutputPath(
            repository,
            archive,
            "--archive"
        );
        int confirmedEntryCount = RequireNonNegativeInt(
            options,
            "confirm-entry-count"
        );
        long confirmedTotalBytes = RequireNonNegativeLong(
            options,
            "confirm-total-bytes"
        );
        string confirmedSource = RequireSha256(
            options,
            "confirm-source-sha256"
        );
        string confirmedArchive = RequireSha256(
            options,
            "confirm-archive-sha256"
        );
        LegacyArchiveVerificationResult archiveResult = VerifyLegacyArchive(
            archive
        );
        if (archiveResult is not LegacyArchiveVerificationResult.Verified
                verified) {
            return PrintLegacyArchiveVerificationFailure(
                "legacy-root.delete",
                archiveResult
            );
        }
        if (!string.Equals(
                verified.Manifest.Branch,
                branch,
                StringComparison.Ordinal)
            || !string.Equals(
                verified.Manifest.RefId,
                owner.BranchRefId.ToHexString(),
                StringComparison.Ordinal)
            || !string.Equals(
                verified.Manifest.RawHead,
                EventAddressTextCodec.Format(expectedHead),
                StringComparison.Ordinal)) {
            return Print(
                "legacy-root.delete",
                "archive-authority-mismatch",
                new { code = "LegacyArchiveAuthority" },
                2
            );
        }
        LegacyRootCaptureResult sourceResult = CaptureLegacyRoot(repository);
        if (sourceResult is LegacyRootCaptureResult.ForbiddenV9 v9) {
            return Print(
                "legacy-root.delete",
                "v9-present",
                new { path = v9.RelativePath },
                2
            );
        }
        if (sourceResult is not LegacyRootCaptureResult.Available source) {
            LegacyRootCaptureResult.Invalid invalid =
                (LegacyRootCaptureResult.Invalid)sourceResult;
            return Print(
                "legacy-root.delete",
                "invalid",
                new { code = invalid.Code, detail = invalid.Detail },
                2
            );
        }
        if (!source.Manifest.IsExactSubsetOf(verified.Manifest)) {
            return Print(
                "legacy-root.delete",
                "archive-source-mismatch",
                new {
                    source = DescribeLegacyManifest(source.Manifest),
                    archiveManifestSha256 = verified.ManifestSha256
                },
                2
            );
        }
        if (source.Manifest.EntryCount != confirmedEntryCount
            || source.Manifest.TotalBytes != confirmedTotalBytes
            || !string.Equals(
                source.Manifest.ContentSha256,
                confirmedSource,
                StringComparison.Ordinal)
            || !string.Equals(
                verified.ManifestSha256,
                confirmedArchive,
                StringComparison.Ordinal)) {
            return Print(
                "legacy-root.delete",
                "confirmation-mismatch",
                new {
                    source = DescribeLegacyManifest(source.Manifest),
                    archiveManifestSha256 = verified.ManifestSha256
                },
                2
            );
        }
        LegacyArchiveVerificationResult archiveFresh = VerifyLegacyArchive(
            archive
        );
        LegacyRootCaptureResult sourceFresh = CaptureLegacyRoot(repository);
        if (archiveFresh is not LegacyArchiveVerificationResult.Verified
                freshArchive
            || sourceFresh is not LegacyRootCaptureResult.Available
                freshSource
            || !source.Manifest.EqualsExact(freshSource.Manifest)
            || !verified.Manifest.EqualsExact(freshArchive.Manifest)
            || !string.Equals(
                verified.ManifestSha256,
                freshArchive.ManifestSha256,
                StringComparison.Ordinal)) {
            return Print(
                "legacy-root.delete",
                "changed-before-delete",
                new {
                    source = DescribeLegacyManifest(source.Manifest),
                    archiveManifestSha256 = verified.ManifestSha256
                },
                2
            );
        }
        LegacyBeforeAuthorityFenceForTest.Value?.Invoke(owner);
        if (!IsLegacyAuthorityCurrent(owner, expectedHead)) {
            return Print(
                "legacy-root.delete",
                "raw-head-changed",
                new {
                    expected = EventAddressTextCodec.Format(expectedHead),
                    observed = owner.ReadCurrentHead() is { } current
                        ? EventAddressTextCodec.Format(current)
                        : null
                },
                2
            );
        }
        return DeleteLegacyEntries(
            Path.GetFullPath(repository),
            freshSource.Manifest,
            freshArchive.ManifestSha256
        );
    }

    private static bool TryRequireLegacyAuthority(
        CliOptions options,
        SessionJournalEngine owner,
        string command,
        out EventAddress expectedHead,
        out int rejection
    ) {
        expectedHead = default;
        string confirmedRef = options.RequireSingle("confirm-ref");
        if (!string.Equals(
                confirmedRef,
                owner.BranchRefId.ToHexString(),
                StringComparison.Ordinal)) {
            rejection = Print(
                command,
                "ref-mismatch",
                new {
                    expected = owner.BranchRefId.ToHexString(),
                    observed = confirmedRef
                },
                2
            );
            return false;
        }
        SessionExecutionBoundaryInspection boundary =
            owner.InspectExecutionBoundary();
        if (boundary.Phase != SessionExecutionPhase.Idle) {
            rejection = Print(
                command,
                "not-idle",
                new { phase = boundary.Phase.ToString() },
                2
            );
            return false;
        }
        EventAddress? current = owner.ReadCurrentHead();
        string confirmedHead = options.RequireSingle("confirm-raw-head");
        if (current is null || !string.Equals(
                confirmedHead,
                EventAddressTextCodec.Format(current.Value),
                StringComparison.Ordinal)) {
            rejection = Print(
                command,
                "raw-head-mismatch",
                new {
                    expected = current is { } head
                        ? EventAddressTextCodec.Format(head)
                        : null,
                    observed = confirmedHead
                },
                2
            );
            return false;
        }
        expectedHead = current.Value;
        rejection = 0;
        return true;
    }

    private static bool IsLegacyAuthorityCurrent(
        SessionJournalEngine owner,
        EventAddress expectedHead
    ) => owner.InspectExecutionBoundary().Phase
            == SessionExecutionPhase.Idle
        && owner.ReadCurrentHead() == expectedHead;

    private static LegacyRootCaptureResult CaptureLegacyRoot(
        string repository
    ) {
        try {
            string root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(repository)
            );
            CliIo.EnsurePathChainHasNoReparsePoint(root, "--input");
            if (!Directory.Exists(root)) {
                return new LegacyRootCaptureResult.Invalid(
                    "LegacyRepositoryAbsent",
                    "The input repository does not exist."
                );
            }
            string v9 = Path.Combine(
                root,
                ForbiddenV9Slot.Replace('/', Path.DirectorySeparatorChar)
            );
            if (File.Exists(v9) || Directory.Exists(v9)
                || IsReparsePoint(v9)) {
                return new LegacyRootCaptureResult.ForbiddenV9(
                    ForbiddenV9Slot
                );
            }
            var entries = new List<LegacyRootEntry>();
            long totalBytes = 0;
            foreach (string slot in LegacyDirectorySlots) {
                string path = Path.Combine(
                    root,
                    slot.Replace('/', Path.DirectorySeparatorChar)
                );
                if (Directory.Exists(path) || File.Exists(path)
                    || IsReparsePoint(path)) {
                    CaptureEntryTree(
                        root,
                        path,
                        slot,
                        entries,
                        ref totalBytes
                    );
                }
            }
            string config = Path.Combine(
                root,
                LegacyConfigSlot.Replace('/', Path.DirectorySeparatorChar)
            );
            if (File.Exists(config) || Directory.Exists(config)
                || IsReparsePoint(config)) {
                CaptureExactFile(
                    root,
                    config,
                    LegacyConfigSlot,
                    entries,
                    ref totalBytes
                );
            }
            LegacyRootEntry[] ordered = [.. entries.OrderBy(
                static item => item.Path,
                StringComparer.Ordinal
            )];
            if (ordered.Length > MaximumLegacyEntryCount) {
                throw new LegacyRootException(
                    "LegacyEntryCount",
                    "The legacy root exceeds its entry-count bound."
                );
            }
            string contentSha256 = Sha256(EncodeLegacyManifestBody(
                ordered,
                totalBytes
            ));
            return new LegacyRootCaptureResult.Available(
                new LegacyRootManifest(
                    LegacyManifestSchema,
                    ordered.Length,
                    totalBytes,
                    contentSha256,
                    ordered
                )
            );
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or LegacyRootException
            or OverflowException
            or ArgumentException) {
            string code = exception is LegacyRootException legacy
                ? legacy.Code
                : "LegacyRootInvalid";
            return new LegacyRootCaptureResult.Invalid(
                code,
                exception.Message
            );
        }
    }

    private static void CaptureEntryTree(
        string root,
        string path,
        string relative,
        List<LegacyRootEntry> entries,
        ref long totalBytes
    ) {
        LegacyFileIdentity identity = ReadIdentityNoFollow(
            path,
            "LegacyRootReparsePoint"
        );
        if (identity.FileType != LinuxDirectoryType) {
            throw new LegacyRootException(
                "LegacySlotShape",
                $"Legacy directory slot is not a directory: {relative}"
            );
        }
        entries.Add(new LegacyRootEntry(relative, "directory", 0, null));
        foreach (string child in Directory.EnumerateFileSystemEntries(path)) {
            string childRelative = Path.GetRelativePath(root, child)
                .Replace(Path.DirectorySeparatorChar, '/');
            LegacyFileIdentity childIdentity = ReadIdentityNoFollow(
                child,
                "LegacyRootReparsePoint"
            );
            if (childIdentity.FileType == LinuxDirectoryType) {
                CaptureEntryTree(
                    root,
                    child,
                    childRelative,
                    entries,
                    ref totalBytes
                );
            }
            else {
                CaptureExactFile(
                    root,
                    child,
                    childRelative,
                    entries,
                    ref totalBytes
                );
            }
            if (entries.Count > MaximumLegacyEntryCount) {
                throw new LegacyRootException(
                    "LegacyEntryCount",
                    "The legacy root exceeds its entry-count bound."
                );
            }
        }
    }

    private static void CaptureExactFile(
        string root,
        string path,
        string relative,
        List<LegacyRootEntry> entries,
        ref long totalBytes
    ) {
        _ = root;
        using FileStream stream = OpenRegularFileNoFollow(path, relative);
        long length = stream.Length;
        long maximum = LegacyMaximumTotalBytesForTest.Value
            ?? MaximumLegacyTotalBytes;
        long nextTotal = checked(totalBytes + length);
        if (nextTotal > maximum) {
            throw new LegacyRootException(
                "LegacyTotalBytes",
                "The legacy root exceeds its total-byte bound."
            );
        }
        LegacyBeforeFileHashForTest.Value?.Invoke(relative);
        string digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (stream.Length != length) {
            throw new LegacyRootException(
                "LegacyRootChanged",
                $"Legacy file changed while inspected: {relative}"
            );
        }
        totalBytes = nextTotal;
        entries.Add(new LegacyRootEntry(
            relative,
            "file",
            length,
            digest
        ));
    }

    private static void CopyLegacyEntries(
        string sourceRoot,
        string targetRoot,
        IReadOnlyList<LegacyRootEntry> entries
    ) {
        foreach (LegacyRootEntry entry in entries) {
            string relative = entry.Path.Replace(
                '/',
                Path.DirectorySeparatorChar
            );
            string target = Path.Combine(targetRoot, relative);
            if (entry.Type == "directory") {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string source = Path.Combine(sourceRoot, relative);
            using FileStream input = OpenRegularFileNoFollow(
                source,
                entry.Path
            );
            using var output = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.SequentialScan
            );
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
            if (input.Length != entry.Length) {
                throw new LegacyRootException(
                    "LegacyRootChanged",
                    $"Legacy file changed while copied: {entry.Path}"
                );
            }
            input.Position = 0;
            string digest = Convert.ToHexStringLower(
                SHA256.HashData(input)
            );
            if (!string.Equals(
                    digest,
                    entry.Sha256,
                    StringComparison.Ordinal)) {
                throw new LegacyRootException(
                    "LegacyRootChanged",
                    $"Legacy file changed while copied: {entry.Path}"
                );
            }
        }
    }

    private static int DeleteLegacyEntries(
        string repository,
        LegacyRootManifest expected,
        string archiveManifestSha256
    ) {
        int deletedFiles = 0;
        try {
            foreach (LegacyRootEntry entry in expected.Entries
                         .Where(static item => item.Type == "file")) {
                string path = Path.Combine(
                    repository,
                    entry.Path.Replace('/', Path.DirectorySeparatorChar)
                );
                var one = new List<LegacyRootEntry>();
                long capturedBytes = 0;
                CaptureExactFile(
                    repository,
                    path,
                    entry.Path,
                    one,
                    ref capturedBytes
                );
                if (!entry.Equals(one[0])) {
                    throw new LegacyRootException(
                        "LegacyRootChanged",
                        $"Legacy file changed before delete: {entry.Path}"
                    );
                }
                File.Delete(path);
                FlushDirectory(Path.GetDirectoryName(path)!);
                deletedFiles++;
                LegacyDeleteAfterFileForTest.Value?.Invoke(deletedFiles);
            }
            foreach (LegacyRootEntry entry in expected.Entries
                         .Where(static item => item.Type == "directory")
                         .OrderByDescending(
                             static item => item.Path.Count(
                                 static character => character == '/'
                             ))
                         .ThenByDescending(
                             static item => item.Path,
                             StringComparer.Ordinal
                         )) {
                string path = Path.Combine(
                    repository,
                    entry.Path.Replace('/', Path.DirectorySeparatorChar)
                );
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: false);
                    FlushDirectory(Path.GetDirectoryName(path)!);
                }
            }
            LegacyRootCaptureResult after = CaptureLegacyRoot(repository);
            if (after is not LegacyRootCaptureResult.Available remaining
                || remaining.Manifest.EntryCount != 0) {
                throw new LegacyRootException(
                    "LegacyDeleteIncomplete",
                    "Legacy slots remain after explicit deletion."
                );
            }
            return Print(
                "legacy-root.delete",
                "deleted",
                new {
                    deletedFiles,
                    archiveManifestSha256,
                    remaining = DescribeLegacyManifest(remaining.Manifest)
                }
            );
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or LegacyRootException) {
            LegacyRootCaptureResult remainingResult = CaptureLegacyRoot(
                repository
            );
            object? remaining = remainingResult is
                LegacyRootCaptureResult.Available available
                    ? DescribeLegacyManifest(available.Manifest)
                    : null;
            return Print(
                "legacy-root.delete",
                "partial",
                new {
                    deletedFiles,
                    archiveManifestSha256,
                    code = exception is LegacyRootException legacy
                        ? legacy.Code
                        : "LegacyDeleteIo",
                    detail = exception.Message,
                    remaining
                },
                2
            );
        }
    }

    private static LegacyArchiveVerificationResult VerifyLegacyArchive(
        string archive
    ) {
        try {
            string root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(archive)
            );
            CliIo.EnsurePathChainHasNoReparsePoint(root, "--archive");
            if (!Directory.Exists(root)) {
                return new LegacyArchiveVerificationResult.Invalid(
                    "LegacyArchiveAbsent",
                    "The confirmed archive directory does not exist."
                );
            }
            string manifestPath = Path.Combine(root, "manifest.json");
            byte[] bytes = ReadLegacyManifestBytes(manifestPath);
            LegacyRootManifest manifest = DecodeLegacyManifest(bytes);
            if (!bytes.AsSpan().SequenceEqual(
                    EncodeLegacyManifest(manifest))) {
                throw new LegacyRootException(
                    "LegacyManifestCanonical",
                    "The archive manifest is not exact canonical V2 bytes."
                );
            }
            RequireExactArchiveInventory(root, manifest);
            LegacyRootCaptureResult payload = CaptureLegacyRoot(
                Path.Combine(root, "payload")
            );
            if (payload is not LegacyRootCaptureResult.Available captured
                || !manifest.EqualsContentExact(captured.Manifest)) {
                throw new LegacyRootException(
                    "LegacyArchivePayloadMismatch",
                    "The archive payload differs from its manifest."
                );
            }
            return new LegacyArchiveVerificationResult.Verified(
                manifest,
                Sha256(bytes)
            );
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException
            or LegacyRootException
            or ArgumentException
            or OverflowException) {
            return new LegacyArchiveVerificationResult.Invalid(
                exception is LegacyRootException legacy
                    ? legacy.Code
                    : "LegacyArchiveInvalid",
                exception.Message
            );
        }
    }

    private static int PrintLegacyArchiveVerificationFailure(
        string command,
        LegacyArchiveVerificationResult result
    ) => result switch {
        LegacyArchiveVerificationResult.Invalid invalid => Print(
            command,
            "archive-invalid",
            new { code = invalid.Code, detail = invalid.Detail },
            2
        ),
        _ => Print(command, "invalid-outcome", exitCode: 2)
    };

    private static int PrintLegacyArchivePublicationIndeterminate(
        string archive,
        LegacyRootManifest intended,
        Exception exception
    ) {
        LegacyArchiveVerificationResult observed = VerifyLegacyArchive(
            archive
        );
        object observedDetail = observed switch {
            LegacyArchiveVerificationResult.Verified verified => new {
                status = "archived",
                manifest = DescribeLegacyManifest(verified.Manifest),
                manifestSha256 = verified.ManifestSha256
            },
            LegacyArchiveVerificationResult.Invalid invalid => new {
                status = "invalid",
                code = invalid.Code,
                detail = invalid.Detail
            },
            _ => new { status = "unknown" }
        };
        return Print(
            "legacy-root.archive",
            "publication-indeterminate",
            new {
                intended = DescribeLegacyManifest(intended),
                observed = observedDetail,
                code = "LegacyArchivePublicationIndeterminate",
                detail = exception.Message,
                nextAction = "inspect"
            },
            2
        );
    }

    private static byte[] ReadLegacyManifestBytes(string path) {
        using FileStream stream = OpenRegularFileNoFollow(
            path,
            "manifest.json",
            64 * 1024
        );
        if (stream.Length is < 1 or > MaximumLegacyManifestBytes) {
            throw new LegacyRootException(
                "LegacyManifestBytes",
                "The archive manifest has an invalid byte length."
            );
        }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static LegacyRootManifest DecodeLegacyManifest(
        ReadOnlySpan<byte> bytes
    ) {
        using JsonDocument document = JsonDocument.Parse(
            bytes.ToArray(),
            new JsonDocumentOptions {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            }
        );
        JsonElement root = document.RootElement;
        RequireLegacyProperties(
            root,
            "schema",
            "branch",
            "refId",
            "rawHead",
            "entryCount",
            "totalBytes",
            "contentSha256",
            "entries"
        );
        if (!string.Equals(
                root.GetProperty("schema").GetString(),
                LegacyManifestSchema,
                StringComparison.Ordinal)) {
            throw new LegacyRootException(
                "LegacyManifestSchema",
                "The archive manifest schema is unsupported."
            );
        }
        string branch = root.GetProperty("branch").GetString()
            ?? throw new LegacyRootException(
                "LegacyManifestAuthority",
                "The archive branch authority is null."
            );
        string refId = root.GetProperty("refId").GetString()
            ?? throw new LegacyRootException(
                "LegacyManifestAuthority",
                "The archive RefId authority is null."
            );
        string rawHead = root.GetProperty("rawHead").GetString()
            ?? throw new LegacyRootException(
                "LegacyManifestAuthority",
                "The archive raw-head authority is null."
            );
        if (string.IsNullOrWhiteSpace(branch)
            || Encoding.UTF8.GetByteCount(branch) > 512
            || !IsCanonicalLegacyRefId(refId)
            || !EventAddressTextCodec.TryParse(rawHead, out EventAddress parsed)
            || !string.Equals(
                rawHead,
                EventAddressTextCodec.Format(parsed),
                StringComparison.Ordinal)) {
            throw new LegacyRootException(
                "LegacyManifestAuthority",
                "The archive branch, RefId, or raw head is not canonical."
            );
        }
        int entryCount = root.GetProperty("entryCount").GetInt32();
        long totalBytes = root.GetProperty("totalBytes").GetInt64();
        string contentSha256 = RequireSha256Text(
            root.GetProperty("contentSha256").GetString(),
            "contentSha256"
        );
        if (entryCount is < 0 or > MaximumLegacyEntryCount
            || totalBytes is < 0 or > MaximumLegacyTotalBytes) {
            throw new LegacyRootException(
                "LegacyManifestBounds",
                "The archive manifest exceeds code-owned bounds."
            );
        }
        JsonElement array = root.GetProperty("entries");
        if (array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() != entryCount) {
            throw new LegacyRootException(
                "LegacyManifestEntries",
                "The archive manifest entry count is inconsistent."
            );
        }
        var entries = new List<LegacyRootEntry>(entryCount);
        string? previous = null;
        foreach (JsonElement item in array.EnumerateArray()) {
            RequireLegacyProperties(item, "path", "type", "length", "sha256");
            string path = item.GetProperty("path").GetString()
                ?? throw new LegacyRootException(
                    "LegacyManifestPath",
                    "A manifest path is null."
                );
            string type = item.GetProperty("type").GetString()
                ?? throw new LegacyRootException(
                    "LegacyManifestType",
                    "A manifest type is null."
                );
            long length = item.GetProperty("length").GetInt64();
            JsonElement digestElement = item.GetProperty("sha256");
            string? digest = digestElement.ValueKind == JsonValueKind.Null
                ? null
                : RequireSha256Text(
                    digestElement.GetString(),
                    "entry.sha256"
                );
            RequireLegacyManifestEntry(path, type, length, digest);
            if (previous is not null
                && StringComparer.Ordinal.Compare(previous, path) >= 0) {
                throw new LegacyRootException(
                    "LegacyManifestOrder",
                    "Manifest entries are not strict ordinal unique."
                );
            }
            previous = path;
            entries.Add(new LegacyRootEntry(path, type, length, digest));
        }
        long calculatedBytes = checked(entries.Sum(
            static entry => entry.Length
        ));
        string calculatedDigest = Sha256(EncodeLegacyManifestBody(
            entries,
            calculatedBytes
        ));
        var manifest = new LegacyRootManifest(
            LegacyManifestSchema,
            entries.Count,
            calculatedBytes,
            calculatedDigest,
            entries,
            branch,
            refId,
            rawHead
        );
        if (calculatedBytes != totalBytes
            || !string.Equals(
                calculatedDigest,
                contentSha256,
                StringComparison.Ordinal)
            || !EncodeLegacyManifest(manifest).AsSpan()
                .SequenceEqual(bytes)) {
            throw new LegacyRootException(
                "LegacyManifestCanonical",
                "The archive manifest is not exact canonical V2 bytes."
            );
        }
        return manifest;
    }

    private static bool IsCanonicalLegacyRefId(string text) {
        var parsed = RefId.ParseHex(text);
        return parsed.TryUnwrap(out RefId value, out _)
            && !value.IsDefault
            && string.Equals(
                text,
                value.ToHexString(),
                StringComparison.Ordinal
            );
    }

    private static void RequireLegacyManifestEntry(
        string path,
        string type,
        long length,
        string? digest
    ) {
        if (string.IsNullOrEmpty(path)
            || path.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(path)
            || path.Split('/').Any(static segment =>
                segment is "" or "." or "..")) {
            throw new LegacyRootException(
                "LegacyManifestPath",
                "A manifest path is not canonical relative text."
            );
        }
        bool allowed = string.Equals(
                path,
                LegacyConfigSlot,
                StringComparison.Ordinal)
            || LegacyDirectorySlots.Any(slot =>
                string.Equals(path, slot, StringComparison.Ordinal)
                || path.StartsWith(
                    slot + "/",
                    StringComparison.Ordinal
                ));
        if (!allowed || string.Equals(
                path,
                ForbiddenV9Slot,
                StringComparison.Ordinal)) {
            throw new LegacyRootException(
                "LegacyManifestPath",
                "A manifest path is outside the strict legacy allowlist."
            );
        }
        bool directory = string.Equals(type, "directory", StringComparison.Ordinal);
        bool file = string.Equals(type, "file", StringComparison.Ordinal);
        if ((!directory && !file)
            || length < 0
            || (directory && (length != 0 || digest is not null))
            || (file && digest is null)
            || (string.Equals(path, LegacyConfigSlot, StringComparison.Ordinal)
                && !file)) {
            throw new LegacyRootException(
                "LegacyManifestEntry",
                "A manifest entry has an invalid type, length, or digest."
            );
        }
    }

    private static void RequireLegacyProperties(
        JsonElement value,
        params string[] names
    ) {
        if (value.ValueKind != JsonValueKind.Object
            || !value.EnumerateObject().Select(static property => property.Name)
                .SequenceEqual(names, StringComparer.Ordinal)) {
            throw new LegacyRootException(
                "LegacyManifestShape",
                "The archive manifest has missing, duplicate, reordered, or unknown fields."
            );
        }
    }

    private static void RequireExactArchiveInventory(
        string archive,
        LegacyRootManifest manifest
    ) {
        var expected = new HashSet<string>(StringComparer.Ordinal) {
            "manifest.json",
            "payload"
        };
        foreach (LegacyRootEntry entry in manifest.Entries) {
            string value = "payload/" + entry.Path;
            expected.Add(value);
            string? parent = Path.GetDirectoryName(value.Replace(
                '/',
                Path.DirectorySeparatorChar
            ));
            while (!string.IsNullOrEmpty(parent)) {
                expected.Add(parent.Replace(Path.DirectorySeparatorChar, '/'));
                parent = Path.GetDirectoryName(parent);
            }
        }
        var actual = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(archive);
        int inspected = 0;
        while (pending.Count > 0) {
            string directory = pending.Pop();
            RequireDirectoryNoFollow(directory, "archive inventory");
            foreach (string path in Directory.EnumerateFileSystemEntries(
                         directory)) {
                inspected++;
                if (inspected > MaximumLegacyArchiveInventoryCount) {
                    throw new LegacyRootException(
                        "LegacyArchiveInventoryCount",
                        "The archive inventory exceeds its code-owned bound."
                    );
                }
                LegacyFileIdentity identity = ReadIdentityNoFollow(
                    path,
                    "LegacyArchiveShape"
                );
                string relative = Path.GetRelativePath(archive, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!actual.Add(relative)) {
                    throw new LegacyRootException(
                        "LegacyArchiveInventory",
                        "The archive inventory contains a duplicate path."
                    );
                }
                if (identity.FileType == LinuxDirectoryType) {
                    pending.Push(path);
                }
                else if (identity.FileType != LinuxRegularFileType) {
                    throw new LegacyRootException(
                        "LegacyArchiveShape",
                        $"Archive entry is not a regular file or directory: {relative}"
                    );
                }
            }
        }
        if (!actual.SetEquals(expected)) {
            throw new LegacyRootException(
                "LegacyArchiveInventory",
                "The archive contains missing or unknown inventory."
            );
        }
    }

    private static int RequireNonNegativeInt(
        CliOptions options,
        string key
    ) {
        string value = options.RequireSingle(key);
        return int.TryParse(value, out int parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException(
                $"--{key} must be a non-negative integer."
            );
    }

    private static string RequireSha256(
        CliOptions options,
        string key
    ) => RequireSha256Text(options.RequireSingle(key), $"--{key}");

    private static string RequireSha256Text(string? value, string field) {
        if (value is null || value.Length != 64
            || value.Any(static character =>
                !(character is >= '0' and <= '9'
                    or >= 'a' and <= 'f'))) {
            throw new ArgumentException(
                $"{field} must be a lowercase SHA-256 digest."
            );
        }
        return value;
    }

    private static byte[] EncodeLegacyManifest(
        LegacyRootManifest manifest
    ) => EncodeLegacyJson(writer => {
        writer.WriteStartObject();
        writer.WriteString("schema", manifest.Schema);
        writer.WriteString("branch", manifest.Branch);
        writer.WriteString("refId", manifest.RefId);
        writer.WriteString("rawHead", manifest.RawHead);
        writer.WriteNumber("entryCount", manifest.EntryCount);
        writer.WriteNumber("totalBytes", manifest.TotalBytes);
        writer.WriteString("contentSha256", manifest.ContentSha256);
        WriteLegacyEntries(writer, manifest.Entries);
        writer.WriteEndObject();
    });

    private static object DescribeLegacyManifest(
        LegacyRootManifest manifest
    ) => new {
        schema = manifest.Schema,
        branch = manifest.Branch,
        refId = manifest.RefId,
        rawHead = manifest.RawHead,
        entryCount = manifest.EntryCount,
        totalBytes = manifest.TotalBytes,
        contentSha256 = manifest.ContentSha256,
        entries = manifest.Entries.Select(static entry => new {
            path = entry.Path,
            type = entry.Type,
            length = entry.Length,
            sha256 = entry.Sha256
        })
    };

    private static byte[] EncodeLegacyManifestBody(
        IReadOnlyList<LegacyRootEntry> entries,
        long totalBytes
    ) => EncodeLegacyJson(writer => {
        writer.WriteStartObject();
        writer.WriteString("schema", LegacyManifestSchema);
        writer.WriteNumber("entryCount", entries.Count);
        writer.WriteNumber("totalBytes", totalBytes);
        WriteLegacyEntries(writer, entries);
        writer.WriteEndObject();
    });

    private static void WriteLegacyEntries(
        Utf8JsonWriter writer,
        IReadOnlyList<LegacyRootEntry> entries
    ) {
        writer.WritePropertyName("entries");
        writer.WriteStartArray();
        foreach (LegacyRootEntry entry in entries) {
            writer.WriteStartObject();
            writer.WriteString("path", entry.Path);
            writer.WriteString("type", entry.Type);
            writer.WriteNumber("length", entry.Length);
            if (entry.Sha256 is null) {
                writer.WriteNull("sha256");
            }
            else {
                writer.WriteString("sha256", entry.Sha256);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static byte[] EncodeLegacyJson(Action<Utf8JsonWriter> write) {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   })) {
            write(writer);
        }
        return buffer.ToArray();
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsReparsePoint(string path) {
        try {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint)
                != 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void RequireLinuxLegacyCapability() {
        if (!OperatingSystem.IsLinux()
            || RuntimeInformation.ProcessArchitecture
                is not (Architecture.X64 or Architecture.Arm64)) {
            throw new LegacyRootException(
                "LegacyPlatformUnsupported",
                "RecapGrid legacy-root V2 requires Linux x64 or arm64 no-follow/fsync support."
            );
        }
    }

    private static bool TryRequireLinuxLegacyCapability(
        string command,
        out int rejection
    ) {
        try {
            RequireLinuxLegacyCapability();
            rejection = 0;
            return true;
        }
        catch (LegacyRootException exception) when (
            exception.Code == "LegacyPlatformUnsupported") {
            rejection = Print(
                command,
                "platform-unsupported",
                new { code = exception.Code, detail = exception.Message },
                2
            );
            return false;
        }
    }

    private static LegacyFileIdentity ReadIdentityNoFollow(
        string path,
        string defectCode
    ) {
        RequireLinuxLegacyCapability();
        IntPtr buffer = Marshal.AllocHGlobal(256);
        try {
            Marshal.Copy(new byte[256], 0, buffer, 256);
            if (Lstat(path, buffer) != 0) {
                throw new IOException(
                    $"lstat failed for '{path}' (errno {Marshal.GetLastPInvokeError()})."
                );
            }
            return ReadLinuxIdentity(buffer, defectCode, path);
        }
        finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static LegacyFileIdentity ReadDescriptorIdentity(
        int descriptor,
        string defectCode,
        string path
    ) {
        IntPtr buffer = Marshal.AllocHGlobal(256);
        try {
            Marshal.Copy(new byte[256], 0, buffer, 256);
            if (Fstat(descriptor, buffer) != 0) {
                throw new IOException(
                    $"fstat failed for '{path}' (errno {Marshal.GetLastPInvokeError()})."
                );
            }
            return ReadLinuxIdentity(buffer, defectCode, path);
        }
        finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static LegacyFileIdentity ReadLinuxIdentity(
        IntPtr buffer,
        string defectCode,
        string path
    ) {
        int modeOffset = RuntimeInformation.ProcessArchitecture switch {
            Architecture.X64 => 24,
            Architecture.Arm64 => 16,
            _ => throw new LegacyRootException(
                "LegacyPlatformUnsupported",
                "Unsupported Linux stat ABI."
            )
        };
        ulong device = unchecked((ulong)Marshal.ReadInt64(buffer, 0));
        ulong inode = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
        uint mode = unchecked((uint)Marshal.ReadInt32(buffer, modeOffset));
        uint fileType = mode & LinuxFileTypeMask;
        if (fileType is not (LinuxDirectoryType or LinuxRegularFileType)) {
            throw new LegacyRootException(
                defectCode,
                $"Path is not a regular file or directory: {path}"
            );
        }
        return new LegacyFileIdentity(device, inode, fileType);
    }

    private static LegacyFileIdentity ReadDirectoryIdentityNoFollow(
        string path,
        string defectCode
    ) {
        (int descriptor, LegacyFileIdentity identity) =
            OpenDirectoryIdentityNoFollow(path, defectCode);
        try {
            return identity;
        }
        finally {
            _ = Close(descriptor);
        }
    }

    private static (int Descriptor, LegacyFileIdentity Identity)
        OpenDirectoryIdentityNoFollow(
            string path,
            string defectCode
        ) {
        LegacyFileIdentity before = ReadIdentityNoFollow(path, defectCode);
        if (before.FileType != LinuxDirectoryType) {
            throw new LegacyRootException(
                defectCode,
                $"Path is not a directory: {path}"
            );
        }
        int descriptor = Open(
            path,
            OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec
        );
        if (descriptor < 0) {
            throw new IOException(
                $"Failed to open directory '{path}' without following links (errno {Marshal.GetLastPInvokeError()})."
            );
        }
        try {
            LegacyFileIdentity after = ReadDescriptorIdentity(
                descriptor,
                defectCode,
                path
            );
            if (after.FileType != LinuxDirectoryType
                || after.Device != before.Device
                || after.Inode != before.Inode) {
                throw new LegacyRootException(
                    defectCode,
                    $"Directory identity changed while opened: {path}"
                );
            }
            int ownedDescriptor = descriptor;
            descriptor = -1;
            return (ownedDescriptor, after);
        }
        finally {
            if (descriptor >= 0) {
                _ = Close(descriptor);
            }
        }
    }

    private static void RequireDirectoryNoFollow(
        string path,
        string description
    ) => _ = ReadDirectoryIdentityNoFollow(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
        "LegacyDirectoryNoFollow"
    );

    private static FileStream OpenRegularFileNoFollow(
        string path,
        string relative,
        int bufferSize = 128 * 1024
    ) {
        LegacyFileIdentity before = ReadIdentityNoFollow(
            path,
            "LegacyRootReparsePoint"
        );
        if (before.FileType != LinuxRegularFileType) {
            throw new LegacyRootException(
                "LegacySlotShape",
                $"Legacy path is not a regular file: {relative}"
            );
        }
        int descriptor = Open(
            path,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec
        );
        if (descriptor < 0) {
            throw new IOException(
                $"Failed to open '{relative}' without following links (errno {Marshal.GetLastPInvokeError()})."
            );
        }
        try {
            LegacyFileIdentity after = ReadDescriptorIdentity(
                descriptor,
                "LegacySlotShape",
                relative
            );
            if (after.FileType != LinuxRegularFileType
                || after.Device != before.Device
                || after.Inode != before.Inode) {
                throw new LegacyRootException(
                    "LegacyRootChanged",
                    $"Legacy file identity changed while opened: {relative}"
                );
            }
            var handle = new SafeFileHandle(
                new IntPtr(descriptor),
                ownsHandle: true
            );
            descriptor = -1;
            return new FileStream(
                handle,
                FileAccess.Read,
                bufferSize,
                isAsync: false
            );
        }
        finally {
            if (descriptor >= 0) {
                _ = Close(descriptor);
            }
        }
    }

    private static void FlushDirectoryTreePostOrder(string root) {
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        int inspected = 0;
        while (pending.Count > 0) {
            string directory = pending.Pop();
            RequireDirectoryNoFollow(directory, "archive payload");
            directories.Add(directory);
            foreach (string child in Directory.EnumerateFileSystemEntries(
                         directory)) {
                inspected++;
                if (inspected > MaximumLegacyArchiveInventoryCount) {
                    throw new LegacyRootException(
                        "LegacyArchiveInventoryCount",
                        "The archive payload exceeds its inventory bound."
                    );
                }
                LegacyFileIdentity identity = ReadIdentityNoFollow(
                    child,
                    "LegacyArchiveShape"
                );
                if (identity.FileType == LinuxDirectoryType) {
                    pending.Push(child);
                }
                else if (identity.FileType != LinuxRegularFileType) {
                    throw new LegacyRootException(
                        "LegacyArchiveShape",
                        "The archive payload contains a non-regular entry."
                    );
                }
            }
        }
        foreach (string directory in directories
                     .OrderByDescending(static value => value.Count(
                         static character => character
                             == Path.DirectorySeparatorChar))) {
            FlushDirectory(directory);
        }
    }

    private static void RequireTemporaryTreeSafe(string root) {
        var pending = new Stack<string>();
        pending.Push(root);
        int inspected = 0;
        while (pending.Count > 0) {
            string directory = pending.Pop();
            RequireDirectoryNoFollow(directory, "temporary archive");
            foreach (string child in Directory.EnumerateFileSystemEntries(
                         directory)) {
                inspected++;
                if (inspected > MaximumLegacyArchiveInventoryCount) {
                    throw new LegacyRootException(
                        "LegacyArchiveInventoryCount",
                        "The temporary archive exceeds its inventory bound."
                    );
                }
                LegacyFileIdentity identity = ReadIdentityNoFollow(
                    child,
                    "LegacyTemporaryShape"
                );
                if (identity.FileType == LinuxDirectoryType) {
                    pending.Push(child);
                }
                else if (identity.FileType != LinuxRegularFileType) {
                    throw new LegacyRootException(
                        "LegacyTemporaryShape",
                        "The temporary archive contains a non-regular entry."
                    );
                }
            }
        }
    }

    private static void TryDeleteTemporaryArchive(
        string temporary,
        LegacyFileIdentity expected,
        int ownerDescriptor
    ) {
        if (!Directory.Exists(temporary)) { return; }
        try {
            if (ownerDescriptor < 0) { return; }
            LegacyFileIdentity owner = ReadDescriptorIdentity(
                ownerDescriptor,
                "LegacyTemporaryShape",
                temporary
            );
            if (owner.Device != expected.Device
                || owner.Inode != expected.Inode
                || owner.FileType != LinuxDirectoryType) {
                return;
            }
            LegacyFileIdentity current = ReadDirectoryIdentityNoFollow(
                temporary,
                "LegacyTemporaryShape"
            );
            if (current.Device != expected.Device
                || current.Inode != expected.Inode) {
                return;
            }
            RequireTemporaryTreeSafe(temporary);
            Directory.Delete(temporary, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (LegacyRootException) { }
    }

    private static void FlushDirectory(string path) {
        RequireLinuxLegacyCapability();
        int descriptor = Open(
            path,
            OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec
        );
        if (descriptor < 0) {
            throw new IOException("Failed to open directory for fsync.");
        }
        try {
            if (Fsync(descriptor) != 0) {
                throw new IOException("Failed to fsync directory.");
            }
        }
        finally {
            _ = Close(descriptor);
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int Lstat(string path, IntPtr value);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int Fstat(int descriptor, IntPtr value);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    private readonly record struct LegacyFileIdentity(
        ulong Device,
        ulong Inode,
        uint FileType
    );

    private sealed record LegacyRootEntry(
        string Path,
        string Type,
        long Length,
        string? Sha256
    );

    private sealed record LegacyRootManifest(
        string Schema,
        int EntryCount,
        long TotalBytes,
        string ContentSha256,
        IReadOnlyList<LegacyRootEntry> Entries,
        string? Branch = null,
        string? RefId = null,
        string? RawHead = null
    ) {
        internal LegacyRootManifest WithAuthority(
            string branch,
            string refId,
            string rawHead
        ) => this with {
            Branch = branch,
            RefId = refId,
            RawHead = rawHead
        };

        internal bool EqualsExact(LegacyRootManifest other) =>
            EqualsContentExact(other)
            && string.Equals(Branch, other.Branch, StringComparison.Ordinal)
            && string.Equals(RefId, other.RefId, StringComparison.Ordinal)
            && string.Equals(RawHead, other.RawHead, StringComparison.Ordinal);

        internal bool EqualsContentExact(LegacyRootManifest other) =>
            string.Equals(Schema, other.Schema, StringComparison.Ordinal)
            && EntryCount == other.EntryCount
            && TotalBytes == other.TotalBytes
            && string.Equals(
                ContentSha256,
                other.ContentSha256,
                StringComparison.Ordinal
            )
            && Entries.SequenceEqual(other.Entries);

        internal bool IsExactSubsetOf(LegacyRootManifest archive) {
            var archiveEntries = archive.Entries.ToHashSet();
            return Entries.All(archiveEntries.Contains);
        }
    }

    private abstract record LegacyRootCaptureResult {
        private LegacyRootCaptureResult() { }
        internal sealed record Available(LegacyRootManifest Manifest)
            : LegacyRootCaptureResult;
        internal sealed record ForbiddenV9(string RelativePath)
            : LegacyRootCaptureResult;
        internal sealed record Invalid(string Code, string Detail)
            : LegacyRootCaptureResult;
    }

    private abstract record LegacyArchiveVerificationResult {
        private LegacyArchiveVerificationResult() { }
        internal sealed record Verified(
            LegacyRootManifest Manifest,
            string ManifestSha256
        ) : LegacyArchiveVerificationResult;
        internal sealed record Invalid(string Code, string Detail)
            : LegacyArchiveVerificationResult;
    }

    private sealed class LegacyRootException(
        string code,
        string message
    ) : Exception(message) {
        internal string Code { get; } = code;
    }
}
