using System;
using System.Collections.Generic;
using System.Linq;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

// Estimate only: ESI exposes expiry and assets, not authoritative hourly service consumption.
public static class StationFuelAlignment
{
    public static long? BlocksToAlign(DateTimeOffset? expires, DateTimeOffset? target,
        double? fuelBlocksPerHour)
    {
        if (expires == null || target == null || fuelBlocksPerHour is not > 0 ||
            !double.IsFinite(fuelBlocksPerHour.Value)) return null;
        double hours = Math.Max(0, (target.Value - expires.Value).TotalHours);
        double count = Math.Ceiling(hours * fuelBlocksPerHour.Value - 1e-9);
        return !double.IsFinite(count) || count > long.MaxValue ? null : (long)count;
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
        return stations.Select(s =>
        {
            string station = string.IsNullOrWhiteSpace(s.Name)
                ? $"Structure {s.StructureId}" : s.Name;
            string expires = s.FuelExpires?.ToUniversalTime().ToString("dd MMM yyyy HH:mm 'UTC'") ?? "Unknown";
            if (!target.HasValue || !s.FuelExpires.HasValue || s.FuelExpires <= now)
                return new FuelAlignmentRow(s.StructureId, station, expires, "Unavailable", "Unknown", "Missing/expired ESI fuel expiry");
            if (s.FuelExpires >= target)
                return new FuelAlignmentRow(s.StructureId, station, expires, "Reference", "0 / aligned", "Latest expiry target");
            if (!fresh)
                return new FuelAlignmentRow(s.StructureId, station, expires, "Unavailable", "Unknown", "Fresh fuel-bay ESI snapshot required");

            // Mixed/non-fuel inventories must not be treated as a burn rate.
            var bay = assets.Where(a => a.LocationId == s.StructureId &&
                a.LocationFlag == "StructureFuel").ToArray();
            if (bay.Length == 0 || bay.Any(a => !typeNames.TryGetValue(a.TypeId, out var name) ||
                !name.Contains("Fuel Block", StringComparison.OrdinalIgnoreCase)))
                return new FuelAlignmentRow(s.StructureId, station, expires, "Unavailable", "Unknown", "Fuel type/quantity cannot be verified");
            long blocks = bay.Sum(a => a.Quantity);
            double hoursRemaining = (s.FuelExpires.Value - now).TotalHours;
            if (blocks <= 0 || hoursRemaining < 1)
                return new FuelAlignmentRow(s.StructureId, station, expires, "Unavailable", "Unknown", "Insufficient ESI data");
            double perHour = blocks / hoursRemaining;
            // Plausibility guard: if ESI inventories do not match expiry, do not fabricate precision.
            if (!double.IsFinite(perHour) || perHour < 0.1 || perHour > 500)
                return new FuelAlignmentRow(s.StructureId, station, expires, "Unavailable", "Unknown", "Implausible implied burn rate");
            long? add = BlocksToAlign(s.FuelExpires, target, perHour);
            return new FuelAlignmentRow(s.StructureId, station, expires, $"~{perHour:0.00}/h",
                add.HasValue ? $"~{add.Value:N0} blocks" : "Unknown",
                "Estimate from ESI bay quantity / hours to expiry; verify in-game");
        }).OrderByDescending(r => r.FuelToAdd == "0 / aligned").ThenBy(r => r.StructureName).ToArray();
    }
}
