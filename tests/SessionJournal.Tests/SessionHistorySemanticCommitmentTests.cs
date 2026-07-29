using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionHistorySemanticCommitmentTests {
    [Fact]
    public void GoldenVectors_AreStableAndContentSensitive() {
        var observation = new ObservationMessage("hello");
        var action = new ActionMessage([
            new ActionBlock.Text("answer"),
            new ActionBlock.ToolCall(
                new RawToolCall("lookup", "call-1", """{"x":1}""")
            )
        ]);
        ToolResult result = ToolResult.FromText(
            "lookup",
            "call-1",
            ToolExecutionStatus.Success,
            "found"
        );
        string observationHash =
            SessionHistorySemanticCommitment
                .ComputeObservationContributionSha256(observation);
        string actionHash =
            SessionHistorySemanticCommitment
                .ComputeActionContributionSha256(action);
        string resultHash =
            SessionHistorySemanticCommitment
                .ComputeToolResultSha256(result);
        string toolResultsHash =
            SessionHistorySemanticCommitment
                .ComputeToolResultsContributionSha256([resultHash]);
        string sequenceHash =
            SessionHistorySemanticCommitment
                .ComputeSequenceSha256([
                    observationHash,
                    actionHash,
                    toolResultsHash
                ]);

        Assert.Equal(
            "d3523bbdeb511b9b38aa645cf2cca1de79dbd716624d025b58400e2ab99a9c5b",
            observationHash
        );
        Assert.Equal(
            "8eb542ec3d83146f4796e256d5b2d30d6f7b8d08b6f718bc8d0cd587ac7dbb17",
            actionHash
        );
        Assert.Equal(
            "47461f82c8c1b454e44ac628d20d6e6912b866865a6ef63e4e8f02cbdea51905",
            resultHash
        );
        Assert.Equal(
            "eccc5f8fe8a94a54f6cbab7b4e0c302c2df97faf6a524912a0383922d5505c04",
            toolResultsHash
        );
        Assert.Equal(
            "e90d91890f81dd0afb2ae34bc8e947434e2ab684d076acdd89a20d8f7963e182",
            sequenceHash
        );
    }

    [Fact]
    public void SemanticChanges_ChangeTheirCommitments() {
        string observation =
            SessionHistorySemanticCommitment
                .ComputeObservationContributionSha256(
                    new ObservationMessage("one")
                );
        string changedObservation =
            SessionHistorySemanticCommitment
                .ComputeObservationContributionSha256(
                    new ObservationMessage("two")
                );
        string action =
            SessionHistorySemanticCommitment
                .ComputeActionContributionSha256(
                    new ActionMessage([
                        new ActionBlock.Text("one")
                    ])
                );
        string changedAction =
            SessionHistorySemanticCommitment
                .ComputeActionContributionSha256(
                    new ActionMessage([
                        new ActionBlock.Text("two")
                    ])
                );
        string result =
            SessionHistorySemanticCommitment.ComputeToolResultSha256(
                ToolResult.FromText(
                    "tool",
                    "call",
                    ToolExecutionStatus.Success,
                    "one"
                )
            );
        string changedResult =
            SessionHistorySemanticCommitment.ComputeToolResultSha256(
                ToolResult.FromText(
                    "tool",
                    "call",
                    ToolExecutionStatus.Success,
                    "two"
                )
            );

        Assert.NotEqual(observation, changedObservation);
        Assert.NotEqual(action, changedAction);
        Assert.NotEqual(result, changedResult);
        Assert.NotEqual(
            SessionHistorySemanticCommitment
                .ComputeToolResultsContributionSha256([result]),
            SessionHistorySemanticCommitment
                .ComputeToolResultsContributionSha256([
                    changedResult
                ])
        );
        Assert.NotEqual(
            SessionHistorySemanticCommitment
                .ComputeSequenceSha256([observation, action]),
            SessionHistorySemanticCommitment
                .ComputeSequenceSha256([action, observation])
        );
    }
}
