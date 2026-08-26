using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EveMultiPreview.Models;

public sealed class EveAssetItem
{
    [JsonPropertyName("is_blueprint_copy")]
    public bool? IsBlueprintCopy { get; set; }

    [JsonPropertyName("is_singleton")]
    public bool IsSingleton { get; set; }

    [JsonPropertyName("item_id")]
    public long ItemId { get; set; }

    [JsonPropertyName("location_flag")]
    public string LocationFlag { get; set; } = "";

    [JsonPropertyName("location_id")]
    public long LocationId { get; set; }

    [JsonPropertyName("location_type")]
    public string LocationType { get; set; } = "";

    [JsonPropertyName("quantity")]
    public long Quantity { get; set; }

    [JsonPropertyName("type_id")]
    public int TypeId { get; set; }
}

public sealed class EveFitting
{
    [JsonPropertyName("fitting_id")]
    public int FittingId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("ship_type_id")]
    public int ShipTypeId { get; set; }

    [JsonPropertyName("items")]
    public List<EveFittingItem> Items { get; set; } = new();
}

public sealed class EveFittingItem
{
    [JsonPropertyName("flag")]
    public string Flag { get; set; } = "";

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("type_id")]
    public int TypeId { get; set; }
}

public sealed class EveUniverseName
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public sealed class EveCurrentShipView
{
    public string TypeName { get; init; } = "";
    public string CustomName { get; init; } = "";
    public long ShipItemId { get; init; }
    public int ShipTypeId { get; init; }

    public bool IsOrca =>
        ShipTypeId == 28606 ||
        TypeName.Equals(
            "Orca",
            StringComparison.OrdinalIgnoreCase);

    public string DisplayName =>
        string.IsNullOrWhiteSpace(CustomName) ||
        CustomName.Equals(
            TypeName,
            StringComparison.OrdinalIgnoreCase)
            ? TypeName
            : $"{CustomName} | {TypeName}";

    public string DetailText =>
        ShipItemId <= 0
            ? ""
            : $"Item {ShipItemId}";
}

public sealed class EveShipModuleView
{
    public int TypeId { get; init; }
    public string Slot { get; init; } = "";
    public string Name { get; init; } = "";
    public long Quantity { get; init; }

    public string IconUrl =>
        TypeId > 0
            ? $"https://images.evetech.net/types/{TypeId}/icon?size=64"
            : "";

    public string SlotAccent
    {
        get
        {
            if (Slot.StartsWith(
                    "High",
                    StringComparison.OrdinalIgnoreCase))
                return "#E7B85A";

            if (Slot.StartsWith(
                    "Mid",
                    StringComparison.OrdinalIgnoreCase))
                return "#64C7FF";

            if (Slot.StartsWith(
                    "Low",
                    StringComparison.OrdinalIgnoreCase))
                return "#D693FF";

            if (Slot.StartsWith(
                    "Rig",
                    StringComparison.OrdinalIgnoreCase))
                return "#FF8FA6";

            if (Slot.Contains(
                    "Drone",
                    StringComparison.OrdinalIgnoreCase) ||
                Slot.Contains(
                    "Fighter",
                    StringComparison.OrdinalIgnoreCase))
                return "#58D3B4";

            return "#8DA9A2";
        }
    }
}

public sealed class EveAssetView
{
    public long ItemId { get; init; }
    public string Name { get; init; } = "";
    public string Quantity { get; init; } = "";
    public string Location { get; init; } = "";
    public string Flag { get; init; } = "";
}

public sealed class EveFittingView
{
    public int FittingId { get; init; }
    public int ShipTypeId { get; init; }
    public string Name { get; init; } = "";
    public string Ship { get; init; } = "";
    public string Items { get; init; } = "";
    public string Description { get; init; } = "";

    public IReadOnlyList<EveShipModuleView> Modules { get; init; } =
        Array.Empty<EveShipModuleView>();
}

public sealed class EveFitDefenseStats
{
    public bool Available { get; init; }
    public double ShieldHp { get; init; }
    public double ArmorHp { get; init; }
    public double StructureHp { get; init; }
    public double OmniEhp { get; init; }

    public string EhpText
    {
        get
        {
            if (!Available || OmniEhp <= 0)
                return "EHP --";

            if (OmniEhp >= 1000000)
                return $"EHP ~{OmniEhp / 1000000.0:0.00}m";

            if (OmniEhp >= 1000)
                return $"EHP ~{OmniEhp / 1000.0:0.#}k";

            return $"EHP ~{OmniEhp:0}";
        }
    }

    public string ToolTip =>
        !Available
            ? "Fit EHP estimate unavailable."
            : $"Fit EHP estimate: {OmniEhp:N0}\n" +
              $"Shield: {ShieldHp:N0} HP\n" +
              $"Armor: {ArmorHp:N0} HP\n" +
              $"Structure: {StructureHp:N0} HP\n\n" +
              "Uniform 25/25/25/25 damage profile. Includes common fitted HP/resistance modules and core HP skills. " +
              "Assumes fitted hardeners are active. Fleet boosts, heat, boosters and most implants are not included yet.";
}
public sealed class EveInventorySnapshot
{
    public EveCurrentShipView CurrentShip { get; init; } = new();

    public IReadOnlyList<EveShipModuleView> CurrentShipModules { get; init; } =
        Array.Empty<EveShipModuleView>();

    public IReadOnlyList<EveAssetView> Assets { get; init; } =
        Array.Empty<EveAssetView>();

    public IReadOnlyList<EveFittingView> Fittings { get; init; } =
        Array.Empty<EveFittingView>();

    public EveFitDefenseStats CurrentFitStats { get; init; } = new();

    public bool AssetsAvailable { get; init; }
    public bool FittingsAvailable { get; init; }
    public string AccessMessage { get; init; } = "";
}

public sealed class EveMiningShipIntel
{
    public long CharacterId { get; init; }
    public string CharacterName { get; init; } = "";
    public EveCurrentShipView CurrentShip { get; init; } = new();

    // -1 = asset permission unavailable, so fitted laser count is unknown.
    public int MiningLaserCount { get; init; } = -1;

    public IReadOnlyList<double> MiningLaserBaseCyclesSeconds { get; init; } =
        Array.Empty<double>();

    public EveFitDefenseStats Defense { get; init; } = new();

    public bool AssetsAvailable { get; init; }

    public bool IsOrca => CurrentShip.IsOrca;

    public double? RepresentativeLaserBaseCycleSeconds =>
        MiningLaserBaseCyclesSeconds.Count == 0
            ? null
            : MiningLaserBaseCyclesSeconds.Average();
}
