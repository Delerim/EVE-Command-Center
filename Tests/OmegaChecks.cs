using EveCommandCenter.Services;
using EveCommandCenter.Models;
using System.Text.Json;
internal static partial class Program
{
    private static void CheckOmegaBudget()
    {
        var now=DateTimeOffset.UtcNow;
        var pilots=new[]{new OmegaPilot{Id=1,Account="Main",Expiry=now.AddDays(29)},new OmegaPilot{Id=2,Account="main",Expiry=now.AddDays(29)},new OmegaPilot{Id=3}};
        var state=new OmegaBudgetState();
        Check(OmegaPlanning.Accounts(pilots).Count==2,"Omega groups account names case-insensitively without merging ungrouped pilots");
        Check(OmegaPlanning.Observe(pilots,state,now).Count==1,"Omega under-30-day reminder is grouped once per account");
        state=JsonSerializer.Deserialize<OmegaBudgetState>(JsonSerializer.Serialize(state))!;
        Check(OmegaPlanning.Observe(pilots,state,now).Count==0,"Omega reminder deduplication survives persistence");
        Check(OmegaPlanning.Observe(pilots,state,now.AddDays(23)).Count==1&&OmegaPlanning.Observe(pilots,state,now.AddDays(30)).Count==1,"Omega reminders repeat at seven days and expiry only");
        state.Alerts=false;pilots[0].Expiry=now.AddDays(1);
        Check(OmegaPlanning.Observe(pilots,state,now).Count==0,"Disabled Omega desktop reminders stay quiet");
        var budget=OmegaPlanning.Budget(new(){Plex=500,Days=30},new(){Plex=100,Isk=500000000},5000000,now.AddDays(10),now);
        Check(budget.Cost==2500000000&&budget.Gap==1500000000&&budget.Daily==150000000&&budget.PlexNeeded==400,"Omega budget subtracts earmarked PLEX and ISK once and calculates daily savings");
        Check(OmegaPlanning.Budget(new(),new(),null,now,now).Gap==null,"Missing PLEX quote never displays free renewal");
        Check(OmegaPlanning.Color(null,now)!=OmegaPlanning.Color(now.AddDays(1),now)&&OmegaPlanning.Color(now.AddDays(29),now)!=OmegaPlanning.Color(now.AddDays(31),now),"Omega colors distinguish unknown, urgent and under-30-day accounts");
        var news=OmegaPlanning.ParseNews("\uFEFF<rss><channel><item><title>PLEX deal</title><link>https://www.eveonline.com/news/view/deal</link><pubDate>Sat, 12 Sep 2026 11:00:00 GMT</pubDate></item><item><title>Omega</title><link>https://evil.example/</link></item></channel></rss>");
        Check(news.Count==1&&news[0].Published.Year==2026,"Offer feed handles BOM and only accepts official HTTPS announcements");
    }
}
