using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

internal static class EveAuthorizationScopes
{
    internal static readonly string[] All = EveSsoService.InitialScopes
        .Concat(ContractService.Scopes)
        .Concat(IndustryService.Scopes)
        .Concat(new[] { PlanetaryService.Scope, OmegaService.Scope, MoonReportService.MiningScope, MoonReportService.StructureScope, MoonReportService.FuelAssetsScope })
        .Distinct(StringComparer.Ordinal).ToArray();
    internal static bool Complete(EvePilotProfile pilot) => All.All(pilot.Scopes.Contains);
    internal static string[] Request(EvePilotProfile? existing, IEnumerable<string>? additional) =>
        All.Concat(existing?.Scopes ?? Array.Empty<string>())
            .Concat(additional ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();

    internal static void ValidateReplacement(EvePilotProfile? existing, IEnumerable<string> granted, long returnedId, long? selectedId)
    {
        if (selectedId is > 0 && selectedId != returnedId)
            throw new InvalidOperationException("EVE returned a different character. Select that character in the app before reconnecting it.");
        var missing = (existing?.Scopes ?? Array.Empty<string>()).Except(granted, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"This authorization would remove existing permissions for {existing!.CharacterName}. The saved connection was not replaced. Reconnect from a panel with this character selected and approve all requested permissions.");
    }
}
