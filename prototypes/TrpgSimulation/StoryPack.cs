namespace Atelia.DebugApps.TrpgSimulation;

// ═════════════════════════════════════════
// StoryPack：可切换的成套提示词配置
// ═════════════════════════════════════════
internal sealed record StoryPack(
    string Name,
    string Description,
    string GmSystemPrompt,
    string PlayerSystemPrompt,
    string InitialObservation
);

static partial class StoryPacks {

}
