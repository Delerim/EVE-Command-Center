using EveCommandCenter.Models;
namespace EveCommandCenter.Services;

public static class IndustryJobMatching
{
    public static bool Valid(IndustryJobLink j) => j.OwnerId > 0 && j.JobId > 0 && j.InstallerId == j.OwnerId &&
        j.ProductTypeId > 0 && j.BlueprintTypeId > 0 && j.BlueprintItemId > 0 && j.FacilityId > 0 && j.Runs > 0 &&
        j.StartUtc != default && j.EndUtc >= j.StartUtc && j.Status is "active" or "paused" or "ready" or "delivered" or "cancelled" or "reverted";
    public static IReadOnlyList<IndustryJobLink> Read(IEnumerable<IndustryPilot> pilots, ISet<long> authorized)
    {
        var result = new List<IndustryJobLink>();
        foreach (var p in pilots.Where(p => authorized.Contains(p.Id) && string.IsNullOrWhiteSpace(p.Error)))
        foreach (var j in p.Jobs.Where(j => IndustryCatalog.Num(j, "activity_id") == 1))
        {
            DateTimeOffset.TryParse(IndustryCatalog.Text(j, "start_date"), out var start);
            DateTimeOffset.TryParse(IndustryCatalog.Text(j, "end_date"), out var end);
            var link = new IndustryJobLink(p.Id, IndustryCatalog.Num(j, "job_id"), IndustryCatalog.Num(j, "installer_id"),
                (int)IndustryCatalog.Num(j, "product_type_id"), (int)IndustryCatalog.Num(j, "blueprint_type_id"),
                IndustryCatalog.Num(j, "blueprint_id"), IndustryCatalog.Num(j, "facility_id"), IndustryCatalog.Num(j, "runs"),
                IndustryCatalog.Text(j, "status"), start, end, p.Updated);
            if (Valid(link)) result.Add(link);
        }
        return result;
    }
    public static string Describe(IndustryJobLink j) => j.Status switch
    {
        "ready" => "Ready to deliver in EVE",
        "delivered" => "Delivered from job | project receipt unverified",
        "cancelled" or "reverted" => "Cancelled / reverted | review remaining demand",
        "active" => "Manufacturing", "paused" => "Paused in EVE", _ => "Review observation"
    };
}
