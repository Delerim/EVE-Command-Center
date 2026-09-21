namespace EveCommandCenter.Models;

public sealed class OperationsLedgerRow
{
    public string AccountKey { get; init; } = "";
    public string Account { get; init; } = "";
    public string Characters { get; init; } = "";
    public decimal MinedBuybackValue { get; init; }
    public decimal ContractedValue { get; init; }
    public decimal GapValue => MinedBuybackValue - ContractedValue;
    public int AcceptedContracts { get; init; }
    public DateTimeOffset? LastAccepted { get; init; }

    public decimal CoveragePercent =>
        MinedBuybackValue > 0
            ? ContractedValue / MinedBuybackValue * 100m
            : ContractedValue > 0
                ? 100m
                : 0m;

    public string MinedText => MinedBuybackValue.ToString("N0") + " ISK";
    public string ContractedText => ContractedValue.ToString("N0") + " ISK";
    public string GapText => GapValue.ToString("+N0;-N0;0") + " ISK";
    public string CoverageText => CoveragePercent.ToString("N1") + "%";
    public string LastAcceptedText =>
        LastAccepted?.ToLocalTime().ToString("dd MMM HH:mm") ?? "-";

    public string GapColor =>
        GapValue > Math.Max(10_000_000m, MinedBuybackValue * 0.05m)
            ? "#FFD166"
            : GapValue < -Math.Max(10_000_000m, MinedBuybackValue * 0.05m)
                ? "#80BFFF"
                : "#74D6C9";
}

public sealed class OperationsLedgerSummary
{
    public IReadOnlyList<OperationsLedgerRow> Rows { get; init; } =
        Array.Empty<OperationsLedgerRow>();

    public decimal MinedBuybackValue =>
        Rows.Sum(row => row.MinedBuybackValue);

    public decimal ContractedValue =>
        Rows.Sum(row => row.ContractedValue);

    public decimal GapValue =>
        MinedBuybackValue - ContractedValue;

    public int AccountCount => Rows.Count;

    public int AcceptedContracts =>
        Rows.Sum(row => row.AcceptedContracts);
}