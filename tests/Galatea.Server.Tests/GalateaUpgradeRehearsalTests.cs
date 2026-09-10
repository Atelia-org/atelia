using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using Journal = Atelia.EventJournal.EventJournal;

namespace Atelia.Galatea.Server.Tests;

/// <summary>
/// A synthetic previous-adapter contract rehearsal, not an old-binary migration
/// or hard-crash test. Keeps the existing raw-only Galatea context boundary and
/// exercises the real Codex converter/parser against a rejecting scripted transport.
/// </summary>
[Trait("Category", "GalateaLab")]
public sealed class GalateaUpgradeRehearsalTests(ITestOutputHelper output) {
    private const string OldModel = "gpt-5.6-sol";
    private const string NewModel = "gpt-6-astra";
    private const string RetainedAnswer = "Retained completed answer.";
    private const string FreshAnswer = "Fresh turn after explicit repair.";
    private const string NativeJson = """{"id":"rs_lab","type":"reasoning","encrypted_content":"SYNTHETIC_LAB_REASONING"}""";
    private const string PreviousFingerprint =
        "sha256:8c256736bee867f3e135ff8a61b2d8a85438cae327f3299363cd03910f982fc0";

    [Theory]
    [InlineData("AfterRequestPreparedCommitted", SessionExecutionPhase.AwaitingCompletionDispatch,
        SessionDurableDispatchState.NotStarted, 2)]
    [InlineData("AfterCompletionAttemptStartedCommitted", SessionExecutionPhase.AwaitingCompletion,
        SessionDurableDispatchState.StartedOutcomeUncertain, 3)]
    public async Task PreviousAdapterPending_ExplicitOfflineRepairThenFreshTurnSurvivesColdReopen(
        string failpoint, SessionExecutionPhase pendingPhase,
        SessionDurableDispatchState pendingDispatch, int steps) {
        var factory = new ScriptedCodexFactory();
        CompletionConnectionConfig oldConnection = Connection("test", OldModel);
        await using var lab = GalateaScenarioLab.Create(
            "previous-adapter-repair-" + pendingDispatch, factory,
            connections: [oldConnection, Connection("astra", NewModel)],
            reportArtifact: output.WriteLine);

        EventAddress retainedHead;
        CompletionDescriptor retainedOrigin;
        using (HttpClient http = lab.Host.CreateClient()) {
            using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            (GalateaHostService service, UserSessionHost session) = await SessionAsync(lab.Host);
            using HttpResponseMessage accepted = await http.PostAsJsonAsync(
                "/api/v1/chat/turns", new ChatStreamRequest("Seed retained history.", "test"));
            Assert.Equal("completed", (await WaitAsync(accepted, service, session)).Status);
            retainedHead = session.Engine.ReadCurrentHead()!.Value;
            OpenAIResponsesReasoningBlock reasoning = Assert.Single(
                Assert.Single(session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns)
                    .TerminalAction.Message.Blocks.OfType<OpenAIResponsesReasoningBlock>());
            retainedOrigin = reasoning.Origin;
            Assert.Equal(OldModel, retainedOrigin.Model);
        }
        await lab.StopAsync();

        EventAddress frozenHead;
        using (var client = (OpenAICodexResponsesClient)factory.Create(oldConnection)) {
            frozenHead = await GalateaDurableRecoveryVerticalTests.CreateRecoveryBoundaryAsync(
                lab.SessionDirectory, oldConnection, client, failpoint, pendingPhase,
                (engine, runtime) => GalateaCodexReasoningReplayVerticalTests.BindRawOnlyRuntimeAsync(
                    engine, runtime with {
                        CompletionTarget = runtime.CompletionTarget! with {
                            RequestAdapterFingerprint = PreviousFingerprint
                        }
                    }, GalateaUserMessageEnvelope.Wrap("fixture observation")));
        }
        Assert.Single(factory.Requests);
        int credentialsBeforeRefusal = factory.CredentialReads;
        await lab.ReopenAsync(factory);
        using (HttpClient http = lab.Host.CreateClient()) {
            using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            (GalateaHostService service, UserSessionHost session) = await SessionAsync(lab.Host);
            using HttpResponseMessage accepted = await http.PostAsJsonAsync(
                "/api/v1/chat/turns/resume", new ResumeTurnRequest(
                    EventAddressTextCodec.Format(frozenHead), null, RestartUncertainCompletion: true));
            GalateaLiveTurn refused = await WaitAsync(accepted, service, session);
            Assert.Equal("failed", refused.Status);
            using GalateaTurnSubscription replay = refused.Subscribe();
            GalateaSseFrame error = Assert.Single(replay.ReplayFrames, frame => frame.EventName == "error");
            Assert.Contains("\"code\":\"turn-unavailable\"", Encoding.UTF8.GetString(error.Utf8.Span));
            Assert.Equal(frozenHead, session.Engine.ReadCurrentHead());
            var frozen = Assert.IsType<SessionRuntimeRecoveryRequirements.FrozenCompletionRequired>(
                session.Engine.InspectRuntimeRecoveryRequirements());
            Assert.Equal(pendingDispatch, frozen.DispatchState);
            Assert.Equal(PreviousFingerprint, frozen.CompletionTarget.RequestAdapterFingerprint);
            Assert.Single(factory.Requests);
            Assert.Equal(credentialsBeforeRefusal, factory.CredentialReads);
        }
        await lab.StopAsync();

        SortedDictionary<string, string> before = Snapshot(lab.RootDirectory);
        string[] previewArgs = ["rewind-branch", "--input", lab.SessionDirectory,
            "--branch", "main", "--steps", steps.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        CliResult preview = await RunCliAsync(lab.RootDirectory, previewArgs);
        Assert.True(preview.ExitCode == 0, preview.Error);
        Assert.Equal(before, Snapshot(lab.RootDirectory));
        using JsonDocument report = JsonDocument.Parse(preview.Output);
        JsonElement root = report.RootElement;
        Assert.Equal("preview", root.GetProperty("status").GetString());
        Assert.Equal(EventAddressTextCodec.Format(retainedHead), root.GetProperty("targetHead").GetString());
        Assert.Equal(EventAddressTextCodec.Format(frozenHead), root.GetProperty("beforeHead").GetString());
        RefId refId = RefId.ParseHex(root.GetProperty("refId").GetString()!).Unwrap();
        int moveCount;
        using (var journal = Journal.OpenReadOnlyExisting(lab.SessionDirectory)) {
            moveCount = journal.ReadReflog(refId).Unwrap().Count;
        }
        string[] applyArgs = [.. previewArgs, "--apply", "--accept-external-effects",
            "--confirm-ref", refId.ToHexString(),
            "--expected-head", EventAddressTextCodec.Format(frozenHead),
            "--confirm-target", EventAddressTextCodec.Format(retainedHead)];
        CliResult applied = await RunCliAsync(lab.RootDirectory, applyArgs);
        Assert.True(applied.ExitCode == 0, applied.Error);
        SortedDictionary<string, string> after = Snapshot(lab.RootDirectory);
        Assert.Equal(before.Keys, after.Keys);
        string changed = Assert.Single(before.Keys, path => before[path] != after[path]);
        Assert.Contains("/refs/objects/" + refId.ToHexString() + "/", "/" + changed.Replace('\\', '/'));
        using (var journal = Journal.OpenReadOnlyExisting(lab.SessionDirectory)) {
            Assert.Equal(retainedHead, journal.GetHead(refId));
            var log = journal.ReadReflog(refId).Unwrap();
            Assert.Equal(moveCount + 1, log.Count);
            Assert.Equal(frozenHead, log[^1].OldTarget);
            Assert.Equal(retainedHead, log[^1].NewTarget);
            using var stillReadable = journal.ReadEvent(frozenHead).Unwrap();
        }
        // Reusing a previously authorized command cannot remove another event.
        Assert.Equal(2, (await RunCliAsync(lab.RootDirectory, applyArgs)).ExitCode);
        Assert.Equal(after, Snapshot(lab.RootDirectory));
        using (var engine = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory)) {
            Assert.Equal(SessionExecutionPhase.Idle, engine.InspectExecutionBoundary().Phase);
        }

        await lab.ReopenAsync(factory);
        using (HttpClient http = lab.Host.CreateClient()) {
            using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            (GalateaHostService service, UserSessionHost session) = await SessionAsync(lab.Host);
            using HttpResponseMessage accepted = await http.PostAsJsonAsync(
                "/api/v1/chat/turns", new ChatStreamRequest("New request after repair.", "astra"));
            Assert.Equal("completed", (await WaitAsync(accepted, service, session)).Status);
        }
        Assert.Equal(2, factory.Requests.Count);
        await lab.StopAsync();
        await lab.ReopenAsync(factory);
        (GalateaHostService _, UserSessionHost reopened) = await SessionAsync(lab.Host);
        Assert.Equal(SessionExecutionPhase.Idle, reopened.Engine.InspectExecutionBoundary().Phase);
        var turns = reopened.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns;
        Assert.Equal(2, turns.Count);
        Assert.Equal(FreshAnswer, turns[0].TerminalAction.Message.GetFlattenedText());
        Assert.Equal(RetainedAnswer, turns[1].TerminalAction.Message.GetFlattenedText());
        OpenAIResponsesReasoningBlock retained = Assert.Single(
            turns[1].TerminalAction.Message.Blocks.OfType<OpenAIResponsesReasoningBlock>());
        Assert.Equal(retainedOrigin, retained.Origin);
        using JsonDocument native = JsonDocument.Parse(NativeJson);
        using JsonDocument persisted = JsonDocument.Parse(retained.RawItemJson);
        Assert.True(JsonElement.DeepEquals(native.RootElement, persisted.RootElement));
        Assert.Equal(2, factory.Requests.Count);
        await lab.CompleteAsync();
    }

    private static CompletionConnectionConfig Connection(string id, string model) => new(
        id, CodexSubscriptionCompletionClientFactory.ConnectionKind, model,
        CodexSubscriptionCompletionClientFactory.CompletionSurfaceId,
        CodexSubscriptionCompletionClientFactory.CanonicalBaseAddress);

    private static async Task<(GalateaHostService, UserSessionHost)> SessionAsync(GalateaTestHost host) {
        var service = host.Factory.Services.GetRequiredService<GalateaHostService>();
        return (service, await service.GetSessionAsync("alice", CancellationToken.None));
    }

    private static async Task<GalateaLiveTurn> WaitAsync(HttpResponseMessage response,
        GalateaHostService service, UserSessionHost session) {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var started = Assert.IsType<StartTurnResponseDto>(await response.Content.ReadFromJsonAsync<StartTurnResponseDto>());
        var turn = Assert.IsType<GalateaLiveTurn>(service.FindTurn(session, started.TurnId));
        await Assert.IsAssignableFrom<Task>(turn.RunTask).WaitAsync(TimeSpan.FromSeconds(15));
        return turn;
    }

    private static SortedDictionary<string, string> Snapshot(string path) => new(
        Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(path, file),
            file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))), StringComparer.Ordinal);

    private static async Task<CliResult> RunCliAsync(string workingDirectory, string[] arguments) {
        var start = new ProcessStartInfo("dotnet") {
            WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Atelia.SessionJournal.Cli.Program).Assembly.Location);
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        start.Environment["ATELIA_DEBUG_FILE_LEVEL"] = "Error";
        start.Environment["ATELIA_DEBUG_CONSOLE_LEVEL"] = "Error";
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("CLI did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        return new CliResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);

    private sealed class ScriptedCodexFactory : ICompletionClientFactory, ICodexSubscriptionCredentialProvider {
        private readonly CodexSubscriptionCredential _credential = CodexSubscriptionCredential.Create(
            "synthetic-lab-token", "synthetic-lab-account", null, null, 1);
        private int _credentialReads;
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal int CredentialReads => Volatile.Read(ref _credentialReads);

        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Assert.Equal(CodexSubscriptionCompletionClientFactory.ConnectionKind, connection.Kind);
            ConstructorInfo constructor = typeof(OpenAICodexResponsesClient).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(ICodexSubscriptionCredentialProvider), typeof(OpenAICodexResponsesClientOptions), typeof(HttpMessageHandler)], null)!;
            return (ICompletionClient)constructor.Invoke([this,
                new OpenAICodexResponsesClientOptions {
                    ExpectedAccountFingerprint = _credential.AccountFingerprint, ProductVersion = "galatea-lab"
                }, new Handler(Requests)]);
        }

        public ValueTask<CodexSubscriptionCredential> GetCredentialAsync(CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _credentialReads);
            return ValueTask.FromResult(_credential);
        }
    }

    private sealed class Handler(ConcurrentQueue<string> requests) : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            requests.Enqueue(body);
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            int number = requests.Count;
            Assert.InRange(number, 1, 2);
            Assert.Equal(number == 1 ? OldModel : NewModel, root.GetProperty("model").GetString());
            Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.DoesNotContain("fixture observation", body, StringComparison.Ordinal);
            if (number == 2) {
                JsonElement reasoning = Assert.Single(root.GetProperty("input").EnumerateArray(),
                    item => item.GetProperty("type").GetString() == "reasoning");
                using JsonDocument expected = JsonDocument.Parse(NativeJson);
                Assert.True(JsonElement.DeepEquals(expected.RootElement, reasoning));
                Assert.Contains(RetainedAnswer, body, StringComparison.Ordinal);
                Assert.Contains("New request after repair.", body, StringComparison.Ordinal);
            }
            string stream = number == 1 ? "data: {\"type\":\"response.output_item.done\",\"item\":" + NativeJson + "}\n\n" : "";
            stream += "data: " + JsonSerializer.Serialize(new {
                type = "response.output_text.delta", delta = number == 1 ? RetainedAnswer : FreshAnswer
            }) + "\n\ndata: {\"type\":\"response.completed\"}\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
