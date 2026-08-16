using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Manager;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

[Collection(ConsoleSerialCollection.Name)]
public sealed class RecapGridProgressContractTests {
    [Fact]
    public void FrontierReportKeepsExactJsonPropertyNamesAndOrder() {
        var authority = (RecapGridBuildProgressAuthority)
            InvokeArgumentConstructor(
                typeof(RecapGridBuildProgressAuthority),
                null,
                null,
                null,
                default(GridBuildRecipeDigest),
                default(HistoryRowId),
                default(HistorySegmentDescriptorDigest)
            );
        var nextWork = (RecapGridRecipeRowWork)InvokeArgumentConstructor(
            typeof(RecapGridRecipeRowWork),
            default(HistoryRowId),
            default(GridBuildRecipeDigest),
            true
        );
        var missing = (RecapGridMissingAssignmentProgress)
            InvokeArgumentConstructor(
                typeof(RecapGridMissingAssignmentProgress),
                7,
                default(HistoryRowId),
                default(GridBuildRecipeDigest),
                default(LogicalColumnId),
                default(EvaluationKeyDigest)
            );
        var frontier = new RecapGridBuildProgressResult.Frontier(
            authority,
            AnchorRowId: null,
            nextWork,
            PendingRecipeRows: 2,
            [missing]
        );
        typeof(RecapGridBuildProgressResult).GetProperty("Metrics")!
            .SetValue(frontier, new RecapGridBuildProgressMetrics(1, 2, 3, 4));

        (int exitCode, string json) = CapturePrint(
            "progress",
            "frontier",
            frontier
        );

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "{" +
                "\"schema\":\"atelia.session-journal.recap-grid-cli.v1\"," +
                "\"command\":\"progress\",\"status\":\"frontier\"," +
                "\"detail\":{" +
                "\"Authority\":{" +
                "\"TimelineHead\":null,\"ControlHead\":null," +
                "\"StoreIdentity\":null," +
                "\"RecipeDigest\":{\"Value\":null}," +
                "\"ThroughRowId\":{\"Value\":null}," +
                "\"ThroughDescriptorDigest\":{\"Value\":null}}," +
                "\"AnchorRowId\":null," +
                "\"NextWork\":{" +
                "\"RowId\":{\"Value\":null}," +
                "\"RecipeDigest\":{\"Value\":null}," +
                "\"IsOverlayBootstrap\":true}," +
                "\"PendingRecipeRows\":2," +
                "\"OrderedMissing\":[{" +
                "\"Ordinal\":7,\"RowId\":{\"Value\":null}," +
                "\"RecipeDigest\":{\"Value\":null}," +
                "\"LogicalColumnId\":{\"Value\":null}," +
                "\"EvaluationKey\":{\"Value\":null}}]," +
                "\"RowId\":{\"Value\":null}," +
                "\"RecipeDigest\":{\"Value\":null}," +
                "\"Metrics\":{" +
                "\"SelectedRows\":1,\"RecipeRowSteps\":2," +
                "\"ExaminedAssignments\":3," +
                "\"MissingAssignments\":4}}}",
            json
        );
    }

    private static object InvokeArgumentConstructor(
        Type type,
        params object?[] arguments
    ) {
        const System.Reflection.BindingFlags InstanceConstructors =
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic;
        System.Reflection.ConstructorInfo constructor = Assert.Single(
            type.GetConstructors(InstanceConstructors),
            candidate => {
                System.Reflection.ParameterInfo[] parameters = candidate
                    .GetParameters();
                return parameters.Length == arguments.Length
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
        object detail
    ) {
        TextWriter original = Console.Out;
        using var output = new StringWriter();
        try {
            Console.SetOut(output);
            int exitCode = RecapGridCommands.Print(command, status, detail);
            string json = output.ToString().TrimEnd('\r', '\n');
            return (exitCode, json);
        }
        finally {
            Console.SetOut(original);
        }
    }
}
