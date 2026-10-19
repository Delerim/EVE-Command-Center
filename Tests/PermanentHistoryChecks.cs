using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckPermanentHistoricalRetention()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "ecc-permanent-history-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string contractsDir = Path.Combine(root, "Contracts");
            string state = Path.Combine(contractsDir, "contracts.json");
            ContractRow Row(long id, string status) => new()
            {
                CorporationId = 42, Issuer = "Synthetic supplier", Location = "Test location",
                Contract = new CorporationContract
                {
                    Id = id, Status = status, Issued = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    Price = 1234567m
                }
            };
            var old = new[] { Row(1001, "outstanding") };
            var fresh = new[] { Row(1001, "finished"), Row(1002, "outstanding") };
            DateTimeOffset observed = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
            int written = ContractStatusJournal.Capture(state, 42, fresh, old, observed);
            string journal = Path.Combine(contractsDir, "contract-status-history.jsonl");
            var lines = File.ReadAllLines(journal);
            Check(written == 3 && lines.Length == 3 &&
                lines.Any(line => line.Contains("outstanding")) &&
                lines.Any(line => line.Contains("finished")),
                "Contract journal preserves pre-upgrade status, transition and newly observed contract");
            Check(ContractStatusJournal.Capture(state, 42, fresh, fresh, observed.AddHours(1)) == 0 &&
                File.ReadAllLines(journal).Length == 3,
                "Contract journal does not append a new event for an unchanged ESI refresh");
            Check(ContractStatusJournal.Capture(state, 42,
                new[] { Row(1001, "finished"), Row(1002, "finished") }, fresh,
                observed.AddDays(365)) == 1 && File.ReadAllLines(journal).Length == 4,
                "Contract changes remain append-only across year boundaries");

            string pi = Path.Combine(root, "PiHistory");
            using JsonDocument layout = JsonDocument.Parse("{\"pins\":[{\"pin_id\":12345}],\"synthetic\":true}");
            PiColony MakeColony(DateTimeOffset time) => new()
            {
                CharacterId = 314, Character = "Synthetic", PlanetId = 159,
                Planet = "Synthetic planet", Fetched = time,
                Layout = layout.RootElement.Clone()
            };
            var day1 = MakeColony(observed);
            Check(PiDailySnapshotJournal.Capture(day1, pi),
                "PI captures the first observed colony layout of the UTC day");
            Check(!PiDailySnapshotJournal.Capture(MakeColony(observed.AddMinutes(12)), pi) &&
                Directory.GetFiles(pi, "*.jsonl").Length == 1 &&
                File.ReadAllLines(Path.Combine(pi, "2026-10-19.jsonl")).Length == 1,
                "PI history does not bloat from every ten-minute refresh");
            Check(PiDailySnapshotJournal.Capture(MakeColony(observed.AddYears(1)), pi) &&
                Directory.GetFiles(pi, "*.jsonl").Length == 2 &&
                File.ReadAllText(Path.Combine(pi, "2026-10-19.jsonl")).Contains("pins"),
                "PI retains full observed layouts from earlier years without overwriting them");
            Check(CloudHistoryAuditService.Categorize("local/PiHistory/2026-10-19.jsonl") == "Planetary industry" &&
                CloudHistoryAuditService.Categorize("local/Contracts/contract-status-history.jsonl") == "Contracts / buyback",
                "New long-lived journals appear in the correct history audit categories");

            string mining = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),
                "Services", "MiningHistoryService.cs"));
            string moons = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),
                "Services", "MoonReportService.cs"));
            Check(!mining.Contains("CleanupOldLiveJsonl(currentKey);") &&
                  !mining.Contains("_archive.Rows.RemoveAll(r => string.CompareOrdinal(r.DayKey, oldestKey) < 0);") &&
                  !mining.Contains("File.Delete(file);"),
                "Mining archive no longer deletes completed daily JSONL or prunes one-year aggregate rows");
            Check(!moons.Contains("_state.LedgerHistory.Remove(key);") &&
                  !moons.Contains("_state.Pulls.Remove(key);"),
                "Moon collection no longer prunes permanently observed pulls and ledger history");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
