using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Store;
using System.Text.Json;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

[Collection(ConsoleSerialCollection.Name)]
public sealed class ProgramRecapGridStoreCommandTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-grid-cli-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void InspectExportVerifyAndResetNeverConstructProvider() {
        Directory.CreateDirectory(_root);
        Assert.Equal(0, Run("inspect", "--input", _root));
        Assert.Equal(0, Run("export", "--input", _root));
        Assert.Equal(0, Run("verify", "--input", _root));
        (int absentCode, string absentJson) = RunCaptured(
            "reset", "--prepare", "--input", _root
        );
        Assert.Equal(0, absentCode);
        using (JsonDocument absent = JsonDocument.Parse(absentJson)) {
            Assert.Equal(
                "absent",
                absent.RootElement.GetProperty("status").GetString()
            );
        }
        Assert.False(Directory.Exists(Path.Combine(
            _root,
            "derived",
            "recap-grid"
        )));

        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        Assert.Equal(0, Run("inspect", "--input", _root));
        Assert.Equal(0, Run("export", "--input", _root));
        Assert.Equal(0, Run("verify", "--input", _root));

        (int prepareCode, string prepareJson) = RunCaptured(
            "reset", "--prepare", "--input", _root
        );
        Assert.Equal(0, prepareCode);
        long length;
        string sha256;
        using (JsonDocument prepared = JsonDocument.Parse(prepareJson)) {
            Assert.Equal(
                "prepared",
                prepared.RootElement.GetProperty("status").GetString()
            );
            JsonElement detail = prepared.RootElement.GetProperty("detail");
            length = detail.GetProperty("length").GetInt64();
            sha256 = detail.GetProperty("sha256").GetString()!;
        }
        Assert.Equal(2, Run(
            "reset",
            "--input", _root,
            "--confirm-length", length.ToString(),
            "--confirm-sha256", new string('0', 64)
        ));
        Assert.Equal(0, Run(
            "reset",
            "--input", _root,
            "--confirm-length", length.ToString(),
            "--confirm-sha256", sha256
        ));
        Assert.Equal(0, Run("verify", "--input", _root));
    }

    private static int Run(params string[] args) => Program.MainCore(
        ["recap-grid", .. args],
        ThrowingCompletionClientFactory.Instance
    );

    private static (int ExitCode, string Json) RunCaptured(
        params string[] args
    ) {
        TextWriter original = Console.Out;
        using var output = new StringWriter();
        try {
            Console.SetOut(output);
            int exitCode = Run(args);
            string json = output.ToString().Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries
            )[^1];
            return (exitCode, json);
        }
        finally {
            Console.SetOut(original);
        }
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ThrowingCompletionClientFactory
        : ICompletionClientFactory {
        internal static ThrowingCompletionClientFactory Instance { get; } =
            new();

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => throw new InvalidOperationException(
            $"recap-grid must not construct provider '{connection.Id}'."
        );
    }
}
