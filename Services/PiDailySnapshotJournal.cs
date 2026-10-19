using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

// One immutable observed colony layout per UTC day and character/planet.
// These are observations, not proof of production or items hauled while offline.
internal static class PiDailySnapshotJournal
{
    private static readonly object Gate = new();

    internal static bool Capture(PiColony colony, string? directory = null)
    {
        if (colony.CharacterId <= 0 || colony.PlanetId <= 0 || colony.Fetched == default)
            return false;
        string root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE Command Center", "PiHistory");
        string path = Path.Combine(root,
            colony.Fetched.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
        lock (Gate)
        {
            if (File.Exists(path))
            {
                foreach (string line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using JsonDocument existing = JsonDocument.Parse(line);
                    JsonElement item = existing.RootElement;
                    if (item.TryGetProperty("CharacterId", out JsonElement pilot) &&
                        item.TryGetProperty("PlanetId", out JsonElement planet) &&
                        pilot.GetInt64() == colony.CharacterId && planet.GetInt64() == colony.PlanetId)
                        return false;
                }
            }
            Directory.CreateDirectory(root);
            string snapshot = JsonSerializer.Serialize(new
            {
                ObservedUtc = colony.Fetched,
                colony.CharacterId, colony.Character, colony.PlanetId, colony.Planet,
                colony.PlanetType, colony.UpgradeLevel,
                ColonyLastUpdateUtc = colony.LastUpdate,
                colony.Layout
            });
            File.AppendAllText(path, snapshot + "\n", new UTF8Encoding(false));
            return true;
        }
    }
}
