using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;

internal static partial class Program
{
    private static void CheckJaniceProfitability(string? renderPath = null)
    {
        string fixture = File.ReadAllText(Path.Combine("Tests", "Fixtures", "janice-2coDfZ.json"));
        using var doc = JsonDocument.Parse(fixture);
        var good = JaniceProfitability.Parse(doc.RootElement);
        using var badDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine("Tests", "Fixtures", "janice-4S2IIm.json")));
        var snapshot = JaniceProfitability.Parse(badDoc.RootElement);
        var result = JaniceProfitability.Calculate(snapshot, new());
        Check(snapshot.Items.Count == 7 && Math.Abs(result.Payout - 6884967906.72m) < .01m && Math.Abs(result.Reference - 8619042790m) < .01m,
            "Corp's real 90% buy payout is compared with unscaled 100% Jita sell");
        Check(Math.Abs(result.Difference + 1734074883.28m) < .01m && Math.Abs(result.DiscountPercent - 20.1191m) < .001m && !result.MeetsLimit,
            "Bad appraisal exposes the 1.734b opportunity gap and 20.12% total discount");
        Check(Math.Abs(snapshot.Items.Sum(x => x.Sell) * .9m - 7757138511m) < .01m,
            "The 7.757b sell figure already has the 90% adjustment");
        Check(Math.Abs((1 - good.Items.Sum(x => x.Buy) / good.Items.Sum(x => x.Sell)) * 100 - 9.28m) < .01m &&
            Math.Abs((1 - snapshot.Items.Sum(x => x.Buy) / snapshot.Items.Sum(x => x.Sell)) * 100 - 11.24m) < .01m,
            "Both appraisals distinguish the buy/sell spread from the corp haircut");
        var buyReference = JaniceProfitability.Calculate(snapshot, new(ReferenceSide: JanicePriceSide.Buy));
        Check(Math.Abs(buyReference.Difference + 764996434.08m) < .01m && buyReference.DiscountPercent == 10 && buyReference.MeetsLimit,
            "90% buy meets an inclusive 10% limit against 100% buy");
        var split = JaniceProfitability.Calculate(good, new(PayoutSide: JanicePriceSide.Split, ReferenceSide: JanicePriceSide.Buy));
        Check(Math.Abs(split.Payout - 5014730544.885m) < .01m && Math.Abs(split.Difference + 286105850.315m) < .01m,
            "90% split prices are scaled once, never twice");
        var costs = JaniceProfitability.Calculate(snapshot, new(DirectSaleFeesPercent: 5, DirectCosts: 100000000, CorpCosts: 10000000));
        Check(Math.Abs(costs.Reference - (result.Reference * .95m - 100000000)) < .0001m && Math.Abs(costs.Payout - (result.Payout - 10000000)) < .0001m,
            "Each route's fees and basket hauling cost are applied once for a net-to-net comparison");
        var mixed = new JaniceSnapshot("mixed", DateTimeOffset.UtcNow, new[] {
            new JaniceOrePrice("Bad spread", 1, 10, 55, 100), new JaniceOrePrice("Favourable", 1, 1000, 1000, 1000) });
        var mixedResult = JaniceProfitability.Calculate(mixed, new(MaximumDiscountPercent: 20));
        Check(mixedResult.DiscountPercent <= 20 && mixedResult.Lines.Any(x => x.MeetsLimit) && !mixedResult.MeetsLimit,
            "A favourable aggregate cannot hide an individual line above the discount limit");
        bool Reject(string json)
        {
            try { using var d = JsonDocument.Parse(json); JaniceProfitability.Parse(d.RootElement); return false; }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { return true; }
        }
        var missing = JsonNode.Parse(fixture)!; missing["items"]![0]!["immediatePrices"]!["buyPriceTotal"] = 0;
        Check(Reject(missing.ToJsonString()), "An unquoted ore blocks comparison");
        missing = JsonNode.Parse(fixture)!; missing["failures"] = "Unknown ore";
        Check(Reject(missing.ToJsonString()), "Unpriced appraisal input prevents partial basket comparisons");
        missing = JsonNode.Parse(fixture)!; missing["pricerMarket"]!["id"] = 1;
        Check(Reject(missing.ToJsonString()), "Non-Jita appraisals cannot masquerade as Jita");
        missing = JsonNode.Parse(fixture)!; missing["datasetTime"] = "unknown";
        Check(Reject(missing.ToJsonString()), "Snapshot provenance is required");
        bool invalid = false;
        try { JaniceProfitability.Calculate(snapshot, new(DirectSaleFeesPercent: 100)); } catch (ArgumentException) { invalid = true; }
        Check(invalid, "Invalid fee percentages are rejected");
        invalid = false;
        try { JaniceProfitability.Calculate(snapshot, new(DirectCosts: 100000000000)); } catch (ArgumentException) { invalid = true; }
        Check(invalid, "Costs consuming the whole reference value require corrected inputs");
        var dashboard = new MiningDashboardWindow(new StatTrackerService(), new AppSettings());
        var view = (JaniceSimulationView)dashboard.FindName("JaniceSimulator"); view.ShowSnapshot(snapshot);
        Check(((DataGrid)view.FindName("Lines")).Items.Count == 7 && ((TextBlock)view.FindName("DecisionText")).Text.StartsWith("EXCESS DISCOUNT") &&
            ((TextBlock)view.FindName("TotalsText")).Text.Contains("1,734,074,883"), "Screen exposes the real full-volume shortfall");
        ((TextBox)view.FindName("PayoutPercent")).Text = "bad input";
        Check(((DataGrid)view.FindName("Lines")).Items.Count == 0 && ((TextBlock)view.FindName("DecisionText")).Text.StartsWith("CHECK INPUTS"),
            "Invalid edits clear the previous assessment");
        ((TextBox)view.FindName("PayoutPercent")).Text = "110";
        view.ShowSnapshot(snapshot with { DatasetTime = DateTimeOffset.UtcNow.AddDays(-1) });
        Check(((TextBlock)view.FindName("DecisionText")).Text.StartsWith("OLDER SNAPSHOT"), "Stale passing simulations require refreshed data");
        ((TextBox)view.FindName("AppraisalUrl")).Text = "https://janice.e-351.com/a/another";
        Check(((DataGrid)view.FindName("Lines")).Items.Count == 0 && !((TextBlock)view.FindName("DecisionText")).Text.Contains("WITHIN"),
            "Changing the appraisal URL clears prior results");
        ((TextBox)view.FindName("PayoutPercent")).Text = "90";
        ((TextBox)view.FindName("AppraisalUrl")).Text = "https://janice.e-351.com/a/4S2IIm"; view.ShowSnapshot(snapshot);
        ((TabControl)dashboard.FindName("MiningTabs")).SelectedItem = dashboard.FindName("JaniceSimulationTab");
        Check(((Grid)dashboard.FindName("MiningControlsPanel")).Visibility == Visibility.Collapsed,
            "Simulation hides unrelated mining settings to preserve space for ore lines");
        ((TabControl)dashboard.FindName("MiningTabs")).SelectedIndex = 0;
        Check(((Grid)dashboard.FindName("MiningControlsPanel")).Visibility == Visibility.Visible,
            "Mining settings return when leaving the simulator");
        if (renderPath != null)
        {
            ((TabControl)dashboard.FindName("MiningTabs")).SelectedItem = dashboard.FindName("JaniceSimulationTab");
            dashboard.Width = 1250; dashboard.Height = 780; Render(dashboard, renderPath);
        }
        Check(dashboard.FindName("JaniceSimulationTab") is TabItem, "Simulator is accessible in Mining Command Center");
        dashboard.Close();
    }
}
