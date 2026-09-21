namespace EveCommandCenter.Models;

public sealed class OperationsLedgerRow
{
    public string GroupKey { get; init; } = "";
    public string Group { get; init; } = "";
    public string GroupKind { get; init; } = "";
    public string Characters { get; init; } = "";
    public decimal MinedBuybackValue { get; init; }
    public decimal ContractedBackValue { get; init; }
    public int BuybackContracts { get; init; }
    public DateTimeOffset? LastBuyback { get; init; }

    public bool HasMiningData =>
        MinedBuybackValue > 0;

    public decimal DifferenceValue =>
        MinedBuybackValue -
        ContractedBackValue;

    public decimal? CoveragePercent =>
        HasMiningData
            ? ContractedBackValue /
              MinedBuybackValue *
              100m
            : null;

    public string MinedText =>
        FormatIsk(MinedBuybackValue);

    public string ContractedBackText =>
        FormatIsk(ContractedBackValue);

    public string DifferenceText =>
        !HasMiningData
            ? "N/A"
            : DifferenceValue >= 0
                ? FormatIsk(DifferenceValue)
                : "OVER " +
                  FormatIsk(-DifferenceValue);

    public string CoverageText =>
        CoveragePercent.HasValue
            ? CoveragePercent.Value.ToString("N1") + "%"
            : "N/A";

    public string LastBuybackText =>
        LastBuyback?
            .ToLocalTime()
            .ToString("dd MMM HH:mm") ??
        "-";

    public string DifferenceColor =>
        !HasMiningData
            ? "#6F9497"
            : DifferenceValue >
              Math.Max(
                  10_000_000m,
                  MinedBuybackValue * 0.05m)
                ? "#FFD166"
                : DifferenceValue <
                  -Math.Max(
                      10_000_000m,
                      MinedBuybackValue * 0.05m)
                    ? "#80BFFF"
                    : "#74D6C9";

    public static string FormatIsk(decimal value)
    {
        decimal absolute =
            Math.Abs(value);

        string text =
            absolute >= 1_000_000_000_000m
                ? (absolute / 1_000_000_000_000m).ToString("0.##") + "t"
                : absolute >= 1_000_000_000m
                    ? (absolute / 1_000_000_000m).ToString("0.##") + "b"
                    : absolute >= 1_000_000m
                        ? (absolute / 1_000_000m).ToString("0.##") + "m"
                        : absolute >= 1_000m
                            ? (absolute / 1_000m).ToString("0.##") + "k"
                            : absolute.ToString("N0");

        return (value < 0 ? "-" : "") +
               text +
               " ISK";
    }
}

public sealed class OperationsLedgerSummary
{
    public IReadOnlyList<OperationsLedgerRow> Rows { get; init; } =
        Array.Empty<OperationsLedgerRow>();

    public decimal MinedBuybackValue =>
        Rows.Sum(
            row =>
                row.MinedBuybackValue);

    public decimal ContractedBackValue =>
        Rows.Sum(
            row =>
                row.ContractedBackValue);

    public decimal UncontractedValue =>
        Rows
            .Where(row => row.HasMiningData)
            .Sum(row =>
                Math.Max(
                    0m,
                    row.DifferenceValue));

    public decimal OverRecordedValue =>
        Rows
            .Where(row => row.HasMiningData)
            .Sum(row =>
                Math.Max(
                    0m,
                    -row.DifferenceValue));

    public int GroupCount =>
        Rows.Count;

    public int BuybackContracts =>
        Rows.Sum(
            row =>
                row.BuybackContracts);
}