using System.CommandLine;

namespace Atelia.TextAdv;

/// <summary>
/// 直接进程入口。PipeMux 仍然通过 <see cref="GameEntry.BuildGame"/> 加载同一套命令树。
/// </summary>
public static class Program {
    public static async Task<int> Main(string[] args) {
        if (!TryExtractDirectOptions(args, out var commandArgs, out var errorMessage)) {
            Console.Error.WriteLine(errorMessage);
            return 2;
        }

        var root = GameEntry.BuildGame();
        var parseResult = root.Parse(commandArgs);
        return await parseResult.InvokeAsync(
            new InvocationConfiguration {
                Output = Console.Out,
                Error = Console.Error,
            },
            CancellationToken.None
        ).ConfigureAwait(false);
    }

    private static bool TryExtractDirectOptions(
        string[] args,
        out string[] commandArgs,
        out string? errorMessage
    ) {
        var remaining = new List<string>(args.Length);
        errorMessage = null;

        for (var index = 0; index < args.Length; index++) {
            var arg = args[index];
            if (TryConsumeValueOption(args, ref index, arg, "--repo-dir", out var repoDir, out errorMessage)) {
                TextAdvRuntimeEnvironment.SetRepoDirOverride(repoDir);
                continue;
            }

            if (errorMessage is not null) {
                commandArgs = [];
                return false;
            }

            if (TryConsumeValueOption(args, ref index, arg, "--actor-journal-dir", out var actorJournalDir, out errorMessage)) {
                TextAdvRuntimeEnvironment.SetActorJournalDirOverride(actorJournalDir);
                continue;
            }

            if (errorMessage is not null) {
                commandArgs = [];
                return false;
            }

            remaining.AddRange(args[index..]);
            break;
        }

        commandArgs = remaining.ToArray();
        return true;
    }

    private static bool TryConsumeValueOption(
        string[] args,
        ref int index,
        string arg,
        string optionName,
        out string? value,
        out string? errorMessage
    ) {
        value = null;
        errorMessage = null;

        if (arg.StartsWith($"{optionName}=", StringComparison.Ordinal)) {
            value = arg[(optionName.Length + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(value)) {
                errorMessage = $"Option '{optionName}' requires a non-empty value.";
                return false;
            }

            return true;
        }

        if (!string.Equals(arg, optionName, StringComparison.Ordinal)) {
            return false;
        }

        if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal)) {
            errorMessage = $"Option '{optionName}' requires a value.";
            return false;
        }

        index++;
        value = args[index].Trim();
        if (string.IsNullOrWhiteSpace(value)) {
            errorMessage = $"Option '{optionName}' requires a non-empty value.";
            return false;
        }

        return true;
    }
}
