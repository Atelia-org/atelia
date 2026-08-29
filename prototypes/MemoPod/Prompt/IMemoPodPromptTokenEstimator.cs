namespace Atelia.MemoPod;

internal interface IMemoPodPromptTokenEstimator {
    int EstimateTokenCount(string exactPromptText);
}
