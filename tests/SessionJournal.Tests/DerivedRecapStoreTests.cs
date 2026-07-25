using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.Derived;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class DerivedRecapStoreTests : IDisposable {
    private readonly List<string> _tempDirectories = new();

    public void Dispose() {
        foreach (string path in _tempDirectories) {
            try {
                if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
            }
            catch {
                // Best-effort cleanup for temp test directories.
            }
        }
    }

    [Fact]
    public async Task WriteProduced_CreatesArtifactAndLatestIndex_ThenReopenReadsLatest() {
        var addresses = CreateAddresses();
        string repoPath = NewRepoPath();
        var store = DerivedRecapStore.Open(repoPath);

        var artifact = await store.WriteProducedAsync(CreateRequest(addresses, summary: "summary v1"));

        Assert.True(File.Exists(Path.Combine(store.ArtifactsDirectory, $"{artifact.ArtifactId}.json")));
        Assert.True(File.Exists(store.LatestIndexPath));
        Assert.Equal("summary v1", artifact.Content);
        Assert.True(artifact.MemoryPack.TryGetBlock(artifact.Target, out var block));
        Assert.Equal("summary v1", block.Text);

        var reopened = DerivedRecapStore.Open(repoPath);
        var latest = await reopened.TryReadLatestAsync(artifact.LineageKey);

        Assert.NotNull(latest);
        Assert.Equal(artifact.ArtifactId, latest.ArtifactId);
        Assert.Equal("summary v1", latest.Content);
    }

    [Fact]
    public async Task RebuildLatestIndex_AfterIndexDeleted_SelectsPreviousArtifactSuccessor() {
        var addresses = CreateAddresses();
        string repoPath = NewRepoPath();
        var store = DerivedRecapStore.Open(repoPath);

        var first = await store.WriteProducedAsync(CreateRequest(addresses, summary: "summary v1"));
        var second = await store.WriteProducedAsync(CreateRequest(
            addresses with {
                SourceStartExclusive = addresses.SourceEndInclusive,
                SourceEndInclusive = addresses.AnchorRawEvent,
                AnchorRawEvent = addresses.AnchorRawEvent
            },
            summary: "summary v2",
            previousArtifact: first.ArtifactId
        ));

        Directory.Delete(store.IndexesDirectory, recursive: true);

        var latest = await store.TryReadLatestAsync(first.LineageKey);

        Assert.NotNull(latest);
        Assert.Equal(second.ArtifactId, latest.ArtifactId);
        Assert.True(File.Exists(store.LatestIndexPath));
    }

    [Fact]
    public async Task CorruptIndex_DoesNotLoseArtifacts_AndRebuilds() {
        var addresses = CreateAddresses();
        string repoPath = NewRepoPath();
        var store = DerivedRecapStore.Open(repoPath);
        var artifact = await store.WriteProducedAsync(CreateRequest(addresses, summary: "summary v1"));
        await File.WriteAllTextAsync(store.LatestIndexPath, "{not json");

        var latest = await store.TryReadLatestAsync(artifact.LineageKey);

        Assert.NotNull(latest);
        Assert.Equal(artifact.ArtifactId, latest.ArtifactId);
    }

    [Fact]
    public async Task WriteProduced_SameIdentityWithDifferentCreatedUtc_ReusesArtifactId() {
        var addresses = CreateAddresses();
        string repoPath = NewRepoPath();
        var store = DerivedRecapStore.Open(repoPath);
        var request = CreateRequest(addresses, summary: "summary v1") with { CreatedUtc = null };

        var first = await store.WriteProducedAsync(request);
        await Task.Delay(5);
        var second = await store.WriteProducedAsync(request);

        Assert.Equal(first.ArtifactId, second.ArtifactId);
        Assert.Single(Directory.EnumerateFiles(store.ArtifactsDirectory, "*.json"));
    }

    [Fact]
    public async Task CorruptArtifact_IsSkippedDuringRebuild() {
        var addresses = CreateAddresses();
        string repoPath = NewRepoPath();
        var store = DerivedRecapStore.Open(repoPath);
        var artifact = await store.WriteProducedAsync(CreateRequest(addresses, summary: "summary v1"));
        Directory.CreateDirectory(store.ArtifactsDirectory);
        await File.WriteAllTextAsync(Path.Combine(store.ArtifactsDirectory, "broken.json"), "{not json");
        Directory.Delete(store.IndexesDirectory, recursive: true);

        var index = await store.RebuildLatestIndexAsync();

        Assert.Single(index.Items);
        Assert.Contains(index.Items, pair => pair.Value.ArtifactId == artifact.ArtifactId);
    }

    [Fact]
    public async Task ContentTargetMismatch_IsSkippedDuringRebuild() {
        var addresses = CreateAddresses();
        string repoPath = NewRepoPath();
        var store = DerivedRecapStore.Open(repoPath);
        var artifact = await store.WriteProducedAsync(CreateRequest(addresses, summary: "summary v1"));
        string artifactPath = Path.Combine(store.ArtifactsDirectory, $"{artifact.ArtifactId}.json");
        string json = await File.ReadAllTextAsync(artifactPath);
        var root = JsonNode.Parse(json)!.AsObject();
        root["memoryPack"]!["observation"]![0]!["text"] = "tampered";
        json = root.ToJsonString();

        await File.WriteAllTextAsync(artifactPath, json);

        var index = await store.RebuildLatestIndexAsync();

        Assert.Empty(index.Items);
        Assert.Null(await store.TryReadArtifactAsync(artifact.ArtifactId));
    }

    [Fact]
    public async Task MissingContentAndMemoryPackArtifact_IsSkippedDuringRebuild() {
        string repoPath = NewRepoPath();
        var store = DerivedRecapStore.Open(repoPath);
        Directory.CreateDirectory(store.ArtifactsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(store.ArtifactsDirectory, "missing-fields.json"),
            """
            {
              "schema": "atelia.session-journal.derived-recap.v1",
              "artifactId": "missing-fields",
              "artifactKind": "rolling-summary",
              "lineageKey": "rolling-summary|profile:rolling-summary|target:observation/session.rolling-summary",
              "profileId": "rolling-summary",
              "producer": "tests",
              "producerFingerprint": "sha256:test",
              "status": "produced"
            }
            """
        );

        var index = await store.RebuildLatestIndexAsync();

        Assert.Empty(index.Items);
    }

    [Fact]
    public async Task TryReadLatestAsync_RebuildsWhenIndexContainsInvalidEntry() {
        var addresses = CreateAddresses();
        string repoPath = NewRepoPath();
        var store = DerivedRecapStore.Open(repoPath);
        var artifact = await store.WriteProducedAsync(CreateRequest(addresses, summary: "summary v1"));
        await File.WriteAllTextAsync(
            store.LatestIndexPath,
            $$"""
            {
              "schema": "atelia.session-journal.derived-recap.latest-index.v1",
              "rebuiltUtc": "2026-07-25T00:00:00Z",
              "items": {
                "{{artifact.LineageKey.Value}}": {
                  "artifactId": "../bad",
                  "artifactPath": "../artifacts/../bad.json",
                  "sourceRawHead": "ej1:00000000000000010000000100000000",
                  "anchorRawEvent": "ej1:00000000000000010000000100000000",
                  "sourceEndInclusive": "ej1:00000000000000010000000100000000",
                  "createdUtc": "2026-07-25T00:00:00Z",
                  "producerFingerprint": "sha256:test"
                }
              }
            }
            """
        );

        var latest = await store.TryReadLatestAsync(artifact.LineageKey);

        Assert.NotNull(latest);
        Assert.Equal(artifact.ArtifactId, latest.ArtifactId);
    }

    [Fact]
    public async Task MemoryPackSchemaMismatchArtifact_IsSkippedDuringRebuild() {
        var addresses = CreateAddresses();
        string repoPath = NewRepoPath();
        var store = DerivedRecapStore.Open(repoPath);
        var artifact = await store.WriteProducedAsync(CreateRequest(addresses, summary: "summary v1"));
        string artifactPath = Path.Combine(store.ArtifactsDirectory, $"{artifact.ArtifactId}.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(artifactPath))!.AsObject();
        root["memoryPack"]!["schema"] = "atelia.session-journal.memory-pack.snapshot.v0";
        await File.WriteAllTextAsync(artifactPath, root.ToJsonString());

        var index = await store.RebuildLatestIndexAsync();

        Assert.Empty(index.Items);
        Assert.Null(await store.TryReadArtifactAsync(artifact.ArtifactId));
    }

    [Fact]
    public async Task RebuildLatestIndex_DuplicateArtifactIdFiles_DoesNotThrow() {
        var addresses = CreateAddresses();
        string repoPath = NewRepoPath();
        var store = DerivedRecapStore.Open(repoPath);
        var artifact = await store.WriteProducedAsync(CreateRequest(addresses, summary: "summary v1"));
        string artifactPath = Path.Combine(store.ArtifactsDirectory, $"{artifact.ArtifactId}.json");
        File.Copy(artifactPath, Path.Combine(store.ArtifactsDirectory, "copied-artifact.json"));
        Directory.Delete(store.IndexesDirectory, recursive: true);

        var index = await store.RebuildLatestIndexAsync();

        Assert.Single(index.Items);
        Assert.Contains(index.Items, pair => pair.Value.ArtifactId == artifact.ArtifactId);
    }

    [Fact]
    public async Task DuplicateMemoryPackBlockKeyArtifact_IsSkipped() {
        var addresses = CreateAddresses();
        string repoPath = NewRepoPath();
        var store = DerivedRecapStore.Open(repoPath);
        var artifact = await store.WriteProducedAsync(CreateRequest(addresses, summary: "summary v1"));
        string artifactPath = Path.Combine(store.ArtifactsDirectory, $"{artifact.ArtifactId}.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(artifactPath))!.AsObject();
        var observation = root["memoryPack"]!["observation"]!.AsArray();
        observation.Add(new JsonObject {
            ["key"] = "session.rolling-summary",
            ["text"] = "duplicate"
        });
        await File.WriteAllTextAsync(artifactPath, root.ToJsonString());

        var index = await store.RebuildLatestIndexAsync();

        Assert.Empty(index.Items);
        Assert.Null(await store.TryReadArtifactAsync(artifact.ArtifactId));
    }

    [Fact]
    public void EventAddressTextCodec_Roundtrip_AndRejectsInvalidText() {
        EventAddress address = CreateAddresses().SourceEndInclusive;

        string text = EventAddressTextCodec.Format(address);
        EventAddress parsed = EventAddressTextCodec.Parse(text);

        Assert.Equal(address, parsed);
        Assert.Equal(36, text.Length);
        Assert.StartsWith("ej1:", text);
        Assert.Equal(text.ToLowerInvariant(), text);
        Assert.Null(EventAddressTextCodec.ParseNullable(null));
        Assert.False(EventAddressTextCodec.TryParse("ej1:1234", out _));
        Assert.False(EventAddressTextCodec.TryParse("ej1:zzzzzzzzzzzzzzzz0000000100000000", out _));
        Assert.False(EventAddressTextCodec.TryParse("ej1:" + text[4..].ToUpperInvariant(), out _));
        Assert.False(EventAddressTextCodec.TryParse("ej1:00000000000000000000000100000000", out _));
        Assert.False(EventAddressTextCodec.TryParse("ej1:00000000000000010000000000000000", out _));
    }

    [Fact]
    public async Task WriteProduced_RejectsDefaultEventAddress() {
        var addresses = CreateAddresses() with { SourceEndInclusive = default };
        var store = DerivedRecapStore.Open(NewRepoPath());

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await store.WriteProducedAsync(CreateRequest(addresses, summary: "summary v1"))
        );
    }

    [Fact]
    public async Task DerivedStore_DoesNotModifyRawSessionJournal() {
        string repoPath = NewRepoPath();
        EventAddress sourceRawHead;
        IReadOnlyList<IHistoryMessage> contextBefore;
        using (var engine = SessionJournalEngine.Create(
            repoPath,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            engine.AppendObservation("hello");
            engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("answer")]),
                new CompletionDescriptor("fake-provider", "fake-api-v1", "model-A")
            );
            var projection = engine.Project();
            sourceRawHead = projection.Head!.Value;
            contextBefore = projection.Context;
        }

        var store = DerivedRecapStore.Open(repoPath);
        await store.WriteProducedAsync(CreateRequest(
            new AddressSet(
                sourceRawHead,
                SourceStartExclusive: null,
                SourceEndInclusive: sourceRawHead,
                AnchorRawEvent: sourceRawHead,
                GoverningRuntimeConfigSetup: sourceRawHead,
                GoverningSystemPromptSetup: sourceRawHead
            ),
            summary: "summary v1"
        ));

        using var reopened = SessionJournalEngine.Open(repoPath);
        var projectionAfter = reopened.Project();

        Assert.Equal(sourceRawHead, projectionAfter.Head);
        Assert.Equal(contextBefore.Count, projectionAfter.Context.Count);
        Assert.Equal("hello", Assert.IsType<ObservationMessage>(projectionAfter.Context[0]).Content);
        Assert.Equal("answer", Assert.IsType<ActionMessage>(projectionAfter.Context[1]).GetFlattenedText());
    }

    private DerivedRecapWriteRequest CreateRequest(
        AddressSet addresses,
        string summary,
        string? previousArtifact = null
    ) {
        var target = new MemoryPackBlockPath(MemoryPackCarrier.Observation, "session.rolling-summary");
        var memoryPack = new MemoryPack();
        memoryPack.Observation.Add(target.BlockKey, new MemoryPackBlock(summary));
        return new DerivedRecapWriteRequest(
            ArtifactKind: DerivedRecapArtifactKinds.RollingSummary,
            ProfileId: "rolling-summary",
            Producer: "tests",
            ProducerFingerprint: "sha256:test",
            SourceRawHead: addresses.SourceRawHead,
            SourceStartExclusive: addresses.SourceStartExclusive,
            SourceEndInclusive: addresses.SourceEndInclusive,
            AnchorRawEvent: addresses.AnchorRawEvent,
            GoverningRuntimeConfigSetup: addresses.GoverningRuntimeConfigSetup,
            GoverningSystemPromptSetup: addresses.GoverningSystemPromptSetup,
            PreviousArtifact: previousArtifact,
            Target: target,
            MemoryPack: memoryPack,
            Invocation: new CompletionDescriptor("scripted", "openai-chat-v1", "model-a"),
            InputArtifacts: previousArtifact is null ? [] : [previousArtifact],
            CallLogPaths: ["calls/0001.json"],
            CreatedUtc: DateTimeOffset.Parse("2026-07-25T00:00:00Z")
        );
    }

    private AddressSet CreateAddresses() {
        string repoPath = NewRepoPath();
        using var journal = EventJournal.EventJournal.CreateNew(repoPath);
        journal.CreateBranch("main", startPoint: null).Unwrap();
        EventAddress runtime = journal.CommitToRef("main", null, [1], opaqueEventKind: 1).Unwrap().EventAddress;
        EventAddress prompt = journal.CommitToRef("main", runtime, [2], opaqueEventKind: 2).Unwrap().EventAddress;
        EventAddress sourceEnd = journal.CommitToRef("main", prompt, [3], opaqueEventKind: 4).Unwrap().EventAddress;
        EventAddress anchor = journal.CommitToRef("main", sourceEnd, [4], opaqueEventKind: 5).Unwrap().EventAddress;
        return new AddressSet(
            SourceRawHead: anchor,
            SourceStartExclusive: null,
            SourceEndInclusive: sourceEnd,
            AnchorRawEvent: anchor,
            GoverningRuntimeConfigSetup: runtime,
            GoverningSystemPromptSetup: prompt
        );
    }

    private string NewRepoPath() {
        string path = Path.Combine(Path.GetTempPath(), "atelia-derived-recap-store-tests", Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(path);
        return path;
    }

    private sealed record AddressSet(
        EventAddress SourceRawHead,
        EventAddress? SourceStartExclusive,
        EventAddress SourceEndInclusive,
        EventAddress AnchorRawEvent,
        EventAddress GoverningRuntimeConfigSetup,
        EventAddress GoverningSystemPromptSetup
    );
}
