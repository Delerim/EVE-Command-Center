using EveCommandCenter.Models;
namespace EveCommandCenter.Services;

// Capacity is unmodified hull capacity. These snapshots never drive cargo-full alarms.
internal static class MiningHoldSnapshots
{
    internal static async Task<EveShipHoldSnapshot> ReadAsync(EveCurrentShipView ship, IReadOnlyList<EveAssetItem> assets,
        DateTimeOffset observed, Func<int, Task<EveUniverseType>> typeLookup)
    {
        var hull = await typeLookup(ship.ShipTypeId);
        bool shipPresent = assets.Any(a => a.ItemId == ship.ShipItemId && a.TypeId == ship.ShipTypeId);
        async Task<EveShipHoldSnapshot> Hold(string flag, int attribute, double? fallback = null)
        {
            double capacity = hull.DogmaAttributes.FirstOrDefault(a => a.AttributeId == attribute)?.Value ?? fallback ?? 0;
            if (!double.IsFinite(capacity) || capacity <= 0)
                return new() { Supported = false, Detail = "No base hold capacity listed" };
            if (!shipPresent || observed == default)
                return new() { BaseCapacityM3 = capacity, Detail = "Current ship is absent from the asset snapshot. Refresh after changing ships." };
            double used = 0; bool complete = true;
            foreach (var group in assets.Where(a => a.LocationId == ship.ShipItemId && a.LocationFlag.Equals(flag, StringComparison.OrdinalIgnoreCase)).GroupBy(a => a.TypeId))
            {
                try
                {
                    var type = await typeLookup(group.Key);
                    foreach (var item in group)
                    {
                        double? volume = item.IsSingleton ? type.Volume : type.PackagedVolume ?? type.Volume;
                        if (item.Quantity <= 0 || volume is not > 0 || !double.IsFinite(volume.Value)) { complete = false; continue; }
                        double amount = volume.Value * item.Quantity;
                        if (!double.IsFinite(amount) || !double.IsFinite(used + amount)) { complete = false; continue; }
                        used += amount;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { complete = false; }
            }
            return new() { UsedM3 = used, BaseCapacityM3 = capacity, Complete = complete, ObservedUtc = observed,
                Detail = complete ? "Cached inventory; base hull capacity excludes skills and fittings" : "Some item volumes could not be resolved" };
        }
        return await Hold("FleetHangar", 912);
    }
    internal sealed record Row(string Label, string VolumeText, double Percent, string Accent, string Detail);
    internal static Row Present(string label, EveShipHoldSnapshot hold, DateTimeOffset now)
    {
        bool stale = hold.ObservedUtc == default || hold.ObservedUtc < now.AddMinutes(-15) || hold.ObservedUtc > now.AddMinutes(5);
        bool known = hold.Complete && double.IsFinite(hold.UsedM3) && hold.UsedM3 >= 0 && double.IsFinite(hold.BaseCapacityM3) && hold.BaseCapacityM3 > 0;
        string amounts = known ? $"{hold.UsedM3:N0} / {hold.BaseCapacityM3:N0} m\u00b3" : "Unavailable";
        string prefix = known ? stale ? "Old: " : "Cached: " : "";
        double fraction = known ? hold.UsedM3 / hold.BaseCapacityM3 : 0;
        return new(label, prefix + amounts, known ? Math.Clamp(fraction * 100, 0, 100) : 0,
            !known || stale ? "#829C9F" : fraction >= .8 ? "#D7B66B" : "#65CBB9",
            $"{label}: {(known ? $"{hold.UsedM3:N1} / {hold.BaseCapacityM3:N1} m\u00b3 capacity" : "Unknown volume")}\n" +
            (hold.ObservedUtc == default ? "No asset snapshot. " : $"Assets fetched {hold.ObservedUtc.ToLocalTime():dd MMM HH:mm}. ") +
            "ESI-cached, not live. Capacity excludes skill and fitting bonuses.\n" + hold.Detail +
            (fraction > 1 ? "\nVolume exceeds base capacity; this does not imply the in-game hold is full." : ""));
    }
}
