using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Online;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

[Collection(ConsoleSerialCollection.Name)]
public sealed class RecapGridOnlineMaintenanceContractTests {
    [Fact]
    public void MaintenanceContinuationKeepsExactJsonPropertyOrder() {
        var evidence = (RecapGridOnlineMaintenanceEvidence)
            InvokeInternalArgumentConstructor(
                typeof(RecapGridOnlineMaintenanceEvidence),
                1,
                true,
                2,
                null,
                null,
                3,
                4,
                5,
                6,
                null,
                null,
                RecapGridOnlineContinuationKind.GridDebtRemaining
            );

        (int exitCode, string json) = CapturePrint(
            "run-online-turn",
            "maintenance-continuation",
            new {
                component = "Manager",
                Code = "GridDebtRemaining",
                Detail = "One recipe-row maintenance unit consumed this lifecycle pass.",
                Evidence = evidence
            },
            exitCode: 2
        );

        Assert.Equal(2, exitCode);
        Assert.Equal(
            "{" +
                "\"schema\":\"atelia.session-journal.recap-grid-cli.v1\"," +
                "\"command\":\"run-online-turn\"," +
                "\"status\":\"maintenance-continuation\"," +
                "\"detail\":{" +
                "\"component\":\"Manager\"," +
                "\"Code\":\"GridDebtRemaining\"," +
                "\"Detail\":\"One recipe-row maintenance unit consumed this lifecycle pass.\"," +
                "\"Evidence\":{" +
                "\"Passes\":1,\"EntryDebt\":true," +
                "\"TimelineRowsCommitted\":2," +
                "\"LastAttemptedRecipeRow\":null," +
                "\"LastAttemptedAuthority\":null," +
                "\"RecipeRowSteps\":3,\"RowViewsCommitted\":4," +
                "\"CellsCommitted\":5,\"NewCalls\":6," +
                "\"NextRecipeRow\":null,\"NextAuthority\":null," +
                "\"ContinuationKind\":3}}}",
            json
        );
    }

    private static object InvokeInternalArgumentConstructor(
        Type type,
        params object?[] arguments
    ) {
        const System.Reflection.BindingFlags InstanceConstructors =
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic;
        System.Reflection.ConstructorInfo constructor = Assert.Single(
            type.GetConstructors(InstanceConstructors),
            candidate => {
                System.Reflection.ParameterInfo[] parameters = candidate
                    .GetParameters();
                return candidate.IsAssembly
                    && parameters.Length == arguments.Length
                    && parameters.Zip(arguments).All(static pair =>
                        pair.Second is null
                            ? !pair.First.ParameterType.IsValueType
                            : pair.First.ParameterType.IsInstanceOfType(
                                pair.Second
                            )
                    );
            }
        );
        return constructor.Invoke(arguments);
    }

    private static (int ExitCode, string Json) CapturePrint(
        string command,
        string status,
        object detail,
        int exitCode
    ) {
        TextWriter original = Console.Out;
        using var output = new StringWriter();
        try {
            Console.SetOut(output);
            int result = RecapGridCommands.Print(
                command,
                status,
                detail,
                exitCode
            );
            string json = output.ToString().TrimEnd('\r', '\n');
            return (result, json);
        }
        finally {
            Console.SetOut(original);
        }
    }
}
