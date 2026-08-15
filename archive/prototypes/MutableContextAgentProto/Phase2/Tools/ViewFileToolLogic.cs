using Atelia.MutableContextAgentProto.Phase2.Model;

namespace Atelia.MutableContextAgentProto.Phase2.Tools;

public sealed class ViewFileToolLogic {
    public const int DefaultMaxBytes = 64 * 1024;
    public const int DefaultMaxLines = 400;

    public ViewFileToolLogic(
        string workspaceRoot,
        int maxBytes = DefaultMaxBytes,
        int maxLines = DefaultMaxLines
    ) {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) { throw new ArgumentException("Workspace root cannot be empty.", nameof(workspaceRoot)); }

        if (maxBytes < 1) { throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Max bytes must be 1 or greater."); }

        if (maxLines < 1) { throw new ArgumentOutOfRangeException(nameof(maxLines), maxLines, "Max lines must be 1 or greater."); }

        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        MaxBytes = maxBytes;
        MaxLines = maxLines;
    }

    public string WorkspaceRoot { get; }

    public int MaxBytes { get; }

    public int MaxLines { get; }

    public async ValueTask<NumberedFileView> ViewAsync(
        string intention,
        string relativePath,
        CancellationToken cancellationToken = default
    ) {
        if (string.IsNullOrWhiteSpace(intention)) { throw new ArgumentException("Intention is required for view_file.", nameof(intention)); }

        string fullPath = ResolveSafeFilePath(relativePath);
        FileInfo fileInfo = new(fullPath);
        if (!fileInfo.Exists) { throw new FileNotFoundException($"File '{relativePath}' does not exist under workspace root.", relativePath); }

        if (fileInfo.Length > MaxBytes) {
            throw new InvalidOperationException(
                $"File '{relativePath}' is {fileInfo.Length} bytes, exceeding the view_file limit of {MaxBytes} bytes."
            );
        }

        string text = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        NumberedFileView view = NumberedFileView.FromText(
            NormalizeRelativePath(fullPath),
            intention.Trim(),
            text
        );

        if (view.LineCount > MaxLines) {
            throw new InvalidOperationException(
                $"File '{relativePath}' has {view.LineCount} lines, exceeding the view_file limit of {MaxLines} lines."
            );
        }

        return view;
    }

    public string ResolveSafeFilePath(string relativePath) {
        if (string.IsNullOrWhiteSpace(relativePath)) { throw new ArgumentException("Path is required for view_file.", nameof(relativePath)); }

        string trimmedPath = relativePath.Trim();
        if (Path.IsPathRooted(trimmedPath)) { throw new ArgumentException("view_file path must be relative to the workspace root.", nameof(relativePath)); }

        string fullPath = Path.GetFullPath(Path.Combine(WorkspaceRoot, trimmedPath));
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(WorkspaceRoot) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal) &&
            !string.Equals(fullPath, Path.TrimEndingDirectorySeparator(WorkspaceRoot), StringComparison.Ordinal)) {
            throw new ArgumentException(
                $"view_file path '{relativePath}' escapes the workspace root.",
                nameof(relativePath)
            );
        }

        return fullPath;
    }

    private string NormalizeRelativePath(string fullPath) {
        string relativePath = Path.GetRelativePath(WorkspaceRoot, fullPath);
        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }
}
