using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckOperationsLedger()
    {
        var now =
            new DateTimeOffset(
                2026,
                9,
                21,
                12,
                0,
                0,
                TimeSpan.Zero);

        var settings =
            new AppSettings
            {
                AccountCharacterMap =
                    new Dictionary<string, List<string>>
                    {
                        ["100"] =
                            new()
                            {
                                "1",
                                "2"
                            }
                    },
                AccountLabels =
                    new Dictionary<string, string>
                    {
                        ["100"] =
                            "Automatic Account"
                    },
                OperationsMinerGroups =
                    new Dictionary<string, List<string>>
                    {
                        ["Niko Fleet"] =
                            new()
                            {
                                "Miner One",
                                "External Corp Miner"
                            }
                    },
                OperationsSoloMiners =
                    new()
                    {
                        "Solo Corp Miner"
                    }
            };

        var pilots =
            new[]
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

        var mining =
            new[]
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
                }
            };

        ContractRow Buyback(
            long id,
            string issuer,
            decimal price,
            string status = "finished",
            string type = "item_exchange",
            bool janice = true,
            long corporation = 42) =>
            new()
            {
                CorporationId = corporation,
                Issuer = issuer,
                JaniceUrl =
                    janice
                        ? "https://janice.e-351.com/a/test"
                        : null,
                Contract =
                    new CorporationContract
                    {
                        Id = id,
                        IssuerId = id + 1000,
                        AssigneeId = corporation,
                        Type = type,
                        Status = status,
                        Accepted = now,
                        Price = price,
                        Issued = now.AddMinutes(-10)
                    }
            };

        var history =
            new[]
            {
                Buyback(
                    1,
                    "Miner One",
                    700),
                Buyback(
                    2,
                    "External Corp Miner",
                    300),
                Buyback(
                    3,
                    "Solo Corp Miner",
                    200),
                Buyback(
                    4,
                    "Miner Two",
                    999,
                    status: "outstanding"),
                Buyback(
                    5,
                    "Miner Two",
                    888,
                    janice: false),
                Buyback(
                    6,
                    "Miner Two",
                    777,
                    type: "courier")
            };

        IReadOnlyList<ContractRow> qualifying =
            BuybackReport.Qualifying(
                history,
                42,
                new DateTime(
                    2026,
                    9,
                    1),
                new DateTime(
                    2026,
                    10,
                    1));

        Check(
            qualifying.Count == 3,
            "Account Ledger reuses Buyback Report filtering and rejects outstanding, non-Janice and non-item-exchange contracts");

        OperationsLedgerSummary summary =
            OperationsLedgerService.Build(
                settings,
                pilots,
                mining,
                qualifying,
                _ => 10m);

        OperationsLedgerRow manual =
            summary.Rows.Single(row =>
                row.Group ==
                "Niko Fleet");

        OperationsLedgerRow automatic =
            summary.Rows.Single(row =>
                row.Group ==
                "Automatic Account");

        OperationsLedgerRow solo =
            summary.Rows.Single(row =>
                row.Group ==
                "Solo - Solo Corp Miner");

        Check(
            manual.MinedBuybackValue == 1000m &&
            manual.ContractedBackValue == 1000m &&
            manual.BuybackContracts == 2 &&
            manual.Characters.Contains("External Corp Miner"),
            "Manual miner groups combine local alts with external corporation miners");

        Check(
            automatic.MinedBuybackValue == 500m &&
            automatic.ContractedBackValue == 0m,
            "Characters without a manual override still use learned EVE-account grouping");

        Check(
            solo.ContractedBackValue == 200m &&
            solo.MinedBuybackValue == 0m &&
            solo.CoverageText == "N/A" &&
            solo.DifferenceText == "N/A",
            "Solo corporation miners remain separate and never fake 100 percent mining coverage");

        Check(
            manual.CoverageText == "100.0%" &&
            summary.UncontractedValue == 500m,
            "Uncontracted estimate counts only locally recorded mining gaps");

        Check(
            OperationsLedgerRow.FormatIsk(
                122_765_632_746m) ==
            "122.77b ISK",
            "Operations Ledger uses compact readable ISK totals");
    }
}