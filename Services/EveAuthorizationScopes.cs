using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

internal static class EveAuthorizationScopes
{
    internal static string[] Request(EvePilotProfile? existing, IEnumerable<string>? additional) =>
        EveSsoService.InitialScopes.Concat(existing?.Scopes ?? Array.Empty<string>())
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
