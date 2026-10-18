using System;
using System.Collections.Generic;
using System.Linq;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

// ESI fuel expiry and fuel-bay quantities are snapshots, not authoritative hourly consumption.
// When similarly fuelled drills have the same inferred burn rate, align the BLOCK TOTAL to
// the fullest latest-expiring drill, not to a fractional rate estimated from rounded expiry.
public static class StationFuelAlignment
{
    private const long BlockStep = 5;
    private const double MatchingRateTolerance = 0.02; // tolerate ESI timestamp/quantity skew
    private sealed record FuelReading(long Blocks, double RatePerHour);

    public static long? BlocksToAlign(DateTimeOffset? expires, DateTimeOffset? target,
        double? fuelBlocksPerHour)
    {
        if (expires == null || target == null || fuelBlocksPerHour is not > 0 ||
            !double.IsFinite(fuelBlocksPerHour.Value)) return null;
        double hours = Math.Max(0, (target.Value - expires.Value).TotalHours);
        double count = Math.Ceiling(hours * fuelBlocksPerHour.Value - 1e-9);
        return !double.IsFinite(count) || count > long.MaxValue ? null : (long)count;
    }

    private static long RoundUpToBlocksOfFive(long count)
    {
        if (count <= 0) return 0;
        long remainder = count % BlockStep;
        return remainder == 0 ? count : checked(count + BlockStep - remainder);
    }

    private static FuelReading? GetReading(EsiCorporationStructure structure,
        IReadOnlyList<EveAssetItem> assets, IReadOnlyDictionary<int, string> typeNames,
        DateTimeOffset now)
    {
        if (structure.FuelExpires is not { } expiry || expiry <= now) return null;
        var bay = assets.Where(a => a.LocationId == structure.StructureId &&
            a.LocationFlag == "StructureFuel").ToArray();
        if (bay.Length == 0 || bay.Select(a => a.TypeId).Distinct().Count() != 1 ||
            bay.Any(a => !typeNames.TryGetValue(a.TypeId, out var name) ||
                !name.Contains("Fuel Block", StringComparison.OrdinalIgnoreCase))) return null;

        long blocks = bay.Sum(a => a.Quantity);
        double hoursRemaining = (expiry - now).TotalHours;
        if (blocks <= 0 || hoursRemaining < 1) return null;
        double rate = blocks / hoursRemaining;
        // Never treat incompatible ESI numbers as an authoritative consumption rate.
        if (!double.IsFinite(rate) || rate < 0.1 || rate > 500) return null;
        return new FuelReading(blocks, rate);
    }

    public static IReadOnlyList<FuelAlignmentRow> Build(
        IReadOnlyList<EsiCorporationStructure> structures,
        IReadOnlyList<EveAssetItem> assets,
        IReadOnlyDictionary<int, string> typeNames,
        DateTimeOffset? assetsUpdatedUtc,
        DateTimeOffset now)
    {
        var stations = structures.Where(s => s.Services.Any(x =>
            x.Name.Contains("moon", StringComparison.OrdinalIgnoreCase))).ToArray();
        DateTimeOffset? target = stations.Where(s => s.FuelExpires > now)
            .Select(s => s.FuelExpires).Max();
        bool fresh = assetsUpdatedUtc.HasValue &&
            Math.Abs((now - assetsUpdatedUtc.Value).TotalMinutes) <= 120;

        // The ESI snapshot may contain several stations with the same (or almost
        // the same) latest expiry, yet stock differs by one five-block fuel tick.
        // Choose the FULLER station as the stock reference (10,290, not 10,285).
        FuelReading? reference = null;
        if (fresh && target.HasValue)
        {
            reference = stations.Where(s => s.FuelExpires.HasValue &&
                    s.FuelExpires <= target &&
                    target.Value - s.FuelExpires.Value <= TimeSpan.FromHours(1.5))
                .Select(s => GetReading(s, assets, typeNames, now))
                .Where(r => r != null)
                .OrderByDescending(r => r!.Blocks)
                .FirstOrDefault();
        }

        return stations.Select(s =>
        {
            string station = string.IsNullOrWhiteSpace(s.Name)
                ? $"Structure {s.StructureId}" : s.Name;
            string expires = s.FuelExpires?.ToUniversalTime()
                .ToString("dd MMM yyyy HH:mm 'UTC'") ?? "Unknown";
            if (!target.HasValue || !s.FuelExpires.HasValue || s.FuelExpires <= now)
                return new FuelAlignmentRow(s.StructureId, station, expires,
                    "Unavailable", "Unknown", "Missing or expired ESI fuel expiry");
            if (s.FuelExpires >= target)
                return new FuelAlignmentRow(s.StructureId, station, expires,
                    "Reference", "0 / aligned", "Latest expiry target");
            if (!fresh)
                return new FuelAlignmentRow(s.StructureId, station, expires,
                    "Unavailable", "Unknown", "Fresh ESI fuel-bay snapshot required");

            FuelReading? reading = GetReading(s, assets, typeNames, now);
            if (reading == null)
                return new FuelAlignmentRow(s.StructureId, station, expires,
                    "Unavailable", "Unknown", "Fuel inventory or implied rate unavailable");

            long toAdd;
            string basis;
            bool matchesReference = reference != null &&
                Math.Abs(reading.RatePerHour - reference.RatePerHour) <=
                Math.Max(reading.RatePerHour, reference.RatePerHour) * MatchingRateTolerance &&
                reading.Blocks <= reference.Blocks;
            if (matchesReference)
            {
                // Matching-rate stations get the same total as the latest
                // expiry's actual stock. This avoids a ~7,951 block estimate
                // when the reference is 10,290 and the station holds 2,350.
                long totalTarget = RoundUpToBlocksOfFive(reference!.Blocks);
                toAdd = Math.Max(0, totalTarget - reading.Blocks);
                basis = "Same burn rate; match fuller reference stock (estimate)";
            }
            else
            {
                long? rough = BlocksToAlign(s.FuelExpires, target, reading.RatePerHour);
                if (!rough.HasValue)
                    return new FuelAlignmentRow(s.StructureId, station, expires,
                        "Unavailable", "Unknown", "Cannot estimate time-based top-up");
                toAdd = RoundUpToBlocksOfFive(rough.Value);
                basis = reference == null
                    ? "No valid stock reference; time-based estimate"
                    : "Different rate or higher stock; time-based estimate";
            }
            long finalStock = checked(reading.Blocks + toAdd);
            return new FuelAlignmentRow(s.StructureId, station, expires,
                $"~{reading.RatePerHour:0.00}/h",
                toAdd == 0 ? $"0 / aligned ({finalStock:N0} total)"
                    : $"+{toAdd:N0} blocks | {finalStock:N0} total", basis);
        })
        .OrderByDescending(r => r.FuelToAdd.StartsWith("0 /", StringComparison.Ordinal))
        .ThenBy(r => r.StructureName)
        .ToArray();
    }
}
