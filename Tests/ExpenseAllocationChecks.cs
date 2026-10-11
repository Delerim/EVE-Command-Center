using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;
using Microsoft.Data.Sqlite;
internal static partial class Program
{
    private static void CheckExpenseAllocations(string root, string? renderPath)
    {
        string folder = Path.Combine(root, "expenses"); var store = new IndustryWorkspaceStore(folder); var pilotsSet = new HashSet<long> { 42, 43 };
        var project = store.Create("Obelisk production", 20187, 1, null, pilotsSet);
        var second = store.Create("Pacifier production", 44993, 1, null, pilotsSet);
        var ledger = new IndustryLedger(folder); ledger.Ensure(); var now = DateTimeOffset.UtcNow;
        EveWalletJournalEntry Entry(long id, string type, decimal? amount) => new() { Id = id, Date = now.AddMinutes(-5), RefType = type, Description = "Confirmed wallet activity", Amount = amount, Tax = 5 };
        var charge = Entry(1, "brokers_fee", -100.125m);
        ledger.ImportJournal(42, new() { charge, Entry(2, "market_transaction", -500), Entry(3, "player_donation", -10), Entry(4, "manufacturing", -300), Entry(5, "transaction_tax", null), Entry(6, "transaction_tax", 50) }, now, now.AddHours(1));
        ledger.ImportJournal(43, new() { charge }, now, now.AddHours(1));
        using var http = new HttpClient(new MarketHandler()); var service = new MarketOrdersService(new EveSsoService(), folder, http);
        ExpenseAllocation Split(Guid id, decimal amount) => new(Guid.NewGuid(), id, "ignored stale name", amount, "Shared listing fee");
        var a = Split(project, 60.125m); var b = Split(second, 40);
        var saved = service.SaveExpenseAllocation(42, 1, 0, new[] { a, b }); a = saved.Splits.Single(s => s.ProjectId == project); b = saved.Splits.Single(s => s.ProjectId == second);
        Check(saved.Revision == 1 && saved.Splits.Sum(s => s.Amount) == 100.125m && a.ProjectName == "Obelisk production", "Charge splits preserve decimal evidence and authoritative project names without adding the tax metadata twice");
        Check(service.SaveExpenseAllocation(42, 1, 1, saved.Splits).Revision == 1 && service.ExpenseHistory(42, 1).Count == 1, "Repeating an unchanged expense assignment creates no extra cost or history");
        bool RejectExpense(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        Check(RejectExpense(() => service.SaveExpenseAllocation(42, 1, 0, new[] { a })), "Stale expense edits cannot replace a newer split");
        Check(RejectExpense(() => service.SaveExpenseAllocation(42, 1, 1, new[] { a with { Amount = 101 } })), "Assigned expense amounts cannot exceed their confirmed debit");
        Check(RejectExpense(() => service.SaveExpenseAllocation(42, 1, 1, new[] { a with { Amount = 0 } })) && RejectExpense(() => service.SaveExpenseAllocation(42, 1, 1, new[] { a, a })), "Zero amounts and repeated split identities are rejected");
        Check(RejectExpense(() => service.SaveExpenseAllocation(42, 2, 0, new[] { a })) && RejectExpense(() => service.SaveExpenseAllocation(42, 3, 0, new[] { a })), "Trade payments and transfers cannot be assigned a second time as project charges");
        Check(RejectExpense(() => service.SaveExpenseAllocation(42, 5, 0, new[] { a })) && RejectExpense(() => service.SaveExpenseAllocation(42, 6, 0, new[] { a })), "Missing amounts and incoming credits cannot become expenses");
        Check(RejectExpense(() => service.SaveExpenseAllocation(42, 999, 0, new[] { a })) && RejectExpense(() => service.SaveExpenseAllocation(42, 4, 0, new[] { a with { ProjectId = Guid.NewGuid() } })), "Expense assignment requires an imported source and active destination");
        service.SaveExpenseAllocation(43, 1, 0, new[] { Split(project, 25) });
        Check(store.ProjectExpenses(project).Sum(e => e.Allocation.Amount) == 85.125m, "Same journal identifier on different pilots retains separate expenses");
        var filtered = ExpenseEvidence.Filter(store.ProjectExpenses(project), now.AddHours(-1), now, 42);
        Check(filtered.Length == 1 && filtered[0].Allocation.Amount == 60.125m && ExpenseEvidence.Filter(store.ProjectExpenses(project), now, now.AddHours(1), null).Length == 0, "Project charge filters apply the same pilot and date boundaries as trades");
        using (var c = new SqliteConnection($"Data Source={ledger.PathName};Pooling=False"))
        { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TRIGGER expense_failure BEFORE INSERT ON expense_allocation_audit BEGIN SELECT RAISE(ABORT,'fixture'); END"; cmd.ExecuteNonQuery(); }
        bool rollback = false; try { service.SaveExpenseAllocation(42, 1, 1, new[] { a }); } catch (SqliteException) { rollback = true; }
        Check(rollback && service.ExpenseAllocation(42, 1).Splits.Count == 2 && service.ExpenseHistory(42, 1).Count == 1, "Expense audit failure rolls back the revision and financial assignments together");
        using (var c = new SqliteConnection($"Data Source={ledger.PathName};Pooling=False")) { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DROP TRIGGER expense_failure"; cmd.ExecuteNonQuery(); }
        string oldFolder = Path.Combine(root, "expense-upgrade"); Directory.CreateDirectory(oldFolder);
        File.Copy(ledger.PathName, Path.Combine(oldFolder, "industry-ledger.db")); File.Copy(Path.Combine(folder, "industry-workspace.json"), Path.Combine(oldFolder, "industry-workspace.json"));
        using (var c = new SqliteConnection($"Data Source={Path.Combine(oldFolder, "industry-ledger.db")};Pooling=False")) { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DROP TABLE expense_allocations; DROP TABLE expense_allocation_audit; PRAGMA user_version=5"; cmd.ExecuteNonQuery(); }
        var migrated = new MarketOrdersService(new EveSsoService(), oldFolder, http);
        Check(migrated.Journal.Count == 7 && new IndustryWorkspaceStore(oldFolder).Snapshot().Projects.Count == 2 && File.Exists(Path.Combine(oldFolder, "industry-ledger.db.v5.bak")), "Expense migration preserves populated wallet and planning history with a version-five backup");
        using (var c = new SqliteConnection($"Data Source={Path.Combine(oldFolder, "industry-ledger.db.v5.bak")};Pooling=False")) { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version"; Check(Convert.ToInt32(cmd.ExecuteScalar()) == 5, "Expense migration backup retains the prior schema"); }
        var reopened = new MarketOrdersService(new EveSsoService(), folder, http); reopened.Select(new[] { 42L });
        Check(reopened.ExpenseAllocation(42, 1).Splits.Count == 2 && reopened.ExpenseHistory(42, 1).Count == 1, "Saved charge splits and audit survive restart");
        var pilots = new[] { new EvePilotProfile { CharacterId = 42, CharacterName = "Industry pilot", Scopes = new[] { MarketOrdersService.WalletScope } }, new EvePilotProfile { CharacterId = 43, CharacterName = "Market pilot", Scopes = new[] { MarketOrdersService.WalletScope } } };
        var view = new MarketOrdersWindow { Width = 1550, Height = 1250 }; view.Initialize(reopened, pilots);
        ((TabControl)view.FindName("MarketTabs")).SelectedIndex = 6; var journal = (DataGrid)view.FindName("JournalGrid");
        object JournalItem(long id) => journal.Items.Cast<object>().Single(o => ((EveWalletJournalEntry)o.GetType().GetProperty("Entry")!.GetValue(o)!).Id == id);
        journal.SelectedItem = JournalItem(1);
        Check(((Border)view.FindName("ExpensePanel")).Visibility == Visibility.Visible && ((DataGrid)view.FindName("ExpenseSplits")).Items.Count == 2, "Selecting an eligible wallet charge opens the project split editor");
        if (renderPath != null) Render(view, Path.ChangeExtension(renderPath, ".expense-editor.png"));
        journal.SelectedItem = JournalItem(2);
        Check(((Border)view.FindName("ExpensePanel")).Visibility == Visibility.Collapsed, "Trade wallet entries do not expose expense assignment");
        journal.SelectedItem = JournalItem(4); ((ComboBox)view.FindName("ExpenseTarget")).SelectedIndex = 0; ((TextBox)view.FindName("ExpenseAmount")).Text = "300";
        Check(((Button)view.FindName("SaveExpense")).IsEnabled, "Valid project and amount enable charge assignment");
        ((Button)view.FindName("SaveExpense")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(reopened.ExpenseAllocation(42, 4).Splits.Single().Amount == 300 && ((TextBlock)view.FindName("ExpenseMessage")).Text.StartsWith("Saved"), "Saving the charge editor persists the split and gives feedback");
        ((TabControl)view.FindName("MarketTabs")).SelectedIndex = 0;
        Check(((ColumnDefinition)view.FindName("AllocationWidth")).Width.Value == 0, "Leaving wallet activity restores the full table width"); view.Close();
        var workspace = new IndustryWorkspaceView(); workspace.Initialize(store, () => Array.Empty<IndustryPilot>(), pilots.ToList());
        ((ListBox)workspace.FindName("Projects")).SelectedIndex = 0; ((TabControl)workspace.FindName("WorkspaceTabs")).SelectedItem = workspace.FindName("ProjectFinancesTab");
        Check(((TextBlock)workspace.FindName("FinanceExpenses")).Text.Contains("385.13") && ((TextBlock)workspace.FindName("FinanceDifference")).Text.Contains("-385.13"), "Project finances include assigned charges even when no purchases or sales are linked");
        ((TabControl)workspace.FindName("FinanceActivity")).SelectedIndex = 1;
        Check(((DataGrid)workspace.FindName("ProjectExpenseEntries")).Items.Count == 3, "Project charge table retains each pilot and split as inspectable evidence");
        if (renderPath != null) { var shell = new Window { Content = workspace, Width = 1450, Height = 900 }; workspace.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { ((TabControl)workspace.FindName("WorkspaceTabs")).SelectedItem = workspace.FindName("ProjectFinancesTab"); ((TabControl)workspace.FindName("FinanceActivity")).SelectedIndex = 1; })); Render(shell, Path.ChangeExtension(renderPath, ".expense-finances.png")); shell.Close(); }
        store.DeleteProject(project);
        Check(service.ExpenseAllocation(42, 1).Splits.Contains(a) && service.Journal.Count == 7, "Deleting a project preserves original charges and historical assignments");
        Check(RejectExpense(() => service.SaveExpenseAllocation(42, 1, 1, new[] { a with { Amount = 59 }, b })), "Removed projects cannot receive changed expense assignments");
        service.SaveExpenseAllocation(42, 1, 1, new[] { b });
        Check(store.ProjectExpenses(project).Sum(e => e.Allocation.Amount) == 325 && service.ExpenseHistory(42, 1).Count == 2, "Removing a split reverses only that cost and preserves audit evidence");
    }
}
