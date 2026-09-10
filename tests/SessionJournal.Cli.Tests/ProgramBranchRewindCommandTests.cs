using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.Cli;
using Xunit;
using Journal = Atelia.EventJournal.EventJournal;

namespace Atelia.SessionJournal.Cli.Tests;

[Collection(ConsoleSerialCollection.Name)]
public sealed class ProgramBranchRewindCommandTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "atelia-branch-rewind-tests", Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void DefaultPreviewIsBytePreservingAndUsesParentNotPhysicalOrder() {
        var fixture = Create();
        var before = Snapshot(fixture.Path);
        var result = Run(fixture);
        Assert.Equal(0, result.Code);
        using var report = JsonDocument.Parse(result.Output);
        Assert.Equal("preview", report.RootElement.GetProperty("status").GetString());
        Assert.Equal(EventAddressTextCodec.Format(fixture.Target),
            report.RootElement.GetProperty("targetHead").GetString());
        Assert.Equal(before, Snapshot(fixture.Path));
        Assert.DoesNotContain("PRIVATE", result.Output);
    }

    [Fact]
    public void ApplyMovesOnlySelectedRefOnceAndReopenIsIdle() {
        var fixture = Create();
        var before = Snapshot(fixture.Path);
        Assert.Equal(0, Run(fixture, Confirm(fixture)).Code);
        using (var journal = Journal.OpenReadOnlyExisting(fixture.Path)) {
            Assert.Equal(fixture.Target, journal.GetHead(fixture.Ref));
            Assert.Equal(fixture.Head, journal.GetHead(journal.OpenBranch("other").Unwrap()));
            using var orphan = journal.ReadEvent(fixture.Head).Unwrap();
            var log = journal.ReadReflog(fixture.Ref).Unwrap();
            Assert.Equal(fixture.MoveCount + 1, log.Count);
            Assert.Equal(RefMoveOperation.Move, log[^1].Operation);
            Assert.Equal(fixture.Head, log[^1].OldTarget);
            Assert.Equal(fixture.Target, log[^1].NewTarget);
        }
        using (var engine = SessionJournalEngine.OpenReadOnly(fixture.Path)) {
            Assert.Equal(SessionExecutionPhase.Idle, engine.InspectExecutionBoundary().Phase);
        }
        string[] changed = before.Keys.Where(key => before[key] != Snapshot(fixture.Path)[key]).ToArray();
        Assert.Single(changed);
        Assert.StartsWith($"refs/objects/{fixture.Ref.ToHexString()}/", changed[0]);
        Assert.Equal(before.Keys, Snapshot(fixture.Path).Keys);
        // The old exact confirmation must never pop a second event.
        var applied = Snapshot(fixture.Path);
        Assert.Equal(2, Run(fixture, Confirm(fixture)).Code);
        Assert.Equal(applied, Snapshot(fixture.Path));
    }

    [Theory]
    [InlineData("ref")]
    [InlineData("head")]
    [InlineData("target")]
    public void WrongConfirmationIsZeroWrite(string field) {
        var fixture = Create();
        var args = Confirm(fixture);
        string key = field switch {
            "ref" => "--confirm-ref", "head" => "--expected-head",
            _ => "--confirm-target"
        };
        args[Array.IndexOf(args, key) + 1] = field == "ref"
            ? "0000000000000001" : EventAddressTextCodec.Format(field == "head" ? fixture.Target : fixture.Head);
        var before = Snapshot(fixture.Path);
        Assert.Equal(2, Run(fixture, args).Code);
        Assert.Equal(before, Snapshot(fixture.Path));
    }

    [Theory]
    [InlineData("--steps", "0")]
    [InlineData("--steps", "10001")]
    [InlineData("--steps", "-1")]
    [InlineData("--apply", "true")]
    [InlineData("--confirm-ref", "0000000000000001")]
    [InlineData("--unknown", "true")]
    public void InvalidSyntaxIsZeroWrite(string key, string value) {
        var fixture = Create();
        var before = Snapshot(fixture.Path);
        Assert.Equal(1, Run(fixture, key, value).Code);
        Assert.Equal(before, Snapshot(fixture.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CannotCrossRoot(bool apply) {
        var fixture = Create();
        var before = Snapshot(fixture.Path);
        string[] args = ["--steps", "100", .. apply ? Confirm(fixture) : []];
        Assert.Equal(2, Run(fixture, args).Code);
        Assert.Equal(before, Snapshot(fixture.Path));
    }

    [Theory]
    [InlineData("events", false)]
    [InlineData("ref-op", false)]
    [InlineData("ref-object", false)]
    [InlineData("events", true)]
    [InlineData("ref-op", true)]
    [InlineData("ref-object", true)]
    public void CorruptActiveTailIsNeverRepaired(string domain, bool apply) {
        var fixture = Create();
        string file = domain switch {
            "events" => Directory.GetFiles(Path.Combine(fixture.Path, "events"), "*.rbf", SearchOption.AllDirectories).Single(),
            "ref-op" => Path.Combine(fixture.Path, "refs", "ref-op-log.rbf"),
            _ => Directory.GetFiles(Path.Combine(fixture.Path, "refs", "objects", fixture.Ref.ToHexString()), "*.rbf", SearchOption.AllDirectories).Single()
        };
        File.AppendAllBytes(file, new byte[] { 0, 0, 0, 0 });
        var before = Snapshot(fixture.Path);
        Assert.NotEqual(0, Run(fixture, apply ? Confirm(fixture) : []).Code);
        Assert.Equal(before, Snapshot(fixture.Path));
    }

    [Fact]
    public void MissingRepositoryIsNotCreated() {
        var fixture = new Fixture(Path.Combine(_root, "missing"), default, default, default, 0);
        Assert.Equal(2, Run(fixture).Code);
        Assert.False(Directory.Exists(fixture.Path));
    }

    [Fact]
    public void BusyOwnerRejectsApplyWithoutChanges() {
        var fixture = Create();
        var before = Snapshot(fixture.Path);
        using (var owner = SessionJournalEngine.Open(fixture.Path)) {
            Assert.Equal(2, Run(fixture, Confirm(fixture)).Code);
            Assert.Equal(fixture.Head, owner.ReadCurrentHead());
        }
        Assert.Equal(before, Snapshot(fixture.Path));
    }

    [Fact]
    public void OtherBranchRefCannotAuthorizeSelectedBranch() {
        var fixture = Create();
        string otherRef;
        using (var journal = Journal.OpenReadOnlyExisting(fixture.Path)) {
            otherRef = journal.OpenBranch("other").Unwrap().ToHexString();
        }
        string[] args = Confirm(fixture);
        args[Array.IndexOf(args, "--confirm-ref") + 1] = otherRef;
        var before = Snapshot(fixture.Path);
        Assert.Equal(2, Run(fixture, args).Code);
        Assert.Equal(before, Snapshot(fixture.Path));
    }

    [Fact]
    public void MultipleStepsStillAppendExactlyOneMove() {
        var fixture = Create();
        EventAddress head;
        using (var journal = Journal.OpenExisting(fixture.Path)) {
            head = journal.CommitToRef(fixture.Ref, fixture.Head, "PRIVATE"u8, 13).Unwrap().EventAddress;
        }
        fixture = fixture with { Head = head, MoveCount = fixture.MoveCount + 1 };
        Assert.Equal(0, Run(fixture, ["--steps", "2", .. Confirm(fixture)]).Code);
        using var reopened = Journal.OpenReadOnlyExisting(fixture.Path);
        Assert.Equal(fixture.Target, reopened.GetHead(fixture.Ref));
        Assert.Equal(fixture.MoveCount + 1, reopened.ReadReflog(fixture.Ref).Unwrap().Count);
    }

    private Fixture Create() {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        EventAddress target;
        EventAddress head;
        RefId refId;
        using (var engine = SessionJournalEngine.Create(path, new SessionCreateOptions("model", "PRIVATE", "surface"))) {
            refId = engine.BranchRefId;
            target = engine.ReadCurrentHead()!.Value;
            head = engine.AppendObservation("PRIVATE");
        }
        using var journal = Journal.OpenExisting(path);
        journal.ForkBranch("other", refId, head).Unwrap();
        journal.AppendEventFrame(head, "PRIVATE sibling"u8).Unwrap();
        return new Fixture(path, refId, target, head, journal.ReadReflog(refId).Unwrap().Count);
    }

    private static string[] Confirm(Fixture fixture) => [
        "--apply", "--accept-external-effects",
        "--confirm-ref", fixture.Ref.ToHexString(),
        "--expected-head", EventAddressTextCodec.Format(fixture.Head),
        "--confirm-target", EventAddressTextCodec.Format(fixture.Target)
    ];

    private static (int Code, string Output) Run(Fixture fixture, params string[] extra) {
        TextWriter original = Console.Out;
        using var output = new StringWriter();
        try {
            Console.SetOut(output);
            int code = Program.MainCore(
                ["rewind-branch", "--input", fixture.Path, "--branch", "main", .. extra],
                new ForbiddenFactory()
            );
            return (code, output.ToString());
        }
        finally { Console.SetOut(original); }
    }

    private static SortedDictionary<string, string> Snapshot(string path) => new(
        Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(path, file),
            file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))
        ), StringComparer.Ordinal
    );

    public void Dispose() {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    private sealed record Fixture(string Path, RefId Ref, EventAddress Target, EventAddress Head, int MoveCount);
    private sealed class ForbiddenFactory : ICompletionClientFactory {
        public ICompletionClient Create(CompletionConnectionConfig connection) =>
            throw new Xunit.Sdk.XunitException("Rewind must never create a provider client.");
    }
}
