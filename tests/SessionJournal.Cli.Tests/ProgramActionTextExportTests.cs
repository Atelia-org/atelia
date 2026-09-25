using System.Security.Cryptography;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.Cli;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramActionTextExportTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-action-text-export-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void ExportsExactTextBlocksWithoutBomOrAddedSeparators() {
        (string repo, _, EventAddress action) = CreateSession();
        string output = Path.Combine(_root, "exports", "action.txt");
        var before = Snapshot(repo);

        Assert.Equal(0, Run(repo, action, output));

        byte[] actual = File.ReadAllBytes(output);
        byte[] expected = Encoding.UTF8.GetBytes(
            "<think>private</think>收件人：Codex\r\n正文"
        );
        Assert.Equal(expected, actual);
        Assert.Equal(before, Snapshot(repo));

        Assert.Equal(1, Run(repo, action, output));
        Assert.Equal(expected, File.ReadAllBytes(output));
    }

    [Fact]
    public void RejectsNonActionAddressAndOutputInsideRepository() {
        (string repo, EventAddress observation, EventAddress action) =
            CreateSession();
        string output = Path.Combine(_root, "missing.txt");
        var before = Snapshot(repo);

        Assert.Equal(1, Run(repo, observation, output));
        Assert.False(File.Exists(output));
        Assert.Equal(1, Run(repo, action, Path.Combine(repo, "action.txt")));
        Assert.False(File.Exists(Path.Combine(repo, "action.txt")));
        Assert.Equal(before, Snapshot(repo));
    }

    private (string Repo, EventAddress Observation, EventAddress Action)
        CreateSession() {
        string repo = Path.Combine(_root, "repo");
        using var engine = SessionJournalEngine.Create(
            repo,
            new SessionCreateOptions("model", "system", "surface")
        );
        EventAddress observation = engine.AppendObservation("user");
        EventAddress action = engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text("<think>private</think>收件人："),
                new ActionBlock.Text("Codex\r\n正文")
            ]),
            new CompletionDescriptor("import", "import-v1", "model")
        );
        return (repo, observation, action);
    }

    private static int Run(
        string repo,
        EventAddress address,
        string output
    ) => Program.MainCore([
        "export-action-text",
        "--input", repo,
        "--address", EventAddressTextCodec.Format(address),
        "--output", output
    ], new CliCompletionClientFactory());

    private static IReadOnlyDictionary<string, string> Snapshot(string repo)
        => Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(repo, path),
                path => Convert.ToHexStringLower(
                    SHA256.HashData(File.ReadAllBytes(path))
                ),
                StringComparer.Ordinal
            );

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }
}
