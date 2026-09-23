using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.RecapGrid;
using Atelia.MdJson;
using Atelia.SessionJournal.HistoryTimeline;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed partial class ProgramRecapGridCommandTests {
    [Fact]
    public void GalateaV7PublicBuildProjectsMixedHistoryAndPreservesMachineInputs() {
        SessionInputContent[] inputs = [
            SessionInputContent.Text("legacy text remains exact\r\n```\nlegacy\n```"),
            StructuredBuildPlayer(),
            StructuredBuildHeartbeat(),
            // Leave a complete recent turn after the input under examination,
            // so cadence can retain its required recent reserve.
            SessionInputContent.Text("recent reserve after structured history")
        ];
        StructuredBuildFixture fixture = PrepareStructuredBuild(inputs);
        Dictionary<string, byte[]> before = SnapshotRawAuthority();
        var factory = new DeterministicCompletionClientFactory();

        (int code, JsonElement report) = RunCapturedWithFactory(factory, StructuredBuildArgs(fixture));

        Assert.Equal(0, code);
        Assert.Equal("fulfilled", report.GetProperty("status").GetString());
        Assert.True(factory.RecapRequestCount > 0);
        Assert.Equal(factory.RequestCount, factory.RecapRequestCount);
        Assert.All(factory.Requests, request => Assert.DoesNotContain(
            request.PromptPrefix.SharedContextMessages, message => message is SessionInputObservationMessage));
        ObservationMessage[] sent = factory.Requests.SelectMany(request => request.PromptPrefix.SharedContextMessages)
            .OfType<ObservationMessage>().ToArray();
        Assert.Contains(sent, message => message.Content == inputs[0].TextValue);

        JsonElement[] projected = sent.Where(message => message.Content?.Contains("externalLocalTimestamp", StringComparison.Ordinal) == true)
            .Select(message => MdJsonSerializer.Read(message.Content!)).ToArray();
        foreach (SessionInputContent expected in inputs.Where(input => input.IsStructured)) {
            Assert.Contains(projected, actual => SessionInputContent.Structured(expected.SchemaId!, actual) == expected);
        }
        JsonElement player = projected.First(value => value.GetProperty("kind").GetString() == "player-action");
        Assert.Equal("player-main", player.GetProperty("sender").GetProperty("id").GetString());
        Assert.Equal("captured player", player.GetProperty("sender").GetProperty("name").GetString());
        Assert.Equal("delegate", player.GetProperty("notices")[0].GetProperty("sender").GetProperty("kind").GetString());
        Assert.Equal("dispatch-fixture", player.GetProperty("notices")[0].GetProperty("dispatchId").GetString());
        Assert.Equal("revision-7", player.GetProperty("recalls")[0].GetProperty("sourceVersion").GetString());
        Assert.Equal("2026-09-15T10:20:30.0000000+08:00", player.GetProperty("externalLocalTimestamp").GetString());
        AssertSnapshotEqual(before, SnapshotRawAuthority());
        AssertStructuredBuildHistory(inputs);

        // Public read-only commands use no domain projector and cannot create a provider.
        Assert.Equal(0, Run("progress", "--input", _root, "--recipe", fixture.Recipe,
            "--max-recipe-row-steps", "64", "--max-new-calls", "0", "--max-elapsed-ms", "10000"));
        Assert.Equal(0, Run("timeline", "verify", "--input", _root));
        Assert.Equal(0, Run("control", "verify", "--input", _root));
        Assert.Equal(0, Run("verify", "--input", _root));
        AssertSnapshotEqual(before, SnapshotRawAuthority());
    }

    [Fact]
    public void GalateaV7PublicBuildRejectsUnknownSchemaBeforeDispatchButReadersRemainAvailable() {
        SessionInputContent[] inputs = [
            SessionInputContent.Structured("unknown.observation.v1", JsonSerializer.SerializeToElement(new { body = "unknown source" })),
            SessionInputContent.Text("recent reserve")
        ];
        StructuredBuildFixture fixture = PrepareStructuredBuild(inputs);
        Dictionary<string, byte[]> before = SnapshotRawAuthority();
        Assert.Equal(0, Run("progress", "--input", _root, "--recipe", fixture.Recipe,
            "--max-recipe-row-steps", "64", "--max-new-calls", "64", "--max-elapsed-ms", "10000"));
        Assert.Equal(0, Run("timeline", "inspect", "--input", _root));
        Assert.Equal(0, Run("control", "inspect", "--input", _root));
        AssertStructuredBuildHistory(inputs);

        var factory = new DeterministicCompletionClientFactory();
        (int code, JsonElement report) = RunCapturedWithFactory(factory, StructuredBuildArgs(fixture));

        Assert.Equal(2, code);
        Assert.Equal("incomplete", report.GetProperty("status").GetString());
        JsonElement[] failures = report.GetProperty("detail").GetProperty("result")
            .GetProperty("Failures").EnumerateArray().ToArray();
        Assert.Equal(2, failures.Length);
        Assert.All(failures, failure => Assert.Equal("InputProjectionFailed", failure.GetProperty("Code").GetString()));
        Assert.Empty(factory.Requests);
        AssertSnapshotEqual(before, SnapshotRawAuthority());
        AssertStructuredBuildHistory(inputs);
    }

    private StructuredBuildFixture PrepareStructuredBuild(IReadOnlyList<SessionInputContent> inputs) {
        RefId refId;
        using (SessionJournalEngine created = SessionJournalEngine.Create(_root,
            new SessionCreateOptions("test-model", "system", "test-v1"))) {
            refId = created.BranchRefId;
        }
        foreach (SessionInputContent input in inputs) {
            if (!input.IsStructured) {
                EventAddress head;
                using (var reader = SessionJournalEngine.OpenReadOnly(_root)) { head = reader.ReadCurrentHead()!.Value; }
                // Genuine old Observation v1 payload, not a current writer with
                // a misleading test name. Structured inputs use the current API.
                using var journal = Atelia.EventJournal.EventJournal.OpenExisting(_root);
                journal.CommitToRef(SessionJournalDefaults.MainBranchName, head,
                    JsonSerializer.SerializeToUtf8Bytes(new { v = 1, body = new { content = input.TextValue } }),
                    opaqueEventKind: (uint)SessionEventKind.ObservationAccepted, hint: default).Unwrap();
            }
            using (SessionJournalEngine writer = SessionJournalEngine.Open(_root)) {
                if (input.IsStructured) { writer.AppendObservation(input); }
                writer.AppendImportedAgentAction(new ActionMessage([new ActionBlock.Text("captured action")]),
                    new CompletionDescriptor("import", "legacy-import-v1", "test-model"));
            }
        }
        string admission = ExternalPath("structured-admission.json");
        string routes = ExternalPath("structured-routes.json");
        (int scaffoldCode, JsonElement scaffold) = RunCaptured(
            "scaffold", "--asset", GalateaRecapGridAssets.RollingRewriteZhCnV7,
            "--character-name", "Galatea",
            "--connection-id", "test", "--permission", "create",
            "--permission", "register-family", "--permission", "register-definition",
            "--permission", "register-recipe", "--permission", "promote",
            "--logical-column-prefix", "world-understanding", "--logical-column-prefix", "autobiography",
            "--max-bootstrap-rows", "64", "--max-projected-calls", "128",
            "--max-concurrency", "1", "--dispatch-timeout-ms", "10000",
            "--admission-output", admission, "--route-output", routes);
        Assert.Equal(0, scaffoldCode);
        Assert.Equal(0, RunInit(SessionJournalDefaults.MainBranchName, refId, admission));
        Assert.Equal(0, Run("timeline", "sync", "--input", _root,
            "--confirm-ref", refId.ToHexString(), "--max-rows", "32"));
        Assert.True(ReadTimelineAuthority(refId).Head.SelectedPathCount > 0);
        Assert.Equal(0, Run("control", "provision-asset", "--input", _root,
            "--confirm-ref", refId.ToHexString(), "--admission", admission,
            "--asset", GalateaRecapGridAssets.RollingRewriteZhCnV7, "--character-name", "Galatea"));
        JsonElement[] definitions = scaffold.GetProperty("detail").GetProperty("definitions").EnumerateArray().ToArray();
        string world = definitions.Single(value => value.GetProperty("logicalColumnId").GetString() == "world-understanding")
            .GetProperty("digest").GetString()!;
        string self = definitions.Single(value => value.GetProperty("logicalColumnId").GetString() == "autobiography")
            .GetProperty("digest").GetString()!;
        string recipeFile = ExternalPath("structured-recipe.json");
        (int composeCode, JsonElement compose) = RunCaptured("control", "compose-full-recipe",
            "--input", _root, "--definition", world, "--definition", self, "--output", recipeFile);
        Assert.Equal(0, composeCode);
        Assert.Equal(0, Run("control", "put-recipe", "--input", _root,
            "--confirm-ref", refId.ToHexString(), "--admission", admission, "--recipe", recipeFile));
        string connections = WriteBytes("structured-connections.json", JsonSerializer.SerializeToUtf8Bytes(new {
            v = 3,
            connections = new[] { new { id = "test", kind = "test", modelId = "test-model", completionSurfaceId = "test-v1", baseAddress = "https://example.invalid" } },
            selectableConnectionIds = new[] { "test" },
            bindings = new Dictionary<string, string?> {
                ["galatea.input-normalizer"] = null,
                ["galatea.outbound-mail-extractor"] = null,
                ["galatea.character-note-extractor"] = null,
                ["galatea.memo-recall"] = null
            }
        }));
        return new(refId.ToHexString(), compose.GetProperty("detail").GetProperty("recipeDigest").GetString()!, routes, connections);
    }

    private string[] StructuredBuildArgs(StructuredBuildFixture fixture) => [
        "build", "--input", _root, "--confirm-ref", fixture.RefId, "--recipe", fixture.Recipe,
        "--max-recipe-row-steps", "64", "--max-new-calls", "64", "--max-elapsed-ms", "10000",
        "--routes", fixture.Routes, "--connections", fixture.Connections
    ];

    private void AssertStructuredBuildHistory(IReadOnlyList<SessionInputContent> expected) {
        using var reader = SessionJournalEngine.OpenReadOnly(_root);
        _ = reader.ScanCheckedAuditEvents(_ => { });
        SessionInputContent[] actual = Assert.IsType<SessionCompletedTurnsReadResult.Snapshot>(reader.ReadRecentCompletedTurns(32)).Value.Turns
            .Select(turn => turn.ObservationContent).Reverse().ToArray();
        Assert.Equal(expected, actual);
    }

    private static SessionInputContent StructuredBuildPlayer() => SessionInputContent.Structured("galatea.observation.v1",
        JsonSerializer.SerializeToElement(new {
            v = 1, kind = "player-action",
            sender = new { kind = "player", id = "player-main", name = "captured player" },
            externalLocalTimestamp = "2026-09-15T10:20:30.0000000+08:00",
            action = new { text = "  推开门。\r\n```json\n{\"raw\":\"\\n\"}\n```\n" },
            notices = new[] { new {
                kind = "reply", sender = new { kind = "delegate", id = "codex", name = "Codex" },
                dispatchId = "dispatch-fixture", threadId = "thread-fixture", turnId = "turn-fixture", noticeId = "notice-fixture",
                body = "回信原文\r\n~~~~\n不改变作者"
            } },
            recalls = new[] { new { kind = "memo-gist", sourceId = "fixture-memory", sourceVersion = "revision-7", text = "当时保存的记忆\n```\n原文\n```" } }
        }));

    private static SessionInputContent StructuredBuildHeartbeat() => SessionInputContent.Structured("galatea.observation.v1",
        JsonSerializer.SerializeToElement(new {
            v = 1, kind = "heartbeat-activation",
            sender = new { kind = "runtime", id = "galatea", name = "Galatea runtime" },
            externalLocalTimestamp = "2026-09-15T10:30:30.0000000+08:00",
            action = new { character = new { kind = "character", id = "g-01", name = "Galatea" }, externalIntervalMinutes = 10 },
            notices = Array.Empty<object>(), recalls = Array.Empty<object>()
        }));

    private sealed record StructuredBuildFixture(string RefId, string Recipe, string Routes, string Connections);
}
