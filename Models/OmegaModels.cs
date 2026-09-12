using System.Text.Json;
namespace EveCommandCenter.Models;
public sealed class OmegaPilot
{
    public long Id {get;set;}
    public string Name {get;set;}="";
    public string Account {get;set;}="";
    public DateTimeOffset? Expiry {get;set;}
    public string ReportedStatus {get;set;}="Unknown";
    public JsonElement Clones {get;set;} = JsonSerializer.SerializeToElement(new {});
    public DateTimeOffset Updated {get;set;}
    public string Error {get;set;}="Link clone permission";
    public string Portrait=>$"https://images.evetech.net/characters/{Id}/portrait?size=64";
    public string OmegaStatus=>Expiry.HasValue ? Expiry>DateTimeOffset.UtcNow?"Omega (manual date)":"Recorded date expired; verify" : ReportedStatus+" (not ESI verified)";
    public string Remaining=>Expiry.HasValue?Math.Max(0,(Expiry.Value-DateTimeOffset.UtcNow).TotalDays).ToString("N1")+" days":"Unknown";
    public string Expires=>Expiry?.ToLocalTime().ToString("dd MMM yyyy HH:mm")??"Not recorded";
    public string Color=>Expiry.HasValue&&Expiry<DateTimeOffset.UtcNow.AddDays(7)?"#FFD166":"#74D6C9";
    public string Home=>Clones.ValueKind==JsonValueKind.Object&&Clones.TryGetProperty("home_location",out var h)?Services.IndustryCatalog.Text(h,"location_type")+" "+Services.IndustryCatalog.Num(h,"location_id"):"Unknown";
    public string JumpCount=>Clones.ValueKind==JsonValueKind.Object&&Clones.TryGetProperty("jump_clones",out var c)?c.GetArrayLength().ToString():"Unknown";
}
