using System;
using System.Collections.Generic;
using System.Linq;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckBuybackIndexPolicy()
    {
        var rows = Enumerable.Range(1, 12).Select(id => new ContractRow
        {
            CorporationId = 42,
            Contract = new CorporationContract { Id = id }
        }).ToArray();
        var cached = new Dictionary<string, List<ContractItem>>
        {
            [BuybackAnalytics.Key(rows[0])] = new(),
            [BuybackAnalytics.Key(rows[3])] = new()
        };
        var next = BuybackIndexPolicy.Next(rows, cached);
        Check(next.Length == 5 && next[0].Contract.Id == 2 && next[1].Contract.Id == 3 && next[2].Contract.Id == 5,
            "Buyback indexing skips cached records and caps ESI requests at five contracts");
        Check(BuybackIndexPolicy.Next(rows.Take(2).ToArray(), cached).Length == 1,
            "Buyback indexing handles partial short batches");
        Check(BuybackIndexPolicy.ContractTimeout < BuybackIndexPolicy.BatchTimeout,
            "Contract index timeout precedes the bounded overall batch deadline");
    }
}
