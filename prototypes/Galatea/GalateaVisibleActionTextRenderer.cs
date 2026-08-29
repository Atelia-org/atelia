using System.Text;
using Atelia.Completion.Abstractions;

namespace Atelia.Galatea.Server;

internal static class GalateaVisibleActionTextRenderer {
    internal static string Render(ActionMessage message) {
        ArgumentNullException.ThrowIfNull(message);
        var text = new StringBuilder();
        foreach (ActionBlock block in message.Blocks) {
            if (block is ActionBlock.Text visible) {
                _ = text.Append(visible.Content);
            }
        }
        return InlineThinkTextFilter.StripInlineThinkBlocks(text.ToString());
    }
}
