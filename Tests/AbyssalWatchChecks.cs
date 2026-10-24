using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckAbyssalWatch()
    {
        string Sample(string source, long id, long contractId, double price, double yield,
            double duration, double cpu, double cpuBase, double pg, double pgBase) =>
            "{\"data\":[{\"id\":"+id+",\"slug\":\"abyssal-test-"+id+"\",\"source_type\":{\"name\":\""+source+"\"},"+
            "\"contract\":{\"id\":"+contractId+",\"price\":"+price.ToString(System.Globalization.CultureInfo.InvariantCulture)+",\"abyssal_modules_count\":1,\"non_abyssal_modules_count\":0},"+
            "\"estimated_value\":500000000,\"mutated_attributes\":["+
            "{\"name\":\"miningAmount\",\"value\":"+yield.ToString(System.Globalization.CultureInfo.InvariantCulture)+"},"+
            "{\"name\":\"duration\",\"value\":"+duration.ToString(System.Globalization.CultureInfo.InvariantCulture)+"},"+
            "{\"name\":\"miningCritChance\",\"value\":1},"+
            "{\"name\":\"miningCritBonusYield\",\"value\":200},"+
            "{\"name\":\"cpu\",\"value\":"+cpu.ToString(System.Globalization.CultureInfo.InvariantCulture)+",\"base_value\":"+cpuBase.ToString(System.Globalization.CultureInfo.InvariantCulture)+"},"+
            "{\"name\":\"power\",\"value\":"+pg.ToString(System.Globalization.CultureInfo.InvariantCulture)+",\"base_value\":"+pgBase.ToString(System.Globalization.CultureInfo.InvariantCulture)+"}]}]}";
        var settings = new AbyssalWatchSettings();
        Check(settings.Enabled && settings.WatchOreStripMiner && settings.WatchModulatedStripMiner && settings.MinAskingPrice == 400_000_000,
            "Abyssal watch defaults to both models and 400M asking-price floor");
        var ore = AbyssalWatchService.ParsePage(Sample("ORE Strip Miner", 1234, 222, 450000000, 150, 45, 60, 59, 12, 11), 90493).Single();
        Check(AbyssalWatchService.Matches(ore, settings) && Math.Abs(ore.EffectiveSpeed - 3.4) < .0001,
            "ORE strip miner passes effective speed, yield, CPU, PG and price rules");
        var modulated = AbyssalWatchService.ParsePage(Sample("Modulated Strip Miner II", 5678, 333, 800000000, 144, 44, 60, 59, 12, 11), 90467).Single();
        Check(AbyssalWatchService.Matches(modulated, settings), "Modulated II passes the same filters without restricting the chosen model");
        Check(AbyssalWatchService.ParsePage(Sample("Strip Miner I", 999, 123, 900000000, 160, 40, 60, 59, 12, 11), 90493).Count == 0,
            "Different base module never masquerades as the desired ORE strip miner");
        Check(!AbyssalWatchService.Matches(ore with { AskingPrice = 399_000_000 }, settings), "Under-400M listings are excluded by default");
        Check(!AbyssalWatchService.Matches(ore with { CpuPenalty = 3 }, settings), "Poor CPU roll is rejected");
        Check(!AbyssalWatchService.Matches(ore with { PowergridPenalty = 3 }, settings), "Poor PG roll is rejected");
        Check(!AbyssalWatchService.Matches(ore with { AbyssalModules = 2 }, settings), "Bundle contracts are excluded from single-item alerts");
        Check(!AbyssalWatchService.Matches(ore with { AskingPrice = double.NaN }, settings), "Unknown asking prices cannot produce a false deal alert");
        var seen = new HashSet<string> { ore.Key };
        Check(seen.Add(ore.Key) == false && seen.Add(modulated.Key), "Distinct module-contract combinations deduplicate repeat alerts");
        Check(ore.Url == "https://mutamarket.com/modules/abyssal-test-1234", "Watch links open only the precise module detail");
        Check(ore.MiningAmountText == "150.00" && ore.CycleText == "45.00" &&
            ore.EffectiveSpeedText == "3.40" && ore.CpuDeltaText == "+1.00" &&
            ore.PowergridDeltaText == "+1.00", "Hunter displays useful rounded mining and fitting numbers");
        Check(ore.PriceAdvantageText.StartsWith("+") && ore.EstimateText == "500M", "Hunter shows estimated-value gap without confusing it with asking price");
        Check(AbyssalWatchService.ParseNextCursor("{\"meta\":{\"next_cursor\":\"abcXYZ==\"}}") == "abcXYZ==" &&
            AbyssalWatchService.ParseNextCursor("{\"data\":[]}") == null,
            "Hunter accepts only documented paginated API cursors");
        Check(!AbyssalWatchService.Matches(ore with { ExpiryUtc = "2020-01-01T00:00:00Z" }, settings),
            "Expired public contracts never produce attractive deal alerts");
    }
}
