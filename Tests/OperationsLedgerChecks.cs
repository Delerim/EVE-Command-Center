using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckOperationsLedger()
    {
        var settings = new AppSettings
        {
            AccountCharacterMap =
                new Dictionary<string, List<string>>
                {
                    ["100"] = new() { "1", "2" }
                },
            AccountLabels =
                new Dictionary<string, string>
                {
                    ["100"] = "Mining Account A"
                }
        };

        var pilots = new[]
        {
            new EvePilotProfile
            {
                CharacterId = 1,
                CharacterName = "Miner One"
            },
            new EvePilotProfile
            {
                CharacterId = 2,
                CharacterName = "Miner Two"
            }
        };

        var mining = new[]
        {
            new MiningAggregateRow
            {
                DayKey = "2026-09-21",
                Character = "Miner One",
                Ore = "Zeolites",
                Units = 100
            },
            new MiningAggregateRow
            {
                DayKey = "2026-09-21",
                Character = "Miner Two",
                Ore = "Zeolites",
                Units = 50
            },
            new MiningAggregateRow
            {
                DayKey = "2026-09-21",
                Character = "Unlinked Miner",
                Ore = "Zeolites",
                Units = 20
            }
        };

        var contracts = new[]
        {
            new ContractRow
            {
                Issuer = "Miner One",
                Contract =
                    new CorporationContract
                    {
                        Id = 1,
                        Status = "finished",
                        Accepted = DateTimeOffset.UtcNow,
                        Price = 1200
                    }
            },
            new ContractRow
            {
                Issuer = "Miner Two",
                Contract =
                    new CorporationContract
                    {
                        Id = 2,
                        Status = "finished",
                        Accepted = DateTimeOffset.UtcNow,
                        Price = 300
                    }
            }
        };

        OperationsLedgerSummary summary =
            OperationsLedgerService.Build(
                settings,
                pilots,
                mining,
                contracts,
                _ => 10m);

        OperationsLedgerRow linked =
            summary.Rows.Single(
                row => row.Account == "Mining Account A");

        OperationsLedgerRow unlinked =
            summary.Rows.Single(
                row => row.Account.Contains(
                    "Unlinked Miner",
                    StringComparison.OrdinalIgnoreCase));

        Check(
            linked.MinedBuybackValue == 1500m &&
            linked.ContractedValue == 1500m &&
            linked.AcceptedContracts == 2,
            "Operations ledger combines mining and accepted contracts across linked account characters");

        Check(
            linked.Characters.Contains("Miner One") &&
            linked.Characters.Contains("Miner Two"),
            "Operations ledger exposes all linked account characters");

        Check(
            unlinked.MinedBuybackValue == 200m &&
            unlinked.ContractedValue == 0m,
            "Operations ledger keeps unlinked miners visible without guessing account membership");
    }
}