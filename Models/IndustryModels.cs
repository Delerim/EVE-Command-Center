using System.Text.Json;
namespace EveCommandCenter.Models;
public sealed class IndustryRecipe
{
    public int Blueprint { get; set; }
    public string Activity { get; set; } = "";
    public double Seconds { get; set; }
    public Dictionary<int,double> Materials { get; set; } = new();
    public Dictionary<int,double> Products { get; set; } = new();
    public Dictionary<int,int> Skills { get; set; } = new();
    public double Probability { get; set; } = 1;
    public string Name => Services.IndustryCatalog.Name(Blueprint);
    public string ActivityText => Activity.Replace('_',' ').ToUpperInvariant();
    public string Icon => $"https://images.evetech.net/types/{Blueprint}/bp?size=64";
}
public sealed class IndustryPilot
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public List<JsonElement> Jobs { get; set; } = new();
    public List<JsonElement> Blueprints { get; set; } = new();
    public List<EveAssetItem> Assets { get; set; } = new();
    public Dictionary<int,int> Skills { get; set; } = new();
    public DateTimeOffset Updated { get; set; }
    public string Error { get; set; } = "Not linked";
    public string Portrait => $"https://images.evetech.net/characters/{Id}/portrait?size=64";
    public string Summary
    {
        get { var jobs=Services.IndustryCatalog.Jobs(this,DateTimeOffset.UtcNow);return $"{jobs.Count(j=>j.Status=="active")} active | {jobs.Count(j=>j.Status.Contains("READY",StringComparison.OrdinalIgnoreCase))} ready | {Blueprints.Count} blueprints"; }
    }
}
public sealed class IndustryQuote
{
    public double? Buy { get; set; }
    public double? Sell { get; set; }
    public DateTimeOffset Time { get; set; }
}
public sealed class IndustryState
{
    public List<IndustryPilot> Pilots { get; set; } = new();
    public Dictionary<int,IndustryQuote> Quotes { get; set; } = new();
    public Dictionary<string,IndustryCostSettings> CostSettings { get; set; } = new();
    public HashSet<string> Notified { get; set; } = new();
    public bool Alerts { get; set; } = true;
}
public sealed class IndustryMaterial
{
    public int TypeId { get; set; }
    public string Name => Services.IndustryCatalog.Name(TypeId);
    public string Icon => $"https://images.evetech.net/types/{TypeId}/icon?size=32";
    public double Required { get; set; }
    public double Owned { get; set; }
    public double Missing => Math.Max(0, Required-Owned);
    public double? UnitPrice { get; set; }
    public string Cost => UnitPrice.HasValue ? (Missing*UnitPrice.Value).ToString("N0") + " ISK" : "Quote pending";
    public string Color => Missing > 0 ? "#FFD166" : "#74D6C9";
}
public sealed class IndustryPlan
{
    public IndustryRecipe Recipe { get; set; } = new();
    public string Name => Recipe.Name;
    public string Icon => $"https://images.evetech.net/types/{Recipe.Blueprint}/{(Blueprint.StartsWith("BPC") ? "bpc" : "bp")}?size=64";
    public string Activity => Recipe.ActivityText;
    public string Blueprint { get; set; } = "Not owned";
    public string Status { get; set; } = "";
    public string Color => Services.IndustryActivities.StatusColor(Status);
    public string ActivityColor => Services.IndustryActivities.Color(Recipe.Activity);
    public string BlueprintColor => Blueprint.StartsWith("BPO") ? "#FFD166" : "#80BFFF";
    public List<IndustryMaterial> Materials { get; set; } = new();
    public string Skills { get; set; } = "";
    public string Duration { get; set; } = "";
    public string Economics { get; set; } = "";
    public string Products { get; set; } = "";
}
public sealed class IndustryJobView
{
    public string ActivityCode {get;set;}="";
    public string ActivityColor=>Services.IndustryActivities.Color(ActivityCode);
    public string TimeLeft {get;set;}="";
    public double Progress {get;set;}
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Activity { get; set; } = "";
    public string Status { get; set; } = "";
    public string Color => Status.Contains("READY",StringComparison.OrdinalIgnoreCase) ? "#FFD166" : Status == "active" ? "#74D6C9" : "#FFD166";
    public string Runs { get; set; } = "";
    public string End { get; set; } = "";
    public string Location { get; set; } = "";
}
