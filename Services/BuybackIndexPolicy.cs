using System;
using System.Collections.Generic;
using System.Linq;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

/// <summary>Bounded, optional ESI contract-content indexing. Never bulk-fetch the archive.</summary>
public static class BuybackIndexPolicy
{
    public const int BatchSize = 5;
    public static readonly TimeSpan ContractTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan BatchTimeout = TimeSpan.FromMinutes(4);

    public static ContractRow[] Next(
        IReadOnlyList<ContractRow> accepted,
        IReadOnlyDictionary<string, List<ContractItem>> indexed)
    {
        return accepted.Where(row => !indexed.ContainsKey(BuybackAnalytics.Key(row)))
            .Take(BatchSize).ToArray();
    }
}
