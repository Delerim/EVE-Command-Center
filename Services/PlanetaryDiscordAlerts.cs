using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

/// <summary>
/// Actionable, grouped PI notifications for Discord. Deliberately distinct from
/// detailed desktop/in-app per-planet alerts. Never treats missing or stale ESI
/// information as confirmation of a failed extractor.
/// </summary>
public sealed record PlanetaryDiscordEvent(string Type, string Title, string Detail, string Key);

public static class PlanetaryDiscordAlerts
{
    public static IReadOnlyList<PlanetaryDiscordEvent> Evaluate(PiState state, DateTimeOffset now)
        => EvaluateAnalysis(PlanetaryAnalysis.Build(state, now), state, now);

    public static IReadOnlyList<PlanetaryDiscordEvent> EvaluateAnalysis(PiAnalysis analysis, PiState state, DateTimeOffset now)
    {
        var events = new List<PlanetaryDiscordEvent>();
        // Haul summaries already exclude supplies reserved for routed processors.
        // Stages are computed in PiHaulSummary at 40,500 and 45,000 m3.
        foreach (var haul in analysis.Hauls.Where(h => h.Stage > 0))
        {
            var colonies = state.Colonies.Where(c => c.CharacterId == haul.CharacterId).ToArray();
            if (colonies.Length == 0 || !colonies.All(c => Fresh(c, now))) continue;
            var stage = haul.Stage;
            events.Add(new(DiscordWebhookService.PiCollection,
                haul.Character + (stage == 2 ? " | T1 haul limit reached" : " | T1 collection approaching"),
                haul.Summary + ". Combined across extractor planets. Estimated stock; confirm in EVE before hauling.",
                haul.CharacterId + ":" + stage));
        }

        foreach (var pilots in state.Colonies.Where(c => Fresh(c, now)).GroupBy(c => c.CharacterId))
        {
            var pilot = pilots.First();
            // A factory world has no extractor pin at all. The existing PI analysis
            // verifies supplied/routed factory inputs before emitting COLLECT / REFILL.
            var factories = pilots.Where(FactoryWorld).Select(c => new
            {
                Colony = c,
                Due = analysis.Pins.Any(p => p.Colony?.PlanetId == c.PlanetId &&
                    p.Colony.CharacterId == c.CharacterId &&
                    p.Status.Contains("COLLECT / REFILL", StringComparison.OrdinalIgnoreCase))
            }).Where(x => x.Due).Select(x => x.Colony.Planet).Distinct().OrderBy(x => x).ToArray();
            if (factories.Length > 0)
                events.Add(new(DiscordWebhookService.PiFactoryRefill,
                    pilot.Character + " | Factory inputs need refilling",
                    factories.Length + " factory world(s) ready for collection/refill: " + string.Join(", ", factories) +
                    ". Based on estimated input runway; verify in EVE.", pilot.CharacterId.ToString()));

            var idle = pilots.Where(c => ExtractorInactiveOverFiveHours(c, now))
                .Select(c => c.Planet).Distinct().OrderBy(x => x).ToArray();
            if (idle.Length > 0)
                events.Add(new(DiscordWebhookService.PiExtractorIdle,
                    pilot.Character + " | Extractors inactive over 5 hours",
                    idle.Length + " extractor planet(s) without an active program for over 5 hours: " +
                    string.Join(", ", idle) + ". Restart/check the extractor programs.",
                    pilot.CharacterId.ToString()));
        }
        return events;
    }

    public static void Dispatch(PiState state, DateTimeOffset now, DiscordWebhookService service)
    {
        // Skip expensive PI projections entirely until a destination opts in.
        var relevant = new[] { DiscordWebhookService.PiCollection,
            DiscordWebhookService.PiFactoryRefill, DiscordWebhookService.PiExtractorIdle };
        if (!service.Destinations().Any(d => d.Enabled &&
            d.AlertTypes.Any(t => relevant.Contains(t)))) return;
        var events = Evaluate(state, now);
        foreach (var type in new[] { DiscordWebhookService.PiCollection,
            DiscordWebhookService.PiFactoryRefill, DiscordWebhookService.PiExtractorIdle })
        {
            // Cleared conditions re-arm; unchanged conditions never spam new posts,
            // even after app restart. Distinct collection stages can escalate.
            service.ReconcileActive(type, events.Where(e => e.Type == type).Select(e => e.Key));
        }
        foreach (var item in events)
            service.Publish(item.Type, item.Title, item.Detail, item.Key);
    }

    private static bool Fresh(PiColony c, DateTimeOffset now) =>
        c.Error.Length == 0 && c.Fetched != default &&
        c.Fetched <= now.AddMinutes(2) && c.Fetched >= now.AddMinutes(-30) &&
        c.Layout.ValueKind == JsonValueKind.Object;

    private static bool FactoryWorld(PiColony c)
    {
        var pins = PlanetaryAnalysis.Array(c.Layout, "pins");
        return pins.Length > 0 &&
            !pins.Any(p => p.TryGetProperty("extractor_details", out _));
    }

    private static bool ExtractorInactiveOverFiveHours(PiColony c, DateTimeOffset now)
    {
        var pins = PlanetaryAnalysis.Array(c.Layout, "pins")
            .Where(p => p.TryGetProperty("extractor_details", out _)).ToArray();
        if (pins.Length == 0) return false;
        // Each extractor must have been unscheduled/expired for more than five
        // hours. Do not mislabel a planet if any extractor is still active.
        return pins.All(p =>
        {
            DateTimeOffset? expiry = p.TryGetProperty("expiry_time", out var t) &&
                t.ValueKind == JsonValueKind.String && t.TryGetDateTimeOffset(out var parsed)
                ? parsed : null;
            var lastKnown = expiry ?? c.LastUpdate;
            return lastKnown != default && lastKnown.AddHours(5) < now;
        });
    }
}
