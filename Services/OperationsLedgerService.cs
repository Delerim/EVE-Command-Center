using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

/// <summary>
/// Additive correlation over existing Mining and Contracts data. Manual
/// reporting groups may contain any corporation miner by character name.
/// Explicit solo markers override automatic EVE-account grouping.
/// </summary>
public static class OperationsLedgerService
{
    public static OperationsLedgerSummary Build(
        AppSettings settings,
        IReadOnlyList<EvePilotProfile> pilots,
        IReadOnlyList<MiningAggregateRow> mining,
        IReadOnlyList<ContractRow> qualifyingBuybacks,
        Func<string, decimal> buybackUnitPrice)
    {
        var nameById =
            pilots
                .GroupBy(
                    pilot =>
                        pilot.CharacterId)
                .ToDictionary(
                    group =>
                        group.Key.ToString(),
                    group =>
                        group.First().CharacterName,
                    StringComparer.OrdinalIgnoreCase);

        var accountByCharacter =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        var accountMembers =
            new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var pair in settings.AccountCharacterMap)
        {
            var members =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (string characterId in
                     pair.Value ??
                     new List<string>())
            {
                if (!nameById.TryGetValue(
                        characterId,
                        out string? name) ||
                    string.IsNullOrWhiteSpace(name))
                    continue;

                members.Add(name);
                accountByCharacter[name] =
                    pair.Key;
            }

            if (members.Count > 0)
                accountMembers[pair.Key] =
                    members;
        }

        var manualGroupByCharacter =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        var manualMembers =
            new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var pair in
                 settings.OperationsMinerGroups
                     .OrderBy(
                         pair =>
                             pair.Key,
                         StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;

            string group =
                pair.Key.Trim();

            var members =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (string raw in
                     pair.Value ??
                     new List<string>())
            {
                string name =
                    raw?.Trim() ??
                    "";

                if (name.Length == 0)
                    continue;

                members.Add(name);

                if (!manualGroupByCharacter.ContainsKey(name))
                    manualGroupByCharacter[name] =
                        group;
            }

            manualMembers[group] =
                members;
        }

        var solo =
            new HashSet<string>(
                settings.OperationsSoloMiners
                    .Where(name =>
                        !string.IsNullOrWhiteSpace(name))
                    .Select(name =>
                        name.Trim()),
                StringComparer.OrdinalIgnoreCase);

        string KeyFor(string character)
        {
            string name =
                character.Trim();

            if (manualGroupByCharacter.TryGetValue(
                    name,
                    out string? manual))
                return "manual:" +
                       manual;

            if (solo.Contains(name))
                return "solo:" +
                       name;

            if (accountByCharacter.TryGetValue(
                    name,
                    out string? account))
                return "account:" +
                       account;

            return "character:" +
                   name;
        }

        string LabelFor(
            string key,
            IEnumerable<string> members)
        {
            if (key.StartsWith(
                    "manual:",
                    StringComparison.OrdinalIgnoreCase))
                return key["manual:".Length..];

            if (key.StartsWith(
                    "solo:",
                    StringComparison.OrdinalIgnoreCase))
                return "Solo - " +
                       key["solo:".Length..];

            if (key.StartsWith(
                    "account:",
                    StringComparison.OrdinalIgnoreCase))
            {
                string id =
                    key["account:".Length..];

                if (settings.AccountLabels.TryGetValue(
                        id,
                        out string? label) &&
                    !string.IsNullOrWhiteSpace(label))
                    return label.Trim();

                string[] names =
                    members
                        .Where(name =>
                            !string.IsNullOrWhiteSpace(name))
                        .OrderBy(
                            name =>
                                name,
                            StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToArray();

                return names.Length > 0
                    ? string.Join(
                        " / ",
                        names)
                    : "Account " +
                      id;
            }

            return "Unassigned - " +
                   key["character:".Length..];
        }

        string KindFor(string key)
        {
            if (key.StartsWith(
                    "manual:",
                    StringComparison.OrdinalIgnoreCase))
                return "MANUAL GROUP";

            if (key.StartsWith(
                    "solo:",
                    StringComparison.OrdinalIgnoreCase))
                return "SOLO";

            if (key.StartsWith(
                    "account:",
                    StringComparison.OrdinalIgnoreCase))
                return "AUTO ACCOUNT";

            return "UNASSIGNED";
        }

        var minedByGroup =
            new Dictionary<string, decimal>(
                StringComparer.OrdinalIgnoreCase);

        var membersByGroup =
            new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);

        foreach (MiningAggregateRow row in mining)
        {
            if (string.IsNullOrWhiteSpace(row.Character) ||
                string.IsNullOrWhiteSpace(row.Ore) ||
                row.Units <= 0)
                continue;

            string key =
                KeyFor(row.Character);

            if (!membersByGroup.TryGetValue(
                    key,
                    out var members))
                membersByGroup[key] =
                    members =
                        new HashSet<string>(
                            StringComparer.OrdinalIgnoreCase);

            members.Add(
                row.Character.Trim());

            decimal unit =
                Math.Max(
                    0m,
                    buybackUnitPrice(
                        row.Ore));

            decimal value =
                (decimal)row.Units *
                unit;

            minedByGroup[key] =
                minedByGroup.GetValueOrDefault(key) +
                value;
        }

        var contractedByGroup =
            new Dictionary<string, decimal>(
                StringComparer.OrdinalIgnoreCase);

        var contractCountByGroup =
            new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);

        var lastBuybackByGroup =
            new Dictionary<string, DateTimeOffset>(
                StringComparer.OrdinalIgnoreCase);

        foreach (ContractRow row in qualifyingBuybacks)
        {
            if (!row.Contract.Price.HasValue ||
                row.Contract.Price.Value <= 0 ||
                string.IsNullOrWhiteSpace(row.Issuer))
                continue;

            string issuer =
                row.Issuer.Trim();

            string key =
                KeyFor(issuer);

            if (!membersByGroup.TryGetValue(
                    key,
                    out var members))
                membersByGroup[key] =
                    members =
                        new HashSet<string>(
                            StringComparer.OrdinalIgnoreCase);

            members.Add(issuer);

            contractedByGroup[key] =
                contractedByGroup.GetValueOrDefault(key) +
                row.Contract.Price.Value;

            contractCountByGroup[key] =
                contractCountByGroup.GetValueOrDefault(key) +
                1;

            DateTimeOffset accepted =
                row.Contract.Accepted ??
                row.Contract.Completed ??
                row.Contract.Issued;

            if (!lastBuybackByGroup.TryGetValue(
                    key,
                    out DateTimeOffset current) ||
                accepted > current)
                lastBuybackByGroup[key] =
                    accepted;
        }

        string[] keys =
            minedByGroup.Keys
                .Concat(
                    contractedByGroup.Keys)
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        var rows =
            keys
                .Select(key =>
                {
                    HashSet<string> members =
                        membersByGroup.TryGetValue(
                            key,
                            out var actual)
                            ? actual
                            : new HashSet<string>(
                                StringComparer.OrdinalIgnoreCase);

                    if (key.StartsWith(
                            "manual:",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        string group =
                            key["manual:".Length..];

                        if (manualMembers.TryGetValue(
                                group,
                                out var known))
                            members.UnionWith(known);
                    }
                    else if (key.StartsWith(
                                 "account:",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        string id =
                            key["account:".Length..];

                        if (accountMembers.TryGetValue(
                                id,
                                out var known))
                            members.UnionWith(known);
                    }

                    return new OperationsLedgerRow
                    {
                        GroupKey = key,
                        Group =
                            LabelFor(
                                key,
                                members),
                        GroupKind =
                            KindFor(key),
                        Characters =
                            string.Join(
                                ", ",
                                members.OrderBy(
                                    name =>
                                        name,
                                    StringComparer.OrdinalIgnoreCase)),
                        MinedBuybackValue =
                            minedByGroup.GetValueOrDefault(key),
                        ContractedBackValue =
                            contractedByGroup.GetValueOrDefault(key),
                        BuybackContracts =
                            contractCountByGroup.GetValueOrDefault(key),
                        LastBuyback =
                            lastBuybackByGroup.TryGetValue(
                                key,
                                out DateTimeOffset last)
                                ? last
                                : null
                    };
                })
                .OrderByDescending(row =>
                    Math.Max(
                        row.MinedBuybackValue,
                        row.ContractedBackValue))
                .ThenBy(
                    row =>
                        row.Group,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        return new OperationsLedgerSummary
        {
            Rows = rows
        };
    }
}