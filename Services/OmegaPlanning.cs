using System.Globalization;
using System.Xml.Linq;
using EveCommandCenter.Models;
namespace EveCommandCenter.Services;

public sealed class OmegaBudgetState
{
    public bool Alerts { get; set; } = true;
    public Dictionary<string,string> Notified { get; set; } = new();
    public Dictionary<string,OmegaSavings> Savings { get; set; } = new();
    public double? PlexSell { get; set; }
    public DateTimeOffset PriceTime { get; set; }
    public DateTimeOffset NewsTime { get; set; }
    public List<OmegaNews> News { get; set; } = new();
    public List<OmegaOffer> Offers { get; set; } = new() {
        new(){Name="Union Day: 1 month",Days=30,Plex=350,Ends=new DateTimeOffset(2026,9,20,0,0,0,TimeSpan.Zero),SourceUrl="https://www.eveonline.com/news/view/nes-up-to-30-off-omega-free-skins"},
        new(){Name="Union Day: 3 months",Days=90,Plex=900,Ends=new DateTimeOffset(2026,9,20,0,0,0,TimeSpan.Zero),SourceUrl="https://www.eveonline.com/news/view/nes-up-to-30-off-omega-free-skins"},
        new(){Name="Union Day: 6 months",Days=180,Plex=1680,Ends=new DateTimeOffset(2026,9,20,0,0,0,TimeSpan.Zero),SourceUrl="https://www.eveonline.com/news/view/nes-up-to-30-off-omega-free-skins"},
        new(){Name="1 month reference",Days=30,Plex=500},new(){Name="3 months reference",Days=90,Plex=1200},
        new(){Name="6 months reference",Days=180,Plex=2100},new(){Name="12 months reference",Days=360,Plex=3600},new(){Name="24 months reference",Days=720,Plex=6600} };
}
public sealed class OmegaSavings { public decimal Isk { get; set; } public int Plex { get; set; } }
public sealed class OmegaOffer
{
    public string Name { get; set; } = "NES offer";
    public int Days { get; set; } = 30;
    public int Plex { get; set; } = 500;
    public DateTimeOffset? Ends { get; set; }
    public string SourceUrl { get; set; } = "";
    public bool Manual { get; set; }
    public string Source => SourceUrl.Length>0 ? "CCP news 12 Sep 2026; cutoff 20 Sep UTC (time unspecified)" : Manual ? "Your NES entry; verify availability" : "CCP June 2026 base reference; verify in NES";
    public string Validity => Ends.HasValue ? Ends.Value <= DateTimeOffset.UtcNow ? "Expired" : "Ends " + Ends.Value.ToLocalTime().ToString("dd MMM HH:mm") : "Availability unverified";
    public decimal PerMonth => Plex * 30m / Math.Max(1,Days);
    public override string ToString() => $"{Name} | {Plex:N0} PLEX / {Days} days";
}
public sealed class OmegaNews
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset Published { get; set; }
    public string Date => Published.ToLocalTime().ToString("dd MMM yyyy");
}
public static class OmegaPlanning
{
    public static string Key(OmegaPilot p) => string.IsNullOrWhiteSpace(p.Account) ? "pilot:"+p.Id : "account:"+p.Account.Trim().ToUpperInvariant();
    public static List<OmegaPilot> Accounts(IEnumerable<OmegaPilot> pilots) => pilots.GroupBy(Key).Select(g=>g.OrderBy(p=>p.Expiry??DateTimeOffset.MaxValue).First()).OrderBy(p=>p.Expiry??DateTimeOffset.MaxValue).ToList();
    public static string Color(DateTimeOffset? expiry, DateTimeOffset now) => !expiry.HasValue ? "#91A4AD" : expiry<=now ? "#F27878" : expiry<now.AddDays(7) ? "#FFAA65" : expiry<now.AddDays(30) ? "#FFD166" : "#74D6C9";
    public static List<string> Observe(IEnumerable<OmegaPilot> pilots,OmegaBudgetState state,DateTimeOffset now)
    {
        var result=new List<string>();
        if(!state.Alerts)return result;
        var accounts=Accounts(pilots);var keys=accounts.Select(Key).ToHashSet();
        foreach(var key in state.Notified.Keys.Where(k=>!keys.Contains(k)).ToList())state.Notified.Remove(key);
        foreach(var p in accounts)
        {
            var key=Key(p);
            if(p.Expiry is not {} expiry || expiry>=now.AddDays(30)){state.Notified.Remove(key);continue;}
            var stage=expiry<=now?"expired":expiry<now.AddDays(7)?"7":"30";
            var mark=expiry.ToUnixTimeSeconds()+":"+stage;
            if(state.Notified.GetValueOrDefault(key)==mark)continue;
            state.Notified[key]=mark;
            result.Add((p.Account.Length>0?p.Account:p.Name)+": "+(stage=="expired"?"recorded date expired; verify renewal":$"{Math.Ceiling((expiry-now).TotalDays):0} days left (recorded date)"));
        }
        return result;
    }
    public static (decimal? Cost,decimal? Gap,decimal? Daily,int PlexNeeded) Budget(OmegaOffer offer,OmegaSavings saved,double? sell,DateTimeOffset? expiry,DateTimeOffset now)
    {
        int plex=Math.Max(0,offer.Plex-Math.Max(0,saved.Plex));
        if(sell is not >0 || !double.IsFinite(sell.Value))return(null,null,null,plex);
        decimal cost=offer.Plex*(decimal)sell.Value;
        decimal gap=Math.Max(0,plex*(decimal)sell.Value-Math.Max(0,saved.Isk));
        decimal? daily=expiry>now?gap/(decimal)Math.Max(1,(expiry.Value-now).TotalDays):null;
        return(cost,gap,daily,plex);
    }
    public static List<OmegaNews> ParseNews(string xml)
    {
        var doc=XDocument.Parse(xml.TrimStart('\uFEFF'));
        return doc.Descendants("item").Select(x=>new OmegaNews {Title=(string?)x.Element("title")??"",Url=(string?)x.Element("link")??"",Published=DateTimeOffset.TryParse((string?)x.Element("pubDate"),CultureInfo.InvariantCulture,DateTimeStyles.None,out var d)?d:default})
            .Where(x=>Uri.TryCreate(x.Url,UriKind.Absolute,out var u)&&u.Scheme=="https"&&u.Host=="www.eveonline.com"&&(x.Title.Contains("Omega",StringComparison.OrdinalIgnoreCase)||x.Title.Contains("PLEX",StringComparison.OrdinalIgnoreCase)||x.Title.Contains("NES",StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x=>x.Published).Take(12).ToList();
    }
}
