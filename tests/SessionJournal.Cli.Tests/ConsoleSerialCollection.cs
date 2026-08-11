using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleSerialCollection {
    public const string Name = "SessionJournal CLI Console serialization";
}
