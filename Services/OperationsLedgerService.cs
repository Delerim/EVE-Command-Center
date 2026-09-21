using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

/// <summary>
/// Correlates existing mining history, contract history and learned EVE-account
/// associations. It does not replace Mining or Contracts; it is a read-only
/// reporting layer over those existing data sources.
/// </summary>
public static class OperationsLedgerService
{
    public static OperationsLedgerSummary Build(
        AppSettings settings,
        IReadOnlyList<EvePilotProfile> pilots,
        IReadOnlyList<MiningAggregateRow> mining,
        IReadOnlyList<ContractRow> contracts,
        Func<string, decimal> buybackUnitPrice)
    {
        var nameById =
            pilots
                .GroupBy(pilot => pilot.CharacterId)
                .ToDictionary(
                    group => group.Key.ToString(),
                    group => group.First().CharacterName,
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

            foreach (string characterId in pair.Value ?? new List<string>())
            {
                if (!nameById.TryGetValue(characterId, out string? name) ||
                    string.IsNullOrWhiteSpace(name))
                    continue;

                members.Add(name);
                accountByCharacter[name] = pair.Key;
            }

            if (members.Count > 0)
                accountMembers[pair.Key] = members;
        }

        string KeyFor(string character)
        {
            if (accountByCharacter.TryGetValue(character, out string? account))
                return "account:" + account;

            return "character:" + character.Trim();
        }

        string LabelFor(string key, IEnumerable<string> members)
        {
            if (key.StartsWith("account:", StringComparison.OrdinalIgnoreCase))
            {
                string id = key["account:".Length..];

                if (settings.AccountLabels.TryGetValue(id, out string? label) &&
                    !string.IsNullOrWhiteSpace(label))
                    return label.Trim();

                string[] names =
                    members
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToArray();

                return names.Length > 0
                    ? string.Join(" / ", names)
                    : "Account " + id;
            }

            return "Unlinked - " +
                   key["character:".Length..];
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

            string key = KeyFor(row.Character);

            if (!membersByGroup.TryGetValue(key, out var members))
                membersByGroup[key] =
                    members =
                        new HashSet<string>(
                            StringComparer.OrdinalIgnoreCase);

            members.Add(row.Character);

            decimal unit =
                Math.Max(
                    0m,
                    buybackUnitPrice(row.Ore));

            decimal value =
                (decimal)row.Units * unit;

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

        var lastAcceptedByGroup =
            new Dictionary<string, DateTimeOffset>(
                StringComparer.OrdinalIgnoreCase);

        foreach (ContractRow row in contracts)
        {
            if (!row.Contract.WasAccepted ||
                !row.Contract.Price.HasValue ||
                row.Contract.Price.Value <= 0 ||
                string.IsNullOrWhiteSpace(row.Issuer))
                continue;

            string key = KeyFor(row.Issuer);

            if (!membersByGroup.TryGetValue(key, out var members))
                membersByGroup[key] =
                    members =
                        new HashSet<string>(
                            StringComparer.OrdinalIgnoreCase);

            members.Add(row.Issuer);

            contractedByGroup[key] =
                contractedByGroup.GetValueOrDefault(key) +
                row.Contract.Price.Value;

            contractCountByGroup[key] =
                contractCountByGroup.GetValueOrDefault(key) + 1;

            DateTimeOffset accepted =
                row.Contract.Accepted ??
                row.Contract.Completed ??
                row.Contract.Issued;

            if (!lastAcceptedByGroup.TryGetValue(key, out DateTimeOffset current) ||
                accepted > current)
                lastAcceptedByGroup[key] = accepted;
        }

        var keys =
            minedByGroup.Keys
                .Concat(contractedByGroup.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        var rows =
            keys
                .Select(key =>
                {
                    HashSet<string> members =
                        membersByGroup.TryGetValue(key, out var actual)
                            ? actual
                            : new HashSet<string>(
                                StringComparer.OrdinalIgnoreCase);

                    if (key.StartsWith("account:", StringComparison.OrdinalIgnoreCase))
                    {
                        string id = key["account:".Length..];

                        if (accountMembers.TryGetValue(id, out var known))
                            members.UnionWith(known);
                    }

                    return new OperationsLedgerRow
                    {
                        AccountKey = key,
                        Account = LabelFor(key, members),
                        Characters =
                            string.Join(
                                ", ",
                                members.OrderBy(
                                    name => name,
                                    StringComparer.OrdinalIgnoreCase)),
                        MinedBuybackValue =
                            minedByGroup.GetValueOrDefault(key),
                        ContractedValue =
                            contractedByGroup.GetValueOrDefault(key),
                        AcceptedContracts =
                            contractCountByGroup.GetValueOrDefault(key),
                        LastAccepted =
                            lastAcceptedByGroup.TryGetValue(
                                key,
                                out DateTimeOffset last)
                                ? last
                                : null
                    };
                })
                .OrderByDescending(row =>
                    Math.Max(
                        row.MinedBuybackValue,
                        row.ContractedValue))
                .ThenBy(row =>
                    row.Account,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        return new OperationsLedgerSummary
        {
            Rows = rows
        };
    }
}