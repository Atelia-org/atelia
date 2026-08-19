using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.Tests;

internal static class PreparedV6Fixture {
    internal sealed record MixedWriterRepository(
        string Path,
        ImmutableArray<EventAddress> PreparedAddresses,
        ImmutableArray<int> PreparedBodySchemaVersions
    );

    internal sealed record PreparedRawRangeEvidence(
        CompletionRequestPreparedBody Manifest,
        int BodySchemaVersion,
        EventAddress RawEndInclusive,
        ImmutableArray<SessionRawRangeHashEntry> Entries
    );

    public static async Task<MixedWriterRepository>
        CreateMixedWriterRepositoryAsync(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var client = new MixedWriterCompletionClient();
        var candidateSource = new TestContextCandidateSource();
        var supplementalSource = new TestSupplementalContextSource(
            new SessionSupplementalContextSelection.Selected(
                "mixed-writer supplemental"
            )
        );
        SessionRuntime v5Runtime = new(
            client,
            CompletionTarget: new SessionCompletionTargetIdentity(
                "mixed-writer",
                "test",
                "mixed-writer-v1",
                "mixed-writer-adapter-v1"
            ),
            ContextCandidateSource: candidateSource
        );
        using (var engine = SessionJournalTestRuntime.Attach(
            SessionJournalEngine.Create(
                path,
                new SessionCreateOptions(
                    "model-A",
                    "system-A",
                    "surface-A"
                )
            ),
            v5Runtime
        )) {
            await CoherentArtifactSetTestFixture
                .ActivateAtCurrentHeadAsync(
                    path,
                    engine,
                    candidateSource,
                    fixtureId: "mixed-prepared-reader"
                );

            _ = await engine.SendAsync(
                "v5 before v6",
                CancellationToken.None
            );
            engine.UseRuntime(v5Runtime with {
                SupplementalContextSource = supplementalSource
            });
            _ = await engine.SendAsync(
                "v6 between v5 entries",
                CancellationToken.None
            );
            engine.UseRuntime(v5Runtime);
            _ = await engine.SendAsync(
                "v5 after v6",
                CancellationToken.None
            );
        }

        if (client.CallCount != 3
            || supplementalSource.CallCount != 1) {
            throw new InvalidDataException(
                "Mixed Prepared writer did not exercise the expected runtime sequence."
            );
        }

        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(
            SessionJournalDefaults.MainBranchName
        ).Unwrap();
        EventAddress head = journal.GetHead(main)
            ?? throw new InvalidDataException(
                "Mixed Prepared writer has no main head."
            );
        ImmutableArray<EventAddress> preparedAddresses = [
            .. journal.ReadChronologicalChain(head, checkedRead: true)
                .Unwrap()
                .Where(address =>
                    journal.ReadEventHeaderPreview(address)
                        .Unwrap()
                        .OpaqueEventKind
                    == (uint)SessionEventKind.CompletionRequestPrepared
                )
        ];
        var versions = ImmutableArray.CreateBuilder<int>(
            preparedAddresses.Length
        );
        foreach (EventAddress address in preparedAddresses) {
            using EventFrame frame = journal.ReadEvent(address).Unwrap();
            _ = SessionEventCodec.Decode(
                SessionEventKind.CompletionRequestPrepared,
                frame.Payload,
                out int bodySchemaVersion
            );
            versions.Add(bodySchemaVersion);
        }
        return new MixedWriterRepository(
            path,
            preparedAddresses,
            versions.MoveToImmutable()
        );
    }

    public static PreparedRawRangeEvidence ReadPreparedRawRange(
        string path,
        EventAddress preparedAddress
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        using EventFrame preparedFrame =
            journal.ReadEvent(preparedAddress).Unwrap();
        if (preparedFrame.Header.OpaqueEventKind
            != (uint)SessionEventKind.CompletionRequestPrepared) {
            throw new InvalidDataException(
                "Raw-range evidence address is not a Prepared event."
            );
        }
        var manifest = (CompletionRequestPreparedBody)
            SessionEventCodec.Decode(
                SessionEventKind.CompletionRequestPrepared,
                preparedFrame.Payload,
                out int preparedBodySchemaVersion
            );
        EventAddress rawEndInclusive = preparedFrame.Header.Parent
            ?? throw new InvalidDataException(
                "Prepared raw-range evidence has no raw end."
            );
        IReadOnlyList<EventAddress> chain = journal
            .ReadChronologicalChain(rawEndInclusive, checkedRead: true)
            .Unwrap();
        int rawStartIndex = -1;
        for (int index = 0; index < chain.Count; index++) {
            if (chain[index]
                == manifest.Plan.RawStartExclusive) {
                rawStartIndex = index;
                break;
            }
        }
        if (rawStartIndex < 0) {
            throw new InvalidDataException(
                "Prepared raw start is not in its raw-end lineage."
            );
        }

        var entries = ImmutableArray.CreateBuilder<
            SessionRawRangeHashEntry
        >(chain.Count - rawStartIndex - 1);
        for (int index = rawStartIndex + 1;
             index < chain.Count;
             index++) {
            EventAddress address = chain[index];
            using EventFrame frame = journal.ReadEvent(address).Unwrap();
            var kind = (SessionEventKind)
                frame.Header.OpaqueEventKind;
            _ = SessionEventCodec.Decode(
                kind,
                frame.Payload,
                out int bodySchemaVersion
            );
            entries.Add(new SessionRawRangeHashEntry(
                address,
                frame.Header.Parent,
                frame.Header.OpaqueEventKind,
                bodySchemaVersion,
                SessionRequestCanonicalizer.Sha256Hex(frame.Payload)
            ));
        }
        return new PreparedRawRangeEvidence(
            manifest,
            preparedBodySchemaVersion,
            rawEndInclusive,
            entries.MoveToImmutable()
        );
    }

    public static string ComputeRepositoryTreeDigest(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string[] files = [
            .. Directory.EnumerateFiles(
                    fullPath,
                    "*",
                    SearchOption.AllDirectories
                )
                .OrderBy(
                    file => Path.GetRelativePath(fullPath, file),
                    StringComparer.Ordinal
                )
        ];
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        AppendDigestField(
            hash,
            "atelia.session-journal.test-tree-digest.v1"u8
        );
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(count, files.Length);
        hash.AppendData(count);
        var strictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true
        );
        foreach (string file in files) {
            string relativePath = Path.GetRelativePath(fullPath, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            AppendDigestField(
                hash,
                strictUtf8.GetBytes(relativePath)
            );
            AppendDigestField(hash, File.ReadAllBytes(file));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static CompletionRequestPreparedBody Create(
        string? selectedObservationContent = "supplemental detail",
        ImmutableArray<SessionRequestContextInput>? recapInputs = null
    ) {
        CompletionRequestPreparedBody v5 = PreparedV5Fixture.Create(
            "correlation-01",
            "observation",
            Address(1),
            Address(2),
            Address(3),
            Address(4),
            "model-A",
            ImmutableArray<ToolDefinition>.Empty,
            toolRuntimeIdentity: null
        );
        ImmutableArray<SessionRequestContextInput> recap =
            recapInputs ?? v5.Plan.ExactContextInputs;
        SessionRequestContextInput terminal = selectedObservationContent is null
            ? SessionSupplementalContextRecipe.CreateNoMatchTerminalInput()
            : SessionSupplementalContextRecipe.CreateSelectedTerminalInput(
                selectedObservationContent
            );
        return v5 with {
            Plan = v5.Plan with {
                ExactContextInputs = [.. recap, terminal]
            },
            Recipe = v5.Recipe with {
                RecipeId = SessionSupplementalContextRecipe.RecipeId
            }
        };
    }

    public static CompletionRequestPreparedBody Upgrade(
        CompletionRequestPreparedBody v5,
        string? selectedObservationContent
    ) {
        ArgumentNullException.ThrowIfNull(v5);
        SessionRequestContextInput terminal = selectedObservationContent is null
            ? SessionSupplementalContextRecipe.CreateNoMatchTerminalInput()
            : SessionSupplementalContextRecipe.CreateSelectedTerminalInput(
                selectedObservationContent
            );
        return v5 with {
            Plan = v5.Plan with {
                ExactContextInputs = [.. v5.Plan.ExactContextInputs, terminal]
            },
            Recipe = v5.Recipe with {
                RecipeId = SessionSupplementalContextRecipe.RecipeId
            }
        };
    }

    private static EventAddress Address(int ticket)
        => EventAddressTextCodec.Parse(
            $"ej1:{ticket:X16}0000000100000000".ToLowerInvariant()
        );

    private static void AppendDigestField(
        IncrementalHash hash,
        ReadOnlySpan<byte> value
    ) {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private sealed class MixedWriterCompletionClient
        : ICompletionClient {
        public string Name => "mixed-writer";

        public string ApiSpecId => "mixed-writer-v1";

        public int CallCount { get; private set; }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text($"mixed-answer-{CallCount}")
                ]),
                new CompletionDescriptor(
                    Name,
                    ApiSpecId,
                    request.ModelId
                )
            ));
        }
    }
}
