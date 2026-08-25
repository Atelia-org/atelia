using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.AgentControl.PublicSurface.Tests;

public sealed class PublicSurfaceTests {
    [Fact]
    public async Task ExternalCompositionCanBindExecuteAndDisposeExactProfile() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "atelia-recap-grid-agent-control-public-tests",
            Guid.NewGuid().ToString("N")
        );
        try {
            var estimator = new O200kBaseHistoryUnitLoadEstimator();
            using SessionJournalEngine journal = SessionJournalEngine.Create(
                path,
                new SessionCreateOptions("model", "system", "surface")
            );
            Assert.IsType<HistoryTimelineCreateResult.Created>(
                HistoryTimelineFactory.Create(
                    journal.ReadView,
                    new HistoryTimelineInitialPolicySpec(
                        HistoryPartitionAlgorithms
                            .FirstReplaySafeBoundaryAtTargetV1,
                        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                        new HistoryLoadUnit(1),
                        maxRawEvents: 64,
                        maxRenderedBytes: 1024 * 1024
                    ),
                    estimator
                )
            );
            var admission = new RecapGridControlAdmission(
                RecapGridControlPermission.Create,
                Array.Empty<FamilyDefinitionDigest>(),
                Array.Empty<string>(),
                Array.Empty<ContextHeaderCarrier>(),
                ["surface."],
                maximumBootstrapRows: 64,
                maximumProjectedCalls: 64
            );
            Assert.IsType<RecapGridControlCreateResult.Created>(
                RecapGridControlFactory.Create(
                    path,
                    journal.BranchRefId,
                    admission
                )
            );
            RecapGridAgentControlProfile profile =
                RecapGridAgentControlProfile.Create(
                    "public-surface-v1",
                    admission
                );
            RecapGridAgentControlOpenResult.Opened opened = Assert.IsType<
                RecapGridAgentControlOpenResult.Opened
            >(RecapGridAgentControlFactory.Bind(
                journal.ReadView,
                profile,
                estimator
            ));
            using RecapGridAgentControlHandle handle = opened.Handle;

            ToolCallExecutionResult result = await handle.ToolSession
                .ExecuteReservedAsync(
                    new Atelia.Completion.Abstractions.RawToolCall(
                        "recap_grid_control",
                        "call-1",
                        "{\"action\":\"inspect\"}"
                    ),
                    reservedExecutionSequence: 1,
                    operationId: "public-surface-operation",
                    TestContext.Current.CancellationToken
                );

            Assert.Equal(ToolExecutionStatus.Success,
                result.ExecuteResult.Status);
            Assert.Equal(profile.RuntimeIdentity, handle.RuntimeIdentity);
        }
        finally {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
