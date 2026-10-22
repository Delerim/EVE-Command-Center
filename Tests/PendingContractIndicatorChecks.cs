using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckPendingContractIndicator()
    {
        var now = new DateTimeOffset(2026, 10, 22, 11, 0, 0, TimeSpan.Zero);
        const long corp = 2001;
        ContractRow Make(long id, string status = "outstanding", long assignee = corp,
            DateTimeOffset? expires = null, DateTimeOffset? accepted = null,
            long corporationId = corp) => new()
        {
            CorporationId = corporationId,
            Contract = new CorporationContract {
                Id = id, Status = status, AssigneeId = assignee,
                Expires = expires ?? now.AddDays(1), Accepted = accepted
            }
        };
        var state = new ContractState {
            CorporationId = corp, LastRefreshUtc = now.AddMinutes(-5), Rows = new()
        };
        Check(PendingContractIndicator.Summarize(null, now).Count == 0 &&
              PendingContractIndicator.Summarize(new ContractState { CorporationId=corp }, now).Count == 0,
              "Pending-contract badge is hidden before the first successful ESI refresh");
        Check(PendingContractIndicator.Summarize(state, now).Count == 0,
              "No outstanding contracts keeps the badge hidden");
        state.Rows.Add(Make(1));
        Check(PendingContractIndicator.Summarize(state, now) is { Count: 1 } one &&
              one.ToolTip.Contains("1 corporation contract awaiting acceptance"),
              "One outstanding corporation contract shows the yellow badge with a count");
        state.Rows.Add(Make(2));
        state.Rows.Add(Make(2));
        state.Rows.Add(Make(3,"in_progress"));
        state.Rows.Add(Make(4,expires:now.AddMinutes(-1)));
        state.Rows.Add(Make(5,assignee:9999));
        state.Rows.Add(Make(6,accepted:now.AddMinutes(-3)));
        state.Rows.Add(Make(7,corporationId:3333));
        Check(PendingContractIndicator.Summarize(state, now).Count == 2,
              "Only distinct, unaccepted, nonexpired contracts for the linked corporation count");
        state.LastRefreshUtc = now.AddHours(-1);
        Check(PendingContractIndicator.Summarize(state, now).ToolTip.Contains("stale"),
              "Outdated cached ESI contract states are clearly labelled as potentially stale");
        state.Rows.Clear();
        Check(PendingContractIndicator.Summarize(state, now).Count == 0,
              "Accepted or removed contracts disappear after the refreshed outstanding list clears");
    }
}
