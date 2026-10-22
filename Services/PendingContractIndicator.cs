using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

/// <summary>
/// The warning is a persistent outstanding-acceptance indicator, not an unread
/// notification. A contract stays visible after being viewed until ESI reports
/// acceptance, removal or expiry. Does not create network requests.
/// </summary>
internal static class PendingContractIndicator
{
    internal readonly record struct Snapshot(int Count, string ToolTip);

    internal static Snapshot Summarize(ContractState? state, DateTimeOffset now)
    {
        // Do not claim there are no contracts before the first successful fetch.
        if (state is null || state.CorporationId <= 0 || state.LastRefreshUtc is null)
            return new(0, "Contracts have not been checked yet");

        long corp = state.CorporationId;
        int count = (state.Rows ?? new List<ContractRow>())
            .Where(row => row is not null && row.CorporationId == corp &&
                row.Contract is not null && row.Contract.Id > 0 &&
                row.Contract.AssigneeId == corp &&
                string.Equals(row.Contract.Status, "outstanding", StringComparison.OrdinalIgnoreCase) &&
                !row.Contract.WasAccepted && row.Contract.Expires > now)
            .Select(row => row.Contract.Id)
            .Distinct()
            .Count();

        string observation = state.LastRefreshUtc.Value.ToLocalTime().ToString("dd MMM HH:mm");
        string ageNote = now - state.LastRefreshUtc.Value > TimeSpan.FromMinutes(30)
            ? " (cached ESI data; may be stale)" : "";
        string tooltip = count > 0
            ? $"{count} corporation contract{(count == 1 ? "" : "s")} awaiting acceptance. Last ESI check: {observation}{ageNote}."
            : $"No corporation contracts awaiting acceptance. Last ESI check: {observation}{ageNote}.";
        return new(count, tooltip);
    }
}
