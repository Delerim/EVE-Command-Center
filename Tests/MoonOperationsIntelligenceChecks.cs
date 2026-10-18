using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckMoonIntelligenceReports()
    {
        var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        Check(MoonOperationsIntelligence.Range(now, "Week") ==
              (new DateTimeOffset(2026,10,19,0,0,0,TimeSpan.Zero), new DateTimeOffset(2026,10,26,0,0,0,TimeSpan.Zero)),
            "Moon intelligence week uses Monday UTC");
        Check(MoonOperationsIntelligence.Range(now, "Last month") ==
              (new DateTimeOffset(2026,9,1,0,0,0,TimeSpan.Zero), new DateTimeOffset(2026,10,1,0,0,0,TimeSpan.Zero)),
            "Moon intelligence last month uses calendar boundaries");
        Check(MoonOperationsIntelligence.Range(now, "Yesterday") ==
              (new DateTimeOffset(2026,10,18,0,0,0,TimeSpan.Zero), new DateTimeOffset(2026,10,19,0,0,0,TimeSpan.Zero)),
            "Daily Discord intelligence reports cover the preceding completed day");
        var state = new MoonReportState { LastRefreshUtc = now.AddMinutes(-10) };
        state.EsiKnownStructureIds.Add(11);
        state.Structures.Add(new EsiCorporationStructure { StructureId = 11, Name = "Synthetic refinery" });
        state.Pulls["seen"] = new MoonPullRecord {
            Id="seen", StructureId=11, FracturedUtc=now.AddHours(-2),
            ExpiredUtc=now.AddHours(-1), MinedM3ByOre = new() { ["Zeolites"] = 100 }
        };
        state.Pulls["unseen"] = new MoonPullRecord {
            Id="unseen", StructureId=22, FracturedUtc=now.AddHours(-2),
            ExpiredUtc=now.AddHours(-1)
        };
        var snapshot = new MoonReportSnapshot {
            ActiveFieldCount = 1, InferredFieldCount = 2,
            CalendarCards = new [] { new MoonCardView { Status="FIELD ACTIVE", HasTargetProfile=true,
                ZeolitesRemainingM3=200, SylviteRemainingM3=100 } },
            Fuel = new [] { new StationFuelRow { StructureId=11, Now=now, Expires=now.AddDays(20) },
                new StationFuelRow { StructureId=22, Now=now, Expires=now.AddDays(100) } },
            DailyTotals = new [] { new MoonDailyTotalView { Date=now.UtcDateTime.Date, ZeolitesM3=400 } }
        };
        MoonDrillRow[] drills = { new(11,"Test","Synthetic","Synthetic","NOT SET","online",null,null,"","","") };
        var report = MoonOperationsIntelligence.Build(state, snapshot, drills, now, "Week",
            pull=>pull.Id=="seen", (pull,fam)=>fam=="zeolit" ? 55:0);
        Check(report.Structures == 1 && report.Fractures == 1 && report.LostFields == 1 && report.NotSet == 1,
            "Moon intelligence counts only the active ESI-discovered structure and recorded events");
        Check(report.ActiveFields==1 && report.InferredFields==2 && report.Ores[0].RemainingM3==200 &&
            report.Ores[0].DespawnedM3==55 && report.Ores[0].MinedM3==400,
            "Moon intelligence separates observed activity from inferred fields and estimated losses");
        Check(report.FuelKnown==1 && report.FuelLow==1 && report.AverageFuelDays is >=19 and <=21,
            "Moon intelligence average fuel excludes stations not in the ESI-discovered set");
        Check(!report.DiscordBody().Contains("https://discord",StringComparison.OrdinalIgnoreCase),
            "Moon intelligence report never emits a webhook URL");
        Check(DiscordWebhookService.AlertChoices.Any(x=>x.Key == DiscordWebhookService.MoonReportManual) &&
            DiscordWebhookService.AlertChoices.Any(x=>x.Key == DiscordWebhookService.MoonReportWeekly),
            "Moon intelligence manual and weekly report types can route independently");
        string directory = Path.Combine(Path.GetTempPath(), "ecc-report-test-"+Guid.NewGuid().ToString("N"));
        try {
            var schedule = new MoonIntelligenceSchedule(directory);
            Check(!schedule.Options().Daily && !schedule.Options().Weekly && !schedule.Options().Monthly,
                "Scheduled Discord reports are opt-in by default");
            schedule.Save(true,false,true,13);
            var loaded = new MoonIntelligenceSchedule(directory).Options();
            Check(loaded.Daily && !loaded.Weekly && loaded.Monthly && loaded.SendHourUtc==13,
                "Scheduled report cadence and UTC hour persist locally");
        } finally { if (Directory.Exists(directory)) Directory.Delete(directory,true); }
    }
}
