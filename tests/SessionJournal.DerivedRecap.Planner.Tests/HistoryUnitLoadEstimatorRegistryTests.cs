using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class HistoryUnitLoadEstimatorRegistryTests {
    [Fact]
    public void KnownEstimatorResolvesToOneProcessSingleton() {
        var first = Assert.IsType<
            HistoryUnitLoadEstimatorResolutionResult.Resolved
        >(HistoryUnitLoadEstimatorRegistry.Resolve(
            O200kBaseHistoryUnitLoadEstimator.EstimatorId
        ));
        var second = Assert.IsType<
            HistoryUnitLoadEstimatorResolutionResult.Resolved
        >(HistoryUnitLoadEstimatorRegistry.Resolve(
            O200kBaseHistoryUnitLoadEstimator.EstimatorId
        ));

        Assert.Equal(
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            first.EstimatorId
        );
        Assert.IsType<O200kBaseHistoryUnitLoadEstimator>(
            first.Estimator
        );
        Assert.Same(first.Estimator, second.Estimator);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingEstimatorIdFailsTyped(string? estimatorId) {
        var invalid = Assert.IsType<
            HistoryUnitLoadEstimatorResolutionResult.Invalid
        >(HistoryUnitLoadEstimatorRegistry.Resolve(estimatorId));

        Assert.Equal(
            HistoryUnitLoadEstimatorResolutionDefectCodes
                .EstimatorIdMissing,
            invalid.Defect.Code
        );
    }

    [Fact]
    public void UnknownEstimatorFailsTypedWithoutFallback() {
        var invalid = Assert.IsType<
            HistoryUnitLoadEstimatorResolutionResult.Invalid
        >(HistoryUnitLoadEstimatorRegistry.Resolve(
            "atelia.history-load.unknown"
        ));

        Assert.Equal(
            HistoryUnitLoadEstimatorResolutionDefectCodes
                .UnknownEstimator,
            invalid.Defect.Code
        );
    }
}
