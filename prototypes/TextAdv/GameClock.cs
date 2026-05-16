namespace Atelia.TextAdv;

internal static class GameClock {
    private static readonly string[] s_namedSlotsForFourPhaseDay = ["早晨", "中午", "晚上", "午夜"];

    internal static string GetNamedSlot(int slot, int slotsPerDay) {
        if (slotsPerDay == s_namedSlotsForFourPhaseDay.Length
            && slot >= 1
            && slot <= s_namedSlotsForFourPhaseDay.Length) { return s_namedSlotsForFourPhaseDay[slot - 1]; }

        return $"时段 {slot}";
    }

    internal static string FormatClock(int day, int slot, int slotsPerDay)
        => $"Day {day} · {GetNamedSlot(slot, slotsPerDay)} (Slot {slot}/{slotsPerDay})";

    internal static (int Day, int Slot) PreviewNextClock(int day, int slot, int slotsPerDay) {
        var nextSlot = slot + 1;
        var nextDay = day;
        if (nextSlot > slotsPerDay) {
            nextDay++;
            nextSlot = 1;
        }

        return (nextDay, nextSlot);
    }
}
