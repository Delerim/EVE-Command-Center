using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EveMultiPreview.Models;

public sealed class EveCharacterAttributesResponse
{
    [JsonPropertyName("charisma")]
    public int Charisma { get; set; }

    [JsonPropertyName("intelligence")]
    public int Intelligence { get; set; }

    [JsonPropertyName("memory")]
    public int Memory { get; set; }

    [JsonPropertyName("perception")]
    public int Perception { get; set; }

    [JsonPropertyName("willpower")]
    public int Willpower { get; set; }

    [JsonPropertyName("bonus_remaps")]
    public int? BonusRemaps { get; set; }

    [JsonPropertyName("last_remap_date")]
    public DateTimeOffset? LastRemapDate { get; set; }

    [JsonPropertyName("accrued_remap_cooldown_date")]
    public DateTimeOffset? AccruedRemapCooldownDate { get; set; }
}

public sealed class EveTrainingAttribute
{
    public int DogmaAttributeId { get; init; }
    public string Name { get; init; } = "";
    public string ShortName { get; init; } = "";
    public string Symbol { get; init; } = "";
    public string Accent { get; init; } = "#58D3B4";

    // ESI's character attributes are the character's CURRENT effective
    // attributes. When implant permission is available we subtract the
    // active attribute implant bonus to expose the underlying remap value.
    public int Total { get; init; }
    public int ImplantBonus { get; init; }
    public int? Raw { get; init; }

    public string RawText =>
        Raw.HasValue
            ? $"({Raw.Value} raw +{ImplantBonus} implant)"
            : "(raw requires implant access)";
}

public sealed class EveImplantView
{
    public int TypeId { get; init; }
    public string Name { get; init; } = "";
    public string BonusText { get; init; } = "";
}

public sealed class EveTrainingProfile
{
    public IReadOnlyList<EveTrainingAttribute> Attributes { get; init; } =
        Array.Empty<EveTrainingAttribute>();

    public IReadOnlyList<EveImplantView> Implants { get; init; } =
        Array.Empty<EveImplantView>();

    public int BonusRemaps { get; init; }
    public string StandardRemapText { get; init; } = "";
    public bool ImplantDataAvailable { get; init; }

    public int GetTotal(int dogmaAttributeId)
    {
        foreach (EveTrainingAttribute attribute in Attributes)
        {
            if (attribute.DogmaAttributeId == dogmaAttributeId)
                return attribute.Total;
        }

        return 0;
    }

    public double BestCurrentTrainingRate
    {
        get
        {
            int[] values = new int[Attributes.Count];

            for (int i = 0; i < Attributes.Count; i++)
                values[i] = Attributes[i].Total;

            Array.Sort(values);
            Array.Reverse(values);

            if (values.Length == 0)
                return 0;

            if (values.Length == 1)
                return values[0];

            return values[0] + values[1] / 2.0;
        }
    }
}
