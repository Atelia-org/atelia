using System.Text;

namespace Atelia.TextAdv;

internal static class GamePresenter
{
    internal static string RenderPerception(GameSimulation.LocationPerception perception)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"📍 {perception.Name}");
        sb.AppendLine($"   {perception.Description}");
        sb.AppendLine();
        sb.AppendLine("🚪 可前往:");

        foreach (var exit in perception.Exits)
        {
            sb.AppendLine($"   {exit.Direction} → {exit.TargetName}");
        }

        return sb.ToString();
    }
}
