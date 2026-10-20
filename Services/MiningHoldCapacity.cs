using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

internal static class MiningHoldCapacity
{
    internal static bool IsExhumer(int typeId) => typeId is 22544 or 22546 or 22548;

    internal static bool HasLiveMiningHold(int typeId) => IsExhumer(typeId) || typeId == 28606;

    // Capacities and bonus percentages come from ship dogma, not a fixed Hulk-sized hold.
    internal static double For(EveUniverseType hull, IEnumerable<EveSkillEntry> skills)
    {
        double Attribute(int id) => hull.DogmaAttributes.FirstOrDefault(a => a.AttributeId == id)?.Value ?? 0;
        int Level(int id) => Math.Clamp(skills.FirstOrDefault(s => s.SkillId == id)?.ActiveSkillLevel ?? 0, 0, 5);
        double capacity = Attribute(1556);
        capacity *= 1 + Attribute(3187) * Level(17940) / 100;
        capacity *= 1 + Attribute(3198) * Level(22551) / 100;
        capacity *= 1 + Attribute(3212) * Level(29637) / 100;
        return double.IsFinite(capacity) && capacity > 0 ? capacity : 0;
    }
}
