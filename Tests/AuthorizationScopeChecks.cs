using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckAuthorizationScopes()
    {
        var firstLink = EveAuthorizationScopes.Request(null, null);
        Check(ContractService.Scopes.Concat(IndustryService.Scopes).Concat(new[] { PlanetaryService.Scope, OmegaService.Scope, MoonReportService.MiningScope, MoonReportService.StructureScope, MoonReportService.FuelAssetsScope }).All(firstLink.Contains),
            "First character link requests permissions for every current feature");
        Check(!EveAuthorizationScopes.Complete(new EvePilotProfile { Scopes = EveSsoService.InitialScopes }) && EveAuthorizationScopes.Complete(new EvePilotProfile { Scopes = firstLink }),
            "Existing basic links are identified for a single all-feature upgrade");
        var pilot = new EvePilotProfile { CharacterId = 42, CharacterName = "Reader", Scopes = new[] { ContractService.ReadScope, PlanetaryService.Scope, OmegaService.Scope } };
        var request = EveAuthorizationScopes.Request(pilot, new[] { MoonReportService.MiningScope, ContractService.ReadScope });
        Check(pilot.Scopes.All(request.Contains) && request.Contains(MoonReportService.MiningScope) && request.Length == request.Distinct().Count(), "Reconnecting a reader preserves other feature scopes without duplicates");
        bool rejected = false;
        try { EveAuthorizationScopes.ValidateReplacement(pilot, EveSsoService.InitialScopes, 42, null); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Generic add cannot overwrite an existing reader with a reduced grant");
        rejected = false;
        try { EveAuthorizationScopes.ValidateReplacement(pilot, request, 43, 42); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Selected-character reconnect rejects the wrong SSO character");
        EveAuthorizationScopes.ValidateReplacement(pilot, request, 42, 42);
        EveAuthorizationScopes.ValidateReplacement(null, EveSsoService.InitialScopes, 99, null);
        Check(true, "Complete reconnects and new-character grants remain valid");
    }
}
