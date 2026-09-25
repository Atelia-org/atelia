using System.Security.Cryptography;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class ActionTextExportCommand {
    internal static int Run(CliOptions options) {
        options.EnsureOnly("input", "branch", "address", "output");
        string input = Path.GetFullPath(options.RequireSingle("input"));
        string branch = options.GetOptionalSingle("branch")
            ?? SessionJournalDefaults.MainBranchName;
        string addressText = options.RequireSingle("address");
        if (!EventAddressTextCodec.TryParse(addressText, out var address)) {
            throw new ArgumentException("--address must be a canonical EventJournal address.");
        }
        string output = Path.GetFullPath(options.RequireSingle("output"));

        CliIo.EnsurePathChainHasNoReparsePoint(input, "--input");
        CliIo.EnsurePathChainHasNoReparsePoint(output, "--output");
        CliIo.ValidateFileOutputPath(input, output, "--output");

        using var engine = SessionJournalEngine.OpenReadOnly(input, branch);
        SessionCompletedTurnsReadResult read = engine.ReadRecentCompletedTurnsAt(
            address,
            maximumCount: 1
        );
        if (read is not SessionCompletedTurnsReadResult.Snapshot snapshot) {
            throw new InvalidDataException(
                $"Cannot read completed turn at {addressText}: {read.GetType().Name}."
            );
        }
        SessionTerminalActionProjection? action = snapshot.Value.Turns
            .SingleOrDefault()?.TerminalAction;
        if (action is null || action.Address != address) {
            throw new InvalidDataException(
                $"Address {addressText} is not a terminal Action."
            );
        }

        string text = string.Concat(action.Message.Blocks
            .OfType<ActionBlock.Text>()
            .Select(static block => block.Content));
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var stream = new FileStream(
                   output,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None
               )) {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        Console.WriteLine($"address: {addressText}");
        Console.WriteLine($"output: {output}");
        Console.WriteLine($"utf8Bytes: {bytes.Length}");
        Console.WriteLine($"sha256: {Convert.ToHexStringLower(SHA256.HashData(bytes))}");
        return 0;
    }
}
