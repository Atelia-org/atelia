using System.Text.Json;
using Atelia.ChatSession;
using Xunit;

namespace Atelia.ChatSession.LegacyExportCli.Tests;

public sealed class ProgramTests : IDisposable {
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-chat-session-legacy-export-cli-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        try {
            if (Directory.Exists(_tempRoot)) {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public void ExportJson_WritesLegacyUpgradeSchemaAndAtomicallyReplacesOutput() {
        string repoPath = CreateLegacyRepository();
        string outputPath = Path.Combine(_tempRoot, "exports", "session.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, "stale");

        int exitCode = Program.MainCore(
            [
                "export-json",
                "--input", repoPath,
                "--output", outputPath,
                "--compact"
            ]
        );

        Assert.Equal(0, exitCode);
        string json = File.ReadAllText(outputPath);
        Assert.DoesNotContain('\n', json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(
            "atelia.chat-session.legacy-upgrade-export.v1",
            root.GetProperty("schema").GetString()
        );
        Assert.Equal("main", root.GetProperty("branchName").GetString());
        Assert.Equal(2, root.GetProperty("events").GetArrayLength());
        Assert.Equal(
            "hello",
            root.GetProperty("events")[1]
                .GetProperty("appendedMessages")[0]
                .GetProperty("content")
                .GetString()
        );
        Assert.Empty(
            Directory.EnumerateFiles(
                Path.GetDirectoryName(outputPath)!,
                $".{Path.GetFileName(outputPath)}.*.tmp"
            )
        );
    }

    [Fact]
    public void ExportMarkdown_WritesPlainTranscript() {
        string repoPath = CreateLegacyRepository();
        string outputPath = Path.Combine(_tempRoot, "exports", "session.md");

        int exitCode = Program.MainCore(
            [
                "export-markdown",
                "--input", repoPath,
                "--output", outputPath,
                "--exclude-warnings"
            ]
        );

        Assert.Equal(0, exitCode);
        string markdown = File.ReadAllText(outputPath);
        Assert.Contains(
            "~~~~~~system-prompt\nsystem prompt\n~~~~~~",
            markdown,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "~~~~~~observation\nhello\n~~~~~~",
            markdown,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "~~~~~~action\nworld\n~~~~~~",
            markdown,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "appendedMessages",
            markdown,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ExportJson_RejectsOutputInsideInputRepository() {
        string repoPath = CreateLegacyRepository();
        string outputPath = Path.Combine(repoPath, "derived", "session.json");

        int exitCode = Program.MainCore(
            [
                "export-json",
                "--input", repoPath,
                "--output", outputPath
            ]
        );

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void ExportMarkdown_RejectsSymlinkInOutputPathChain() {
        if (OperatingSystem.IsWindows()) { return; }

        string repoPath = CreateLegacyRepository();
        string actualOutputDirectory = Path.Combine(_tempRoot, "actual-output");
        string linkedOutputDirectory = Path.Combine(_tempRoot, "linked-output");
        Directory.CreateDirectory(actualOutputDirectory);
        Directory.CreateSymbolicLink(
            linkedOutputDirectory,
            actualOutputDirectory
        );
        string outputPath = Path.Combine(linkedOutputDirectory, "session.md");

        int exitCode = Program.MainCore(
            [
                "export-markdown",
                "--input", repoPath,
                "--output", outputPath
            ]
        );

        Assert.Equal(1, exitCode);
        Assert.False(
            File.Exists(Path.Combine(actualOutputDirectory, "session.md"))
        );
    }

    private string CreateLegacyRepository() {
        Directory.CreateDirectory(_tempRoot);
        string sourcePath = Path.Combine(_tempRoot, "source.json");
        string repoPath = Path.Combine(_tempRoot, $"repo-{Guid.NewGuid():N}");
        File.WriteAllText(
            sourcePath,
            """
            {
              "schema": "atelia.chat-session.legacy-upgrade-export.v1",
              "branchName": "main",
              "events": [
                {
                  "ordinal": 0,
                  "commit": "initial",
                  "kind": "initial-state",
                  "root": {
                    "kind": "chat-session",
                    "schemaVersion": 2,
                    "completionSurfaceId": "surface-a",
                    "modelId": "model-a",
                    "systemPrompt": "system prompt"
                  },
                  "messages": []
                },
                {
                  "ordinal": 1,
                  "commit": "turn",
                  "kind": "model-turn",
                  "appendedMessages": [
                    {
                      "kind": "observation",
                      "content": "hello"
                    },
                    {
                      "kind": "action",
                      "action": {
                        "flattenedText": "world",
                        "blocks": [
                          {
                            "kind": "text",
                            "content": "world"
                          }
                        ]
                      }
                    }
                  ]
                }
              ],
              "warnings": []
            }
            """
        );
        ChatSessionLegacyEventSourceImporter.Import(sourcePath, repoPath);
        return repoPath;
    }
}
