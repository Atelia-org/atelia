using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.MemoPod;
using MemoPodAggregate = Atelia.SessionJournal.MemoPod.MemoPod;

namespace Atelia.SessionJournal.MemoPod.DebugApp;

internal static class Program {
    private const string OutputSchema = "atelia.memo-pod.debug-app.v1";
    private const string FakeModelId = "memo-pod-deterministic-fake-v1";

    private static readonly MemoRecallOptions RecallOptions = new(
        MemoPodLimits.MaximumRecallResultCount,
        maxTokens: 256,
        MemoPodLimits.MaximumRenderedPromptUtf8Bytes,
        MemoPodLimits.MaximumActiveExactTextUtf8Bytes
    );

    private static readonly IReadOnlySet<string> CreateSingleKeys = Set(
        "root",
        "pod",
        "topic-file"
    );
    private static readonly IReadOnlySet<string> CreateRepeatedKeys = Set(
        "memo-file"
    );
    private static readonly IReadOnlySet<string> EditSingleKeys = Set(
        "root",
        "pod"
    );
    private static readonly IReadOnlySet<string> EditRepeatedKeys = Set(
        "remove",
        "memo-file"
    );
    private static readonly IReadOnlySet<string> InspectSingleKeys = Set(
        "root",
        "pod"
    );
    private static readonly IReadOnlySet<string> GetSingleKeys = Set(
        "root",
        "pod",
        "memo"
    );
    private static readonly IReadOnlySet<string> FakeRecallSingleKeys = Set(
        "root",
        "pod",
        "query-file"
    );
    private static readonly IReadOnlySet<string> FakeRecallRepeatedKeys = Set(
        "fake-return-id"
    );
    public static async Task<int> Main(string[] args) {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) => {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try {
            return await MainCoreAsync(
                args,
                Console.Out,
                Console.Error,
                fakeClientFactory: null,
                cancellation.Token
            ).ConfigureAwait(false);
        }
        finally {
            Console.CancelKeyPress -= handler;
        }
    }

    internal static async Task<int> MainCoreAsync(
        string[] args,
        TextWriter standardOutput,
        TextWriter standardError,
        Func<IReadOnlyList<string>, ICompletionClient>?
            fakeClientFactory = null,
        CancellationToken cancellationToken = default,
        LiveMemoRecallServices? liveServices = null
    ) {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        fakeClientFactory ??= static ids =>
            new DeterministicMemoRecallClient(ids);

        try {
            if (args is ["-h"] or ["--help"]) {
                await PrintHelpAsync(standardOutput).ConfigureAwait(false);
                return 0;
            }

            OperatorArguments parsed = OperatorArguments.Parse(args);
            return parsed.Command switch {
                "create" => await RunCreateAsync(
                    parsed,
                    standardOutput,
                    cancellationToken
                ).ConfigureAwait(false),
                "edit" => await RunEditAsync(
                    parsed,
                    standardOutput,
                    cancellationToken
                ).ConfigureAwait(false),
                "inspect" => await RunInspectAsync(
                    parsed,
                    standardOutput
                ).ConfigureAwait(false),
                "get" => await RunGetAsync(
                    parsed,
                    standardOutput
                ).ConfigureAwait(false),
                "recall" => await RunRecallAsync(
                    parsed,
                    standardOutput,
                    fakeClientFactory,
                    cancellationToken,
                    liveServices
                ).ConfigureAwait(false),
                _ => throw new OperatorSyntaxException()
            };
        }
        catch (OperatorSyntaxException) {
            return await FailAsync(
                standardError,
                "syntax",
                exitCode: 1
            ).ConfigureAwait(false);
        }
        catch (OperatorInputException) {
            return await FailAsync(
                standardError,
                "input",
                exitCode: 1
            ).ConfigureAwait(false);
        }
        catch (LiveMemoRecallConfigurationException) {
            return await FailAsync(
                standardError,
                "live-config",
                exitCode: 1
            ).ConfigureAwait(false);
        }
        catch (LiveMemoRecallSafetyException) {
            return await FailAsync(
                standardError,
                "live-safety",
                exitCode: 2
            ).ConfigureAwait(false);
        }
        catch (MemoRecallException exception) {
            string code = exception.FailureKind switch {
                MemoRecallFailureKind.LocalLimitExceeded =>
                    "recall-local-limit",
                MemoRecallFailureKind.InvalidModelOutput =>
                    "recall-invalid-output",
                MemoRecallFailureKind.ProviderFailure =>
                    "recall-provider",
                _ => "recall-failure"
            };
            return await FailAsync(
                standardError,
                code,
                exitCode: 2
            ).ConfigureAwait(false);
        }
        catch (OperationCanceledException) {
            return await FailAsync(
                standardError,
                "cancelled",
                exitCode: 2
            ).ConfigureAwait(false);
        }
        catch (MemoPodPersistenceException) {
            return await FailAsync(
                standardError,
                "persistence",
                exitCode: 2
            ).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
            or FormatException
            or KeyNotFoundException) {
            return await FailAsync(
                standardError,
                "input",
                exitCode: 1
            ).ConfigureAwait(false);
        }
        catch (InvalidOperationException) {
            return await FailAsync(
                standardError,
                "lifecycle",
                exitCode: 2
            ).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception)) {
            return await FailAsync(
                standardError,
                "internal",
                exitCode: 2
            ).ConfigureAwait(false);
        }
    }

    private static async Task<int> RunCreateAsync(
        OperatorArguments arguments,
        TextWriter output,
        CancellationToken cancellationToken
    ) {
        arguments.RequireShape(CreateSingleKeys, CreateRepeatedKeys);
        string root = arguments.RequireSingle("root");
        MemoPodId podId = MemoPodId.Parse(
            arguments.RequireSingle("pod")
        );
        string topic = StrictUtf8File.Read(
            arguments.RequireSingle("topic-file"),
            MemoPodLimits.MaximumTopicUtf8Bytes
        );
        string[] exactTexts = arguments.GetRepeated("memo-file")
            .Select(path => StrictUtf8File.Read(
                path,
                MemoPodLimits.MaximumMemoExactTextUtf8Bytes
            ))
            .ToArray();

        MemoPodAggregate pod = MemoPodAggregate.Create(root, podId, topic);
        MemoId[] committedIds = exactTexts.Select(pod.Append).ToArray();
        await pod.FreezeAsync(cancellationToken).ConfigureAwait(false);
        await WriteMutationReportAsync(
            output,
            "create",
            pod,
            committedIds
        ).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunEditAsync(
        OperatorArguments arguments,
        TextWriter output,
        CancellationToken cancellationToken
    ) {
        arguments.RequireShape(EditSingleKeys, EditRepeatedKeys);
        string root = arguments.RequireSingle("root");
        MemoPodId podId = MemoPodId.Parse(
            arguments.RequireSingle("pod")
        );
        MemoId[] removals = arguments.GetRepeated("remove")
            .Select(MemoId.Parse)
            .ToArray();
        string[] exactTexts = arguments.GetRepeated("memo-file")
            .Select(path => StrictUtf8File.Read(
                path,
                MemoPodLimits.MaximumMemoExactTextUtf8Bytes
            ))
            .ToArray();
        if (removals.Length == 0 && exactTexts.Length == 0) {
            throw new OperatorSyntaxException();
        }

        MemoPodAggregate pod = MemoPodAggregate.Open(root, podId);
        pod.ResumeEditing();
        foreach (MemoId removal in removals) {
            pod.Remove(removal);
        }
        MemoId[] committedIds = exactTexts.Select(pod.Append).ToArray();
        await pod.FreezeAsync(cancellationToken).ConfigureAwait(false);
        await WriteMutationReportAsync(
            output,
            "edit",
            pod,
            committedIds
        ).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunInspectAsync(
        OperatorArguments arguments,
        TextWriter output
    ) {
        arguments.RequireShape(InspectSingleKeys, Set());
        MemoPodAggregate pod = MemoPodAggregate.Open(
            arguments.RequireSingle("root"),
            MemoPodId.Parse(arguments.RequireSingle("pod"))
        );
        string[] activeIds = pod.List()
            .Select(static memo => memo.Id.Value)
            .ToArray();
        await WriteJsonLineAsync(output, new {
            schema = OutputSchema,
            command = "inspect",
            podId = pod.PodId.Value,
            phase = pod.Phase.ToString(),
            activeCount = activeIds.Length,
            activeIds
        }).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunGetAsync(
        OperatorArguments arguments,
        TextWriter output
    ) {
        arguments.RequireShape(GetSingleKeys, Set());
        MemoPodAggregate pod = MemoPodAggregate.Open(
            arguments.RequireSingle("root"),
            MemoPodId.Parse(arguments.RequireSingle("pod"))
        );
        Memo memo = pod.Get(MemoId.Parse(
            arguments.RequireSingle("memo")
        ));
        await output.WriteAsync(memo.ExactText).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunRecallAsync(
        OperatorArguments arguments,
        TextWriter output,
        Func<IReadOnlyList<string>, ICompletionClient> fakeClientFactory,
        CancellationToken cancellationToken,
        LiveMemoRecallServices? liveServices
    ) {
        if (arguments.Contains("live")) {
            if (!string.Equals(
                    arguments.RequireSingle("live"),
                    "true",
                    StringComparison.Ordinal
                )) {
                throw new OperatorSyntaxException();
            }
            return await LiveMemoRecallRunner.RunAsync(
                arguments,
                output,
                liveServices,
                cancellationToken
            ).ConfigureAwait(false);
        }

        arguments.RequireShape(
            FakeRecallSingleKeys,
            FakeRecallRepeatedKeys
        );
        string query = StrictUtf8File.Read(
            arguments.RequireSingle("query-file"),
            MemoPodLimits.MaximumRecallQueryUtf8Bytes
        );
        MemoPodAggregate pod = MemoPodAggregate.Open(
            arguments.RequireSingle("root"),
            MemoPodId.Parse(arguments.RequireSingle("pod"))
        );
        IReadOnlyList<string> rawMemoIds = arguments.GetRepeated(
            "fake-return-id"
        );
        ICompletionClient client = fakeClientFactory(rawMemoIds)
            ?? throw new InvalidOperationException(
                "The fake client factory returned null."
            );
        MemoRecallResult result = await pod.RecallAsync(
            client,
            FakeModelId,
            query,
            RecallOptions,
            cancellationToken
        ).ConfigureAwait(false);
        string[] selectedIds = result.Memos
            .Select(static memo => memo.Id.Value)
            .ToArray();
        await WriteJsonLineAsync(output, new {
            schema = OutputSchema,
            command = "recall",
            podId = pod.PodId.Value,
            phase = pod.Phase.ToString(),
            selectedCount = selectedIds.Length,
            selectedIds
        }).ConfigureAwait(false);
        return 0;
    }

    private static Task WriteMutationReportAsync(
        TextWriter output,
        string command,
        MemoPodAggregate pod,
        IReadOnlyList<MemoId> committedIds
    ) {
        string[] activeIds = pod.List()
            .Select(static memo => memo.Id.Value)
            .ToArray();
        return WriteJsonLineAsync(output, new {
            schema = OutputSchema,
            command,
            podId = pod.PodId.Value,
            phase = pod.Phase.ToString(),
            activeCount = activeIds.Length,
            activeIds,
            committedIds = committedIds
                .Select(static id => id.Value)
                .ToArray()
        });
    }

    private static async Task WriteJsonLineAsync(
        TextWriter output,
        object value
    ) {
        await output.WriteLineAsync(
            JsonSerializer.Serialize(value)
        ).ConfigureAwait(false);
    }

    private static async Task<int> FailAsync(
        TextWriter error,
        string code,
        int exitCode
    ) {
        await error.WriteLineAsync($"error={code}").ConfigureAwait(false);
        return exitCode;
    }

    private static async Task PrintHelpAsync(TextWriter output) {
        await output.WriteLineAsync(
            "SessionJournal.MemoPod.DebugApp"
        ).ConfigureAwait(false);
        await output.WriteLineAsync(
            "  create --root <existing-dir> --pod <id> --topic-file <file> [--memo-file <file>]..."
        ).ConfigureAwait(false);
        await output.WriteLineAsync(
            "  edit --root <dir> --pod <id> [--remove <id>]... [--memo-file <file>]..."
        ).ConfigureAwait(false);
        await output.WriteLineAsync(
            "  inspect --root <dir> --pod <id>"
        ).ConfigureAwait(false);
        await output.WriteLineAsync(
            "  get --root <dir> --pod <id> --memo <id>"
        ).ConfigureAwait(false);
        await output.WriteLineAsync(
            "  recall --root <dir> --pod <id> --query-file <file> [--fake-return-id <raw>]..."
        ).ConfigureAwait(false);
        await output.WriteLineAsync(
            "  recall --live true --root <dir> --pod <id> --connections <v1-file> --connection <exact-id> --case <label> --query-file <file> [--query-file <file>]... [--max-prompt-bytes <n>] [--max-tokens <n>] [--delay-ms <n>]"
        ).ConfigureAwait(false);
    }

    private static IReadOnlySet<string> Set(params string[] values)
        => new HashSet<string>(values, StringComparer.Ordinal);

    private static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException
            or BadImageFormatException
            or CannotUnloadAppDomainException;
}
