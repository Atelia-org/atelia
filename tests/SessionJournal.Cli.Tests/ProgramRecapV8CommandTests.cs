using System.Text;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramRecapV8CommandTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-v8-cli-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void PlannerConfigInitWritesCanonicalV3AndRejectsOldField() {
        Directory.CreateDirectory(_root);
        Assert.Equal(0, Program.Main([
            "recap", "planner-config", "init", "--input", _root
        ]));
        string path = RecapEpochConfigLoader.GetCanonicalPath(_root);
        byte[] canonical = File.ReadAllBytes(path);
        RecapEpochConfigDocument document =
            RecapEpochConfigCodec.Decode(canonical);
        Assert.Equal(RecapEpochConfigCodec.SchemaV3, document.Schema);
        Assert.Equal(0, Program.Main([
            "recap", "planner-config", "inspect", "--input", _root
        ]));

        RecapEpochConfigDocument changedStoreLimit = document with {
            Limits = document.Limits with {
                MaxPublicationBytes =
                    document.Limits.MaxPublicationBytes - 1
            }
        };
        File.WriteAllBytes(
            path,
            RecapEpochConfigCodec.Encode(changedStoreLimit)
        );
        Assert.Equal(2, Program.Main([
            "recap", "planner-config", "inspect", "--input", _root
        ]));

        string old = Encoding.UTF8.GetString(canonical).Replace(
            "\"maxMaintainerCallsPerEpoch\":2",
            "\"maxMaintainerCallsPerBuild\":2",
            StringComparison.Ordinal
        );
        File.WriteAllText(path, old, new UTF8Encoding(false));
        Assert.Equal(2, Program.Main([
            "recap", "planner-config", "inspect", "--input", _root
        ]));
    }

    [Fact]
    public void RunAndExplicitResetRebuildEnforceConfirmationAndSealAuthority() {
        Directory.CreateDirectory(_root);
        string repository = Path.Combine(_root, "repo");
        string connections = Path.Combine(_root, "connections.json");
        string refId;
        using (SessionJournalEngine engine = SessionJournalEngine.Create(
            repository,
            new SessionCreateOptions("model-a", "system-a", "surface-a")
        )) {
            refId = engine.BranchRefId.ToHexString();
        }
        File.WriteAllText(
            connections,
            """
            {
              "defaultConnectionId": "test",
              "connections": [{
                "id": "test",
                "kind": "openai-chat",
                "modelId": "model-a",
                "completionSurfaceId": "surface-a",
                "baseAddress": "https://example.invalid"
              }]
            }
            """
        );
        string[] common = [
            "--input", repository,
            "--branch", "main",
            "--connections", connections
        ];
        Assert.Equal(0, Program.MainCore([
            "recap", "run", .. common
        ], ThrowingCompletionClientFactory.Instance));
        string storeRoot = Path.Combine(
            repository,
            "derived",
            "recap",
            "v8",
            "refs",
            refId
        );
        string marker = Path.Combine(storeRoot, "reset-marker");
        File.WriteAllText(marker, "must disappear only after confirmation");
        string campaign = Guid.NewGuid().ToString("N");

        Assert.Equal(1, Program.MainCore([
            "recap", "rebuild", .. common,
            "--campaign", campaign,
            "--reset",
            "--confirm-ref", new string('0', 32)
        ], ThrowingCompletionClientFactory.Instance));
        Assert.True(File.Exists(marker));

        Assert.Equal(0, Program.MainCore([
            "recap", "rebuild", .. common,
            "--campaign", campaign,
            "--reset",
            "--confirm-ref", refId
        ], ThrowingCompletionClientFactory.Instance));
        Assert.False(File.Exists(marker));
        Assert.True(File.Exists(Path.Combine(
            repository,
            "derived",
            "recap",
            "rebuild",
            "v1",
            "campaigns",
            refId,
            campaign,
            "seal.json"
        )));
        string resumeMarker = Path.Combine(
            storeRoot,
            "same-campaign-resume-marker"
        );
        File.WriteAllText(resumeMarker, "must survive non-reset resume");
        Assert.Equal(0, Program.MainCore([
            "recap", "rebuild", .. common,
            "--campaign", campaign
        ], ThrowingCompletionClientFactory.Instance));
        Assert.True(File.Exists(resumeMarker));
    }

    public void Dispose() {
        try {
            if (Directory.Exists(_root)) {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch {
        }
    }

    private sealed class ThrowingCompletionClientFactory
        : ICompletionClientFactory {
        internal static ThrowingCompletionClientFactory Instance { get; } =
            new();

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => throw new InvalidOperationException(
            $"NoBuild recap command must not create '{connection.Id}'."
        );
    }
}
