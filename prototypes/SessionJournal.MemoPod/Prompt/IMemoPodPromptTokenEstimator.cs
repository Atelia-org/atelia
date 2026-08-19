namespace Atelia.SessionJournal.MemoPod;

internal interface IMemoPodPromptTokenEstimator {
    int EstimateTokenCount(string exactPromptText);
}
