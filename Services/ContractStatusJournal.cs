using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

// Observations are append-only; ESI does not retain every past contract status.
// The current contracts.json continues to serve the existing UI unchanged.
internal static class ContractStatusJournal
{
    internal sealed record Observation(
        DateTimeOffset ObservedUtc,
        long CorporationId,
        long ContractId,
        string Status,
        CorporationContract Contract,
        string Issuer,
        string Acceptor,
        string Location);

    internal static int Capture(
        string contractStateFile,
        long corporationId,
        IReadOnlyList<ContractRow> incoming,
        IEnumerable<ContractRow> previous,
        DateTimeOffset observedUtc)
    {
        if (corporationId <= 0) return 0;
        string directory = Path.GetDirectoryName(contractStateFile)
            ?? throw new ArgumentException("Contract history path must include a directory.", nameof(contractStateFile));
        string journal = Path.Combine(directory, "contract-status-history.jsonl");
        var old = previous.Where(r => r.CorporationId == corporationId && r.Contract.Id > 0)
            .GroupBy(r => r.Contract.Id).ToDictionary(g => g.Key, g => g.First());
        var current = incoming.Where(r => r.CorporationId == corporationId && r.Contract.Id > 0)
            .GroupBy(r => r.Contract.Id).Select(g => g.First()).ToArray();
        var observations = new List<Observation>();
        Observation From(ContractRow row) => new(
            observedUtc, corporationId, row.Contract.Id, row.Contract.Status,
            row.Contract, row.Issuer, row.Acceptor, row.Location);

        // On first upgrade preserve the states we already knew before ESI replaces them.
        // Never delete or overwrite a preexisting journal.
        if (!File.Exists(journal))
            observations.AddRange(old.Values.Select(From));
        foreach (ContractRow row in current)
        {
            if (!old.TryGetValue(row.Contract.Id, out ContractRow? prior) ||
                !string.Equals(prior.Contract.Status, row.Contract.Status, StringComparison.Ordinal))
                observations.Add(From(row));
        }
        if (observations.Count == 0) return 0;
        Directory.CreateDirectory(directory);
        string lines = string.Join("\n", observations.Select(o => JsonSerializer.Serialize(o))) + "\n";
        File.AppendAllText(journal, lines, new UTF8Encoding(false));
        return observations.Count;
    }
}
