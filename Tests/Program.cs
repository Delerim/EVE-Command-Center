using System.IO;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;

internal static partial class Program
{
    private static int _checks;
    private static void Check(bool passed, string name)
    {
        if (!passed) throw new Exception(name);
        _checks++;
        Console.WriteLine("PASS " + name);
    }
    private static ContractRow Row(decimal price = 900, long id = 1, long location = ContractService.JoppayaStationId, string name = "Joppaya IX - Moon 9 - Ardishapur Family Bureau") => new()
    {
        Contract = new() { Id = id, Price = price, Type = "item_exchange", LocationId = location, Expires = DateTimeOffset.UtcNow.AddDays(3), Title = "Ore delivery https://janice.e-351.com/a/test" },
        Issuer = "Example Miner", Location = name, CorporationId = 42, JaniceUrl = "https://janice.e-351.com/a/test"
    };

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.FirstOrDefault() == "--inspect-fit")
        {
            var sso = new EveSsoService();
            foreach (var pilot in sso.LoadPilotsAsync().GetAwaiter().GetResult().Where(p => args.Skip(1).Contains(p.CharacterName)))
            {
                var fit = sso.GetInventoryAsync(pilot).GetAwaiter().GetResult();
                Console.WriteLine(pilot.CharacterName + " low slots: " + string.Join(", ", fit.CurrentShipModules.Where(m => m.Slot.StartsWith("Low")).Select(m => m.Name)));
                Console.WriteLine("Unboosted: " + fit.CurrentFitStats.OmniEhp + "; 19.7% shield bursts: " + fit.CurrentFitStats.ApplyShieldCommandBoost(19.7, 19.7).OmniEhp);
            }
            return;
        }
        if (args.FirstOrDefault() == "--inspect-access")
        {
            var sso = new EveSsoService();
            var access = new CorporationAccessService(sso);
            using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            access.ValidateAsync(sso.LoadPilotsAsync().GetAwaiter().GetResult(), stop.Token).GetAwaiter().GetResult();
            Console.WriteLine("Moons: " + access.MoonStatus);
            Console.WriteLine("Contracts: " + access.ContractStatus);
            return;
        }
        CheckEsiQueue().GetAwaiter().GetResult();
        Check(EveSsoService.IsShieldMindlink("ORE Mining Director Mindlink") && !EveSsoService.IsShieldMindlink("Mining Foreman Mindlink"), "ORE mindlink applies shield bonus; ordinary mining mindlink does not");
        var values = new MoonReportState { TypePrices = new() { [45490] = 1400, [45494] = 1800 }, LedgerHistory = new()
        {
            ["base"] = new() { TypeId=45490, Quantity=100, VolumeM3=1000, EstimatedIsk=1 },
            ["rich"] = new() { TypeId=45494, Quantity=100, VolumeM3=1000, EstimatedIsk=1 },
            ["missing"] = new() { TypeId=999, Quantity=100, EstimatedIsk=9999 }
        } };
        MoonReportService.RevalueLedger(values);
        Check(values.LedgerHistory["base"].EstimatedIsk == 140000 && values.LedgerHistory["rich"].EstimatedIsk == 180000, "Ledger revaluation preserves variant prices and one-to-one mined unit quantities");
        Check(values.LedgerHistory["base"].VolumeM3 == 1000 && values.LedgerHistory["missing"].EstimatedIsk == 0, "Repricing preserves raw mined volume and removes obsolete unpriced values");
        values.TypePrices[45490] = 1500; MoonReportService.RevalueLedger(values);
        Check(values.LedgerHistory["base"].EstimatedIsk == 150000, "Saved ledger entries update when current compressed quotes change");
        CheckPreviewStability();
        CheckAuthorizationScopes();
        CheckWindowLayouts();
        var piFixture = CheckPlanetary();
        var industryFixture = CheckIndustry();
        CheckSkillPlanning();
        CheckOmegaBudget();
        CheckRockTracking();
        CheckNotificationCenter();
        CheckMiningRates();
        CheckBuybackPeriods();
        CheckMoonAlerts();
        CheckContractHistory();
        CheckFitStacking();
        CheckSaberlashFit().GetAwaiter().GetResult();
        CheckCycleAndFuel();
        CheckReleaseMonitor().GetAwaiter().GetResult();
        CheckAccessAsync().GetAwaiter().GetResult();
        var moonSnapshot = CheckMoonRecovery();
        using var appraisal = JsonDocument.Parse("{\"pricerMarket\":{\"name\":\"Jita 4-4\"},\"immediatePrices\":{\"totalBuyPrice\":1000}}");
        var good = Row();
        ContractService.Evaluate(good, appraisal.RootElement, 90, 0.1m);
        Check(good.Passed && good.ActualBuyPercent == 90, "90% and approved station pass");
        foreach (decimal price in new[] { 950m, 1000m, 850m })
        {
            var bad = Row(price);
            ContractService.Evaluate(bad, appraisal.RootElement, 90, 0.1m);
            Check(!bad.Passed && bad.HasMismatch && bad.PriceCheck == "PRICE MISMATCH", $"{price / 10}% price marked red");
        }
        var wrong = Row(location: 60000001, name: "Wrong station");
        wrong.Contract.Title += " " + ContractService.AllowedLocations[2];
        ContractService.Evaluate(wrong, appraisal.RootElement, 90, 0.1m);
        Check(!wrong.Passed && wrong.LocationCheck == "WRONG DESTINATION", "Title cannot spoof destination");
        foreach (string name in new[] { "Raren \u2013 Ducks Migration", "Mazitah - Eagle One", "Mazitah - EagleOne" })
        {
            var structure = Row(location: 1_000_000_000_001, name: name);
            ContractService.Evaluate(structure, appraisal.RootElement, 90, 0.1m);
            Check(structure.Passed, "Approved structure exact name: " + name);
        }
        var suffix = Row(location: 1_000_000_000_001, name: "Mazitah - Eagle One EXTRA");
        ContractService.Evaluate(suffix, appraisal.RootElement, 90, 0.1m);
        Check(suffix.HasMismatch, "Partial destination match rejected");
        var eagleTwo = Row(location: 1_000_000_000_001, name: "Mazitah - EagleTwo");
        ContractService.Evaluate(eagleTwo, appraisal.RootElement, 90, 0.1m);
        Check(eagleTwo.LocationCheck == "WRONG DESTINATION", "EagleOne alias does not authorize EagleTwo");
        var unknown = Row(name: "Unresolved (60008740)");
        ContractService.Evaluate(unknown, null, 90, 0.1m, "Janice unavailable");
        Check(!unknown.Passed && !unknown.HasMismatch && unknown.ResultColor == "#FFD166", "Unverified data is amber, never green");
        var tolerance = Row(901m);
        ContractService.Evaluate(tolerance, appraisal.RootElement, 90, 0.1m);
        Check(tolerance.Passed, "1 ISK minimum tolerance accepted");
        Check(ContractService.ExtractJaniceUrl("<a href='https://janice.e-351.com/a/Ab_12'>Janice</a>") == "https://janice.e-351.com/a/Ab_12", "Janice link extraction");
        Check(ContractService.ExtractJaniceUrl("https://janice.e-351.com.evil.test/a/test") == null, "Lookalike Janice domain rejected");
        var seen = new Dictionary<long, HashSet<long>>();
        Check(ContractService.FindNew(seen, 42, new() { good }).Count == 0, "First refresh establishes baseline");
        Check(ContractService.FindNew(seen, 42, new() { good, Row(id: 2) }).Count == 1, "New contract notified once");
        var persisted = JsonSerializer.Deserialize<Dictionary<long, HashSet<long>>>(JsonSerializer.Serialize(seen))!;
        Check(ContractService.FindNew(persisted, 42, new() { good, Row(id: 2) }).Count == 0, "Seen IDs survive restart");
        Check(ContractService.FindNew(persisted, 43, new() { Row(id: 3) }).Count == 0, "Corporation baselines isolated");
        CheckRefreshAsync().GetAwaiter().GetResult();

        // Load XAML and render sample data without starting the app or live ESI polling.
        var app = new System.Windows.Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var profitWindow = new MiningDashboardWindow(new StatTrackerService(), new AppSettings());
        var waitingPrice = new TaskCompletionSource<MiningMarketQuote?>();
        var profitRefresh = profitWindow.RefreshProfitRowsAsync(new[] { new MiningAggregateRow { DayKey="2026-09-13", Character="Sample pilot", Ore="Zeolites", Units=1080, NormalUnits=1080, Cycles=2 } }, _ => waitingPrice.Task);
        Check(profitRefresh.IsCompletedSuccessfully && ((System.Collections.IEnumerable)((DataGrid)profitWindow.FindName("ProfitOreGrid")).ItemsSource).Cast<object>().Count()==1, "Profit renders mining rows without waiting for ESI prices");
        Check(((TextBlock)profitWindow.FindName("ProfitTotalMinedText")).Text == 1080d.ToString("N0") && ((TextBlock)profitWindow.FindName("ProfitMarketText")).Text == "Prices pending", "Profit preserves quantities and labels unavailable valuations");
        waitingPrice.SetException(new TimeoutException("Simulated ESI cooldown"));
        profitWindow.RefreshProfitRowsAsync(Array.Empty<MiningAggregateRow>(), _ => Task.FromResult<MiningMarketQuote?>(null));
        Check(((TextBlock)profitWindow.FindName("ProfitBuildText")).Text.StartsWith("No mining recorded"), "Profit distinguishes an empty date range from missing prices");
        profitWindow.Close();
        if(args.Length>0)
        {
            var overviewTracker=new StatTrackerService();
            var overview=new MiningFleetOverviewWindow(overviewTracker,new MiningIdleWatchdogService(overviewTracker),new MiningDashboardPreferences{FleetOverviewWidth=1800,FleetOverviewHeight=300});BackgroundOperations.Stop();
            var cardType=typeof(MiningFleetOverviewWindow).GetNestedType("FleetCard",System.Reflection.BindingFlags.NonPublic)!;
            object Card(string name){var c=Activator.CreateInstance(cardType,true)!;void Set(string p,object v)=>cardType.GetProperty(p)!.SetValue(c,v);Set("Character",name);Set("ShipText","Ship: Skiff");Set("Ore","Zeolites");Set("BaseText","44.8 m3/s");Set("ActualText","44.8 m3/s");Set("RockVisibility",Visibility.Visible);Set("Rock1Text","L1 18,420 m3 est.");Set("Rock2Text","L2 9,650 m3 est.");Set("Rock1Percent",74d);Set("Rock2Percent",39d);return c;}
            var sampleCards=new StackPanel{Orientation=Orientation.Horizontal};
            foreach(var name in new[]{"Pilot A","Pilot B"}){var view=(FrameworkElement)((ItemsControl)overview.FindName("MinerItems")).ItemTemplate.LoadContent();view.DataContext=Card(name);sampleCards.Children.Add(view);}
            Render(new Window{Content=sampleCards,Resources=overview.Resources,Background=new SolidColorBrush(Color.FromRgb(7,24,27)),Width=420,Height=320},System.IO.Path.ChangeExtension(args[0],".rock-overview.png"));
        }
        var rockDir=Path.Combine(Path.GetTempPath(),"ecc-rock-ui-"+Guid.NewGuid());
        var rockService=new RockTrackingService(rockDir);rockService.Enable(true);
        var rockWindow=new RockTrackingWindow(rockService,"Example pilot","Zeolites");
        Check(rockWindow.FindName("Volume1")!=null&&rockWindow.FindName("Volume2")!=null,"Rock setup exposes independent per-laser volume controls");
        if(args.Length>0)Render(rockWindow,System.IO.Path.ChangeExtension(args[0],".rocks.png"));
        rockWindow.Close();Directory.Delete(rockDir,true);
        var noticesWindow=new NotificationCenterWindow();
        Check(noticesWindow.FindName("Rows")!=null,"Notification centre XAML loads");
        if(args.Length>0){((ItemsControl)noticesWindow.FindName("Rows")).ItemsSource=new[]{new CenterNotification{Source="PI",Title="Pilot A | 2 colonies",Detail="Planet I: UNDER 4 HOURS (estimated restart/refill)\nPlanet II: UNDER 4 HOURS (estimated restart/refill)",Active=true,Updated=DateTimeOffset.UtcNow}};Render(noticesWindow,System.IO.Path.ChangeExtension(args[0],".notifications.png"));}
        var waitingPlanner=new SkillPlannerWindow(new EvePilotDashboard {Summary=new(){CharacterId=-998,CharacterName="Waiting pilot"}},false);
        typeof(SkillPlannerWindow).GetMethod("Profile_Click",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(waitingPlanner,new object[]{waitingPlanner,new RoutedEventArgs()});
        Check(((TextBlock)waitingPlanner.FindName("Summary")).Text.Contains("Waiting")&&((DataGrid)waitingPlanner.FindName("Steps")).Items.Count==0,"Planner opens before ESI data without inventing missing levels");
        waitingPlanner.ApplySnapshot(new EvePilotDashboard {Summary=new(){CharacterId=-997}});
        Check(((DataGrid)waitingPlanner.FindName("Steps")).Items.Count==0,"Waiting planner rejects another pilot's snapshot");
        waitingPlanner.ApplySnapshot(new EvePilotDashboard {Summary=new(){CharacterId=-998}});
        Check(((DataGrid)waitingPlanner.FindName("Steps")).Items.Count>30,"Waiting planner keeps chosen profile and populates automatically when skills arrive");
        var skillWindow = new SkillPlannerWindow(new EvePilotDashboard { Summary = new() { CharacterId = -999, CharacterName = "Planner test pilot" }, TrainingProfile = new() { Attributes = new[] { 164,165,166,167,168 }.Select((id,i) => new EveTrainingAttribute { DogmaAttributeId = id, Name = SkillPlanning.Attribute(id), Total=20+i }).ToList() } });
        Check(skillWindow.FindName("Steps") != null && ((DataGrid)skillWindow.FindName("ProfileGrid")).Items.Count >= 30, "Skill planner loads profile comparison and training plan controls");
        typeof(SkillPlannerWindow).GetMethod("Profile_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(skillWindow,new object[] { skillWindow,new RoutedEventArgs() });
        Check(((DataGrid)skillWindow.FindName("Steps")).Items.Count > 30, "Profile action populates missing levels and prerequisites");
        if(args.Length>0) Render(skillWindow,System.IO.Path.ChangeExtension(args[0],".skills.png"));
        var omegaWindow = new OmegaWindow(); BackgroundOperations.Stop();
        Check(omegaWindow.FindName("Pilots") != null && omegaWindow.FindName("Expiry") != null,"Omega dashboard exposes tracked dates and clone information");
        ((DataGrid)omegaWindow.FindName("Pilots")).ItemsSource=new[]{new OmegaPilot {Id=42,Name="Example Pilot",Expiry=DateTimeOffset.UtcNow.AddDays(14),Account="Account A"}};
        ((DataGrid)omegaWindow.FindName("Pilots")).SelectedIndex=0;
        if(args.Length>0){Render(omegaWindow,System.IO.Path.ChangeExtension(args[0],".omega.png"));((TabControl)omegaWindow.FindName("OmegaTabs")).SelectedIndex=1;Render(omegaWindow,System.IO.Path.ChangeExtension(args[0],".omega-budget.png"));((TabControl)omegaWindow.FindName("OmegaTabs")).SelectedIndex=2;Render(omegaWindow,System.IO.Path.ChangeExtension(args[0],".omega-offers.png"));}
        var industryWindow = new IndustryWindow(); BackgroundOperations.Stop();
        Check(industryWindow.FindName("Recipes") != null && industryWindow.FindName("Materials") != null, "Industry dashboard XAML exposes blueprint and material planning");
        ((ListBox)industryWindow.FindName("Pilots")).ItemsSource = new[] { industryFixture };
        ((ListBox)industryWindow.FindName("Pilots")).SelectedIndex=0;
        if(args.Length>0)
        {
            Render(industryWindow,System.IO.Path.ChangeExtension(args[0],".industry.png"));
            ((TabControl)industryWindow.FindName("IndustryTabs")).SelectedIndex=1;
            Render(industryWindow,System.IO.Path.ChangeExtension(args[0],".planner.png"));
        }
        var operations = BackgroundOperations.Current;
        BackgroundOperations.Stop();
        // Window field initialization gets the same cancelled instance only via the constructor below;
        // use the normal singleton, then stop its deferred poll before processing layout.
        var window = new ContractsWindow();
        var current = BackgroundOperations.Current;
        current.Contracts.State.Rows = new() { good, Row(950, 2), wrong, unknown };
        foreach (var row in current.Contracts.State.Rows.Skip(1).Take(1)) ContractService.Evaluate(row, appraisal.RootElement, 90, 0.1m);
        current.Contracts.State.CorporationName = "Example Corporation";
        current.Contracts.State.CorporationId = 42;
        current.Contracts.State.History = new() { new ContractRow { CorporationId = 42, Issuer = "Example Miner", Acceptor = "Corporation Officer", JaniceUrl = "https://janice.e-351.com/a/example", Contract = new CorporationContract { Id = 123456, Status = "finished", AssigneeId = 42, Type = "item_exchange", AcceptorId = 123, Accepted = DateTimeOffset.UtcNow, Price = 650000000, Issued = DateTimeOffset.UtcNow.AddHours(-3) } } };
        current.Contracts.State.LastRefreshUtc = DateTimeOffset.UtcNow;
        typeof(ContractsWindow).GetMethod("Render", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
        ((ComboBox)window.FindName("PilotCombo")).ItemsSource = new[] { new EvePilotProfile { CharacterName = "Corporation Data Toon" } };
        ((ComboBox)window.FindName("PilotCombo")).SelectedIndex = 0;
        BackgroundOperations.Stop();
        Check(((DataGrid)window.FindName("ContractsGrid")).Items.Count == 4, "Contract XAML loads and binds rows");
        ((DataGrid)window.FindName("ContractsGrid")).SelectedIndex = 1;
        Check(((DataGrid)window.FindName("HistoryGrid")).Items.Count == 1, "History view binds persisted acceptance records");
        if (args.Length > 0) { ((TabControl)window.FindName("ContractTabs")).SelectedIndex = 3; Render(window, args[0]); }
        var calendar = new System.Windows.Controls.Calendar { Style = (Style)window.FindResource("CommandCalendar"), DisplayDate = new DateTime(2026, 9, 11), SelectedDate = new DateTime(2026, 9, 11) };
        calendar.ApplyTemplate(); calendar.Measure(new Size(300, 330)); calendar.Arrange(new Rect(0, 0, 300, 330)); calendar.UpdateLayout();
        var calendarItem = (System.Windows.Controls.Primitives.CalendarItem)calendar.Template.FindName("PART_CalendarItem", calendar);
        var header = (Button)calendarItem.Template.FindName("PART_HeaderButton", calendarItem);
        header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(calendar.DisplayMode == CalendarMode.Year, "Themed calendar header navigates to months");
        header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(calendar.DisplayMode == CalendarMode.Decade, "Themed calendar header navigates to years");
        calendar.DisplayMode = CalendarMode.Month;
        var monthView = (Grid)calendarItem.Template.FindName("PART_MonthView", calendarItem);
        var day = monthView.Children.OfType<System.Windows.Controls.Primitives.CalendarDayButton>().First(b => b.DataContext is DateTime d && d == new DateTime(2026, 9, 15));
        day.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => {}, System.Windows.Threading.DispatcherPriority.ContextIdle);
        Check(calendar.SelectedDate == new DateTime(2026, 9, 15), "Themed calendar day selection remains functional");
        if (args.Length > 0) Render(new Window { Content = calendar, Width = 340, Height = 380 }, System.IO.Path.ChangeExtension(args[0], ".calendar.png"));
        var details = new ContractContentsWindow(current.Contracts, current.Sso, good, 0);
        Check(((TextBlock)details.FindName("ResultText")).Text == "CHECKS PASSED", "Contents XAML loads check summary");
        if (args.Length > 0)
        {
            ((DataGrid)details.FindName("ItemsGrid")).ItemsSource = new[] { new ContractItem { Name = "Compressed Veldspar", Quantity = 125000, Included = true, UnitVolume = 0.01 }, new ContractItem { Name = "Compressed Scordite", Quantity = 85000, Included = true, UnitVolume = 0.01 } };
            Render(details, System.IO.Path.ChangeExtension(args[0], ".contents.png"));
        }
        var setup = new ClientSetupWindow();
        BackgroundOperations.Stop();
        Check(setup.FindName("MoonPilot") is ComboBox && setup.FindName("ContractPilot") is ComboBox, "Setup exposes separate corporation readers");
        if (args.Length > 0) Render(setup, System.IO.Path.ChangeExtension(args[0], ".setup.png"));
        var moonWindow = new MoonReportWindow();
        BackgroundOperations.Stop();
        typeof(MoonReportWindow).GetMethod("ApplySnapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(moonWindow, new object[] { moonSnapshot });
        Check(((DataGrid)moonWindow.FindName("OverviewFields")).Items.Count == 4, "Moon overview binds all four active fields");
        ((ComboBox)moonWindow.FindName("OverviewSystem")).SelectedItem = "Mazitah";
        Check(((DataGrid)moonWindow.FindName("OverviewFields")).Items.Count == 2, "System selector filters active fields");
        ((ComboBox)moonWindow.FindName("OverviewSystem")).SelectedItem = "All systems";
        if (args.Length > 0) Render(moonWindow, System.IO.Path.ChangeExtension(args[0], ".moons.png"));
        if (args.Length > 0)
        {
            var fuelWindow = new MoonReportWindow(); BackgroundOperations.Stop();
            typeof(MoonReportWindow).GetMethod("ApplySnapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(fuelWindow, new object[]{moonSnapshot});
            fuelWindow.FocusFuel();
            Render(fuelWindow, System.IO.Path.ChangeExtension(args[0], ".fuel.png"));
        }
        if (args.Length > 0)
        {
            var audit = new MoonReportWindow(); BackgroundOperations.Stop();
            var grid = (DataGrid)audit.FindName("AuditGrid");
            grid.ItemsSource = new[] { new MoonAuditView { MoonName="Joppaya VII - Moon 4", StructureName="Joppaya - Adventure", SystemName="Joppaya", Expired="11 Sept 2026 18:09", Fractured="09 Sept 2026 18:09", TotalMined="34.34M m3", TotalLeft="3.81M m3", Outcome="ORE LEFT", OutcomeBrush="#EF7770", Reliable=true, OreRows=new[] { new MoonOreRowView { Name="Zeolites", TypeId=45490, Color="#CE93D8", Mined="24.24M m3", Remaining="0 m3", InitialM3=24240000 }, new MoonOreRowView { Name="Sylvite", TypeId=45491, Color="#80CBC4", Mined="9.29M m3", Remaining="1.12M m3", InitialM3=10410000, RemainingM3=1120000 } } } };
            grid.RowDetailsVisibilityMode = DataGridRowDetailsVisibilityMode.Visible;
            ((TabControl)audit.FindName("MoonTabs")).SelectedIndex = 3;
            Render(audit, System.IO.Path.ChangeExtension(args[0], ".audit.png"));
        }
        if (args.Length > 0)
        {
            var piWindow = new PlanetaryWindow(); BackgroundOperations.Stop();
            var view = PlanetaryAnalysis.Build(piFixture, DateTimeOffset.UtcNow);
            ((ItemsControl)piWindow.FindName("Colonies")).ItemsSource = PlanetaryGroups.Build(view, new HashSet<string> { "pilot:1", "planet:1:40000001" });
            ((DataGrid)piWindow.FindName("Production")).ItemsSource = view.Production;
            ((ItemsControl)piWindow.FindName("FactorySummary")).ItemsSource = view.FactoryTiers;
            ((ItemsControl)piWindow.FindName("Refills")).ItemsSource = PlanetaryGroups.Build(view, new HashSet<string>(), true);
            Render(piWindow, System.IO.Path.ChangeExtension(args[0], ".pi.png"));
            ((ItemsControl)piWindow.FindName("Extractors")).ItemsSource = PlanetaryExtractors.Build(view, new HashSet<string>(), DateTimeOffset.UtcNow);
            ((TabControl)piWindow.FindName("Tabs")).SelectedIndex = 1;
            var extractorTestGroups = PlanetaryExtractors.Build(view, new HashSet<string>(), DateTimeOffset.UtcNow);
            foreach (var group in extractorTestGroups) group.Planets = Enumerable.Range(0, 15).SelectMany(_ => group.Planets.ToArray()).ToList();
            ((ItemsControl)piWindow.FindName("Extractors")).ItemsSource = extractorTestGroups;
            Render(piWindow, System.IO.Path.ChangeExtension(args[0], ".extractors.png"));
            var extractorScroll = (ScrollViewer)piWindow.FindName("ExtractorScroll");
            extractorScroll.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
            piWindow.UpdateLayout();
            Check(extractorScroll.VerticalOffset > 0, "Extractor page scrolls with the wheel over its nested tables");
            ((DataGrid)piWindow.FindName("CompactExtractorGrid")).ItemsSource = extractorTestGroups.SelectMany(g => g.Planets).SelectMany(p => p.Pins).ToList();
            ((CheckBox)piWindow.FindName("CompactExtractors")).IsChecked = true;
            typeof(PlanetaryWindow).GetMethod("ApplyExtractorMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(piWindow, null);
            Render(piWindow, System.IO.Path.ChangeExtension(args[0], ".extractors-compact.png"));
            Check(((DataGrid)piWindow.FindName("CompactExtractorGrid")).Visibility == Visibility.Visible && extractorScroll.Visibility == Visibility.Collapsed, "Extractor compact mode shows a single flat scrolling table");
        }
        var toast = new OperatingToast("Mazitah - Example Moon", "Glistening ore confirmed in the mining ledger. Open the moon overview to inspect the field.", () => {}, "GLISTENING MOON DETECTED");
        if (args.Length > 0) Render(toast, System.IO.Path.ChangeExtension(args[0], ".toast.png"));
        var appMarkup = System.Xml.Linq.XDocument.Load("App.xaml");
        var resources = appMarkup.Descendants().First(e => e.Name.LocalName == "ResourceDictionary");
        resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        foreach (var source in resources.Descendants().SelectMany(e => e.Attributes("Source"))) source.Value = "/EVE Command Center;component/" + source.Value;
        System.Windows.Application.Current.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
        var previewSettings = new SettingsWindow(new SettingsService());
        Check(previewSettings.FindName("NavHotkeys") != null && previewSettings.FindName("NavThumbnails") != null && previewSettings.FindName("NavCrop") != null, "Restyled preview settings retain hotkey, thumbnail and crop navigation");
        if (args.Length > 0) Render(previewSettings, System.IO.Path.ChangeExtension(args[0], ".preview.png"));
        Console.WriteLine($"{_checks} checks passed.");
    }
    private static void CheckBuybackPeriods()
    {
        var row = new ContractRow { CorporationId = 42, JaniceUrl = "https://janice.e-351.com/a/test", Contract = new CorporationContract { Id = 1, AssigneeId = 42, Status = "finished", Type = "item_exchange", Accepted = new DateTimeOffset(2024, 2, 29, 23, 59, 0, TimeSpan.Zero), Price = 100 } };
        var month = BuybackReport.Build(new[] { row }, 42, new DateTime(2024, 2, 10), "Month");
        Check(month.Count == 29 && month.Last().Value == 100, "Buyback month includes leap day and end-of-day acceptance");
        Check(BuybackReport.Build(new[] { row }, 42, new DateTime(2024, 3, 1), "Month").Sum(b => b.Count) == 0, "Adjacent month does not double-count acceptance");
        Check(BuybackReport.Start(new DateTime(2024, 3, 3), "Week") == new DateTime(2024, 2, 26), "Buyback weeks start Monday across month boundaries");
        Check(BuybackReport.Build(new[] { row }, 99, new DateTime(2024, 2, 1), "Year").Sum(b => b.Count) == 0, "Buyback chart isolates the selected corporation");
        row.Contract.Status = "cancelled";
        Check(BuybackReport.Build(new[] { row }, 42, new DateTime(2024, 2, 1), "Year").Sum(b => b.Count) == 0, "Cancelled contracts are excluded from completed buybacks");
        var ore = new MoonOreRowView { InitialM3 = 1000, RemainingM3 = 400, IskPerM3 = 10, MiningRate = 50 };
        Check(ore.RemainingPercent == 40 && ore.RemainingValue == 4000 && ore.IskPerHour == 1800000, "Ore bars, value and mining-rate estimate use consistent units");
    }

    private static void CheckMoonAlerts()
    {
        var seen = new Dictionary<string, DateTimeOffset>();
        var now = DateTimeOffset.UtcNow;
        var empty = new MoonReportSnapshot { LastRefreshUtc = now };
        Check(MoonMilestones.Observe(empty, 1, seen, now).Count == 0, "Moon alerts establish a quiet baseline");
        var snapshot = new MoonReportSnapshot { LastRefreshUtc = now, CalendarCards = new[] { new MoonCardView { PullId = "a", Status = "READY" }, new MoonCardView { PullId = "b", Status = "FIELD ACTIVE", IsJackpot = true } } };
        var alerts = MoonMilestones.Observe(snapshot, 1, seen, now);
        Check(alerts.Count == 3 && alerts.Any(a => a.Title.Contains("GLISTENING")), "Ready, fractured and Glistening fields create distinct alerts");
        seen = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(JsonSerializer.Serialize(seen))!;
        Check(MoonMilestones.Observe(snapshot, 1, seen, now).Count == 0, "Moon milestone notifications do not repeat after restart");
        Check(MoonMilestones.Observe(snapshot, 2, seen, now).Count == 0, "Another corporation reader establishes its own baseline");
    }

    private static void CheckContractHistory()
    {
        var state = new ContractState();
        ContractRow History(long id, string status, DateTimeOffset? accepted = null) => new() { CorporationId = 42, Contract = new() { Id = id, Status = status, Accepted = accepted, AcceptorId = accepted.HasValue ? 123 : 0 } };
        var now = DateTimeOffset.UtcNow;
        Check(ContractService.RecordHistory(state, 42, new[] { History(1, "outstanding"), History(2, "finished", now.AddDays(-3)) }).Count == 0, "Historical acceptance import does not flood notifications");
        Check(ContractService.RecordHistory(state, 42, new[] { History(1, "finished", now) }).Count == 1, "Outstanding to accepted emits one acceptance");
        Check(state.History.Count == 2, "Contracts missing from later ESI pages remain archived");
        state = JsonSerializer.Deserialize<ContractState>(JsonSerializer.Serialize(state))!;
        Check(ContractService.RecordHistory(state, 42, new[] { History(1, "finished", now) }).Count == 0, "Accepted notification stays suppressed after restart");
        Check(ContractService.RecordHistory(state, 42, new[] { History(3, "cancelled") }).Count == 0, "Cancellation is not acceptance");
        state.LastRefreshUtc = now.AddMinutes(-1);
        Check(ContractService.RecordHistory(state, 42, new[] { History(4, "finished", now) }).Count == 1, "Contract created and accepted between polls is detected");
    }
    private static void CheckFitStacking()
    {
        double[] baseline = { 0.8, 0.4, 0.48, 0.64 };
        double[][] modules = { new[] { -43.125, -32.5, -32.5 }, new[] { -32.5, -32.5 }, new[] { -32.5, -32.5 }, new[] { -32.5, -32.5 } };
        double average = Enumerable.Range(0, 4).Average(i => EveFitDefenseStats.StackedResonance(baseline[i], modules[i]));
        var fit = new EveFitDefenseStats { Available = true, ShieldHp = 21000, ShieldAverageResonance = average, ShieldEhp = 21000 / average, ArmorEhp = 11000, StructureEhp = 12000, ShieldResonanceBeforeModules = baseline, ShieldModuleBonuses = modules };
        var boosted = fit.ApplyShieldCommandBoost(19.7, 19.7);
        double oldOverestimate = 21000 * 1.197 / (average * 0.803) + 23000;
        Check(boosted.OmniEhp < oldOverestimate - 10000, "Harmonizing burst shares hardener penalties instead of inflating EHP");
        Check(boosted.ArmorEhp == fit.ArmorEhp && boosted.StructureEhp == fit.StructureEhp, "Shield burst does not change low-slot armor or hull defenses");
        Check(EveFitDefenseStats.StackedResonance(1, new[] { -20.0 }) == 0.8, "A lone resistance bonus keeps its full strength");
        Check(Math.Abs(EveFitDefenseStats.StackedResonance(0.875, new[] { -32.5, -20.0 }) / EveFitDefenseStats.StackedResonance(1, new[] { -32.5, -20.0 }) - 0.875) < 0.00001, "Damage Control remains outside hardener and burst stacking");
    }

    private static MoonReportSnapshot CheckMoonRecovery()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ecc-moon-checks-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        var state = new MoonReportState();
        for (int i = 1; i <= 4; i++)
        {
            string system = i <= 2 ? "Mazitah" : "Joppaya";
            state.TypePrices[45490] = 1000; state.TypePrices[45491] = 500;
            state.Structures.Add(new EsiCorporationStructure { StructureId=i, TypeId=35835, Name=system+" - Refinery "+i, SystemId=i, FuelExpires=i==4?null:now.AddDays(i==1?12:i==2?65:110), Services=new(){new(){Name="Moon Drilling",State="online"}} });
            state.SystemNames[i]=system;
            state.FuelUpdatedUtc=now; state.FuelAssetsUpdatedUtc=now;
            state.FuelQuantityStatus="Fuel bay quantities shown from ESI snapshot. Alert threshold: 80 days.";
            state.TypeNames[4051]="Nitrogen Fuel Block";
            state.FuelAssets.Add(new EveAssetItem{LocationId=i,LocationFlag="StructureFuel",TypeId=4051,Quantity=1000*i});
            state.Profiles[i] = new MoonProfile { MoonId = i, StructureId = i, MoonName = system + " Moon " + i, StructureName = system + " - Refinery " + i, SystemName = system, ProfileConfigured = true, ZeolitesPercent = 50, SylvitePercent = 50, FieldLifetimeHours = 48 };
            state.Pulls["next" + i] = new MoonPullRecord { Id = "next" + i, MoonId = i, StructureId = i, MoonName = system + " Moon " + i, StructureName = system + " - Refinery " + i, SystemName = system, ExtractionStartUtc = now.AddHours(-12), ChunkArrivalUtc = now.AddDays(50), NaturalDecayUtc = now.AddDays(50).AddHours(3), SeenInLatestExtractionList = true };
        }
        // Two fields must be recovered without any ledger activity. One observed and one formerly misclassified natural fracture.
        state.Pulls["old3"] = new MoonPullRecord { Id = "old3", MoonId = 3, StructureId = 3, ExtractionStartUtc = now.AddDays(-54), ChunkArrivalUtc = now.AddHours(-14), NaturalDecayUtc = now.AddHours(-11), FracturedUtc = now.AddHours(-12), EstimatedFieldExpiryUtc = now.AddHours(36) };
        state.Pulls["old4"] = new MoonPullRecord { Id = "old4", MoonId = 4, StructureId = 4, ExtractionStartUtc = now.AddDays(-54), ChunkArrivalUtc = now.AddHours(-15), NaturalDecayUtc = now.AddHours(-12), OutcomeUnobserved = true, ExpiredUtc = now.AddHours(-10) };
        state.Pulls["anchor"] = new MoonPullRecord { Id="anchor",StructureId=99,MoonId=99,StructureName="Raren - Jean-Luc Peckard",SystemName="Raren",FracturedUtc=now.AddDays(-5),ChunkArrivalUtc=now.AddDays(-5),ExpiredUtc=now.AddDays(-3),OutcomeUnobserved=true };
        var file = System.IO.Path.Combine(directory, "moon-report.json");
        System.IO.File.WriteAllText(file, JsonSerializer.Serialize(state));
        try
        {
            using var service = new MoonReportService(new EveSsoService(), directory);
            var snapshot = service.GetSnapshot();
            Check(snapshot.Fuel.Count==4 && snapshot.Fuel.Single(s=>s.StructureId==2).Quantity.Contains("2,000"), "Fuel snapshot joins bay quantities to the correct structure");
            Check(snapshot.CalendarCards.Where(c=>c.StructureId is >=1 and <=4).All(c=>c.StructureImageUri.Contains("35835")), "Moon icons use actual structure type IDs");
            var fields = snapshot.Cards.Where(c => c.MoonId is >= 1 and <= 4 && c.Status == "FIELD ACTIVE").ToArray();
            Check(fields.Length == 4, "Four active fields survive new extraction and missing mining activity");
            Check(fields.All(c => c.RemainingTotalM3 > 0), "Recovered fields retain ore estimates without ledger activity");
            Check(fields.Count(c => c.Evidence.StartsWith("Inferred")) == 2, "Restart inference is labelled separately from known history");
            Check(snapshot.CalendarCards.Count(c => c.Status == "SCHEDULED" && c.MoonId <= 4) == 4, "Active fields do not erase upcoming extraction schedules");
            Check(service.GetSnapshot().CalendarCards.Count == snapshot.CalendarCards.Count, "Repeated snapshots do not duplicate recovered fields");
            var expired = (MoonReportSnapshot)typeof(MoonReportService).GetMethod("BuildSnapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(service, new object[] { now.AddDays(3) })!;
            Check(expired.ActiveFieldCount == 0, "Recovered and observed fields expire without mining activity");
            Check(expired.Audit.Count >= 4, "Expired fields enter the despawn audit");
            return snapshot;
        }
        finally { System.IO.File.Delete(file); System.IO.Directory.Delete(directory); }
    }
    private static async Task CheckAccessAsync()
    {
        var handler = new AccessEsi();
        using var http = new HttpClient(handler);
        var result = await CorporationAccessService.ProbeEndpointsAsync(http, "test", 123, true);
        Check(result.Allowed && handler.Paths.Any(p => p.Contains("structures")) && handler.Paths.Any(p => p.Contains("extractions")), "Moon gate checks both live structure and extraction endpoints");
        handler.Deny = "structures";
        try { await CorporationAccessService.ProbeEndpointsAsync(http, "test", 123, true); throw new Exception("Denied structures passed gate"); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden) { Check(true, "Scope approval cannot bypass denied structure permissions"); }
        handler.Deny = "contracts";
        try { await CorporationAccessService.ProbeEndpointsAsync(http, "test", 123, false); throw new Exception("Denied contracts passed gate"); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden) { Check(true, "Contract permissions are validated by ESI"); }
        handler.Deny = "";
        Check((await CorporationAccessService.ProbeEndpointsAsync(http, "test", 123, false)).Allowed, "Empty successful contract list still grants access");
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ecc-access-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pilots = new[] { new EvePilotProfile { CharacterId = 123, Scopes = new[] { MoonReportService.MiningScope, MoonReportService.StructureScope } } };
            var access = new CorporationAccessService(new(), http, folder, (_, _) => Task.FromResult("test"), () => Task.FromResult<IReadOnlyList<EvePilotProfile>>(pilots));
            access.State.MoonCharacterId = 123;
            await access.ValidateAsync(pilots);
            handler.Deny = "structures"; handler.DeniedStatus = (HttpStatusCode)429;
            await access.ValidateAsync(pilots);
            Check(access.CanReadMoons && access.MoonStatus.Contains("delayed"), "Rate limiting preserves recently verified Moon access");
            var restarted = new CorporationAccessService(new(), http, folder, (_, _) => Task.FromResult("test"), () => Task.FromResult<IReadOnlyList<EvePilotProfile>>(pilots));
            await restarted.ValidateAsync(pilots);
            Check(restarted.CanReadMoons, "Recent verification survives restart during an ESI outage");
            handler.DeniedStatus = HttpStatusCode.Forbidden;
            await access.ValidateAsync(pilots);
            Check(!access.CanReadMoons && access.State.VerifiedMoonId == 0, "Explicit permission denial revokes cached access");
            handler.DeniedStatus = HttpStatusCode.ServiceUnavailable;
            await access.ValidateAsync(pilots);
            Check(!access.CanReadMoons, "Transient errors cannot grant unverified or revoked access");
            handler.Deny = "";
            await access.ValidateAsync(pilots);
            Check(access.CanReadMoons, "Successful revalidation restores a hidden Moon tab");
            pilots[0].Scopes = pilots[0].Scopes.Append(ContractService.ReadScope).ToArray();
            access.State.ContractCharacterId = 123;
            await access.ValidateAsync(new[] { new EvePilotProfile { CharacterId = 123, Scopes = Array.Empty<string>() } });
            Check(access.CanReadContracts, "Stale setup scopes cannot hide freshly reauthorised Contracts");
            pilots[0].Scopes = Array.Empty<string>();
            await access.ValidateAsync(new[] { new EvePilotProfile { CharacterId = 123, Scopes = new[] { ContractService.ReadScope } } });
            Check(!access.CanReadContracts, "Stale UI grants cannot override currently missing permissions");
            access.State.MoonCharacterId = 456;
            await access.ValidateAsync(pilots);
            Check(!access.CanReadMoons, "Switching readers never inherits another character's access");
        }
        finally { if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true); }

    }
    private sealed class AccessEsi : HttpMessageHandler
    {
        public string Deny = "";
        public HttpStatusCode DeniedStatus = HttpStatusCode.Forbidden;
        public List<string> Paths = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var denied = Deny.Length > 0 && path.Contains(Deny);
            var body = path.Contains("characters/") ? "{\"name\":\"Test Pilot\",\"corporation_id\":42}" : path.EndsWith("corporations/42/") ? "{\"name\":\"Test Corporation\"}" : "[]";
            return Task.FromResult(new HttpResponseMessage(denied ? DeniedStatus : HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static async Task CheckRefreshAsync()
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ecc-contract-checks-" + Guid.NewGuid().ToString("N"));
        var handler = new FakeEsi();
        using var service = new ContractService(new EveSsoService(), new HttpClient(handler), folder, (_, _) => Task.FromResult("test-only-token"));
        var pilot = new EvePilotProfile { CharacterId = 123, Scopes = ContractService.Scopes };
        int notifications = 0;
        service.NewContracts += rows => notifications += rows.Count;
        await service.RefreshAsync(pilot, CancellationToken.None, respectCooldown: false);
        Check(service.State.Rows.Count == 2 && handler.ContractPages == 2, "ESI pagination loads all outstanding contracts");
        Check(notifications == 0, "Refresh baseline does not flood notifications");
        Check(service.NextCheckUtc > DateTimeOffset.UtcNow.AddMinutes(4), "Contract scheduling honors ESI cache expiry");
        int beforeCooldown = handler.ContractPages;
        await service.RefreshAsync(pilot, CancellationToken.None);
        Check(handler.ContractPages == beforeCooldown, "Manual refresh cannot bypass provider cooldown");
        handler.AddNew = true;
        await service.RefreshAsync(pilot, CancellationToken.None, respectCooldown: false);
        Check(notifications == 1, "Refresh dispatches only the new contract");
        Check(handler.NameCalls == 1 && service.State.Rows.All(r => r.Issuer == "Example Miner"), "Issuer identities are batched once and cached across refreshes");
        var items = await service.ItemsAsync(service.State.Rows[0], pilot, CancellationToken.None);
        Check(items.Count == 2 && items.Any(i => !i.Included) && items[0].Name == "Test ore", "Contents preserve receive and provide directions");
        int calls = handler.ItemCalls;
        await service.ItemsAsync(service.State.Rows[0], pilot, CancellationToken.None);
        Check(handler.ItemCalls == calls, "Contents cache avoids repeat ESI reads");
        await service.OpenInGameAsync(1, pilot, CancellationToken.None);
        Check(handler.OpenedInGame, "In-game action uses ESI POST on selected character token");
        handler.Deny = true;
        try { await service.RefreshAsync(pilot, CancellationToken.None, respectCooldown: false); throw new Exception("Expected access error"); }
        catch (InvalidOperationException) { }
        Check(service.LastError != null && service.State.Rows.Count == 3 && !service.IsRefreshing, "Failed refresh preserves last successful snapshot");
        handler.Deny = false;
        handler.Throttle = true;
        try { await service.RefreshAsync(pilot, CancellationToken.None, respectCooldown: false); throw new Exception("Expected throttle"); }
        catch (Exception ex) when (ex.Message.Contains("rate limiting")) { }
        Check(service.NextCheckUtc > DateTimeOffset.UtcNow.AddMinutes(59), "Provider Retry-After extends the five-minute interval");
        using var restored = new ContractService(new EveSsoService(), new HttpClient(new FakeEsi()), folder);
        Check(restored.NextCheckUtc == service.NextCheckUtc && restored.State.EntityNames[999] == "Example Miner", "Cooldown and resolved names survive restart");
        Check(restored.State.Rows.Count == 3 && restored.State.SeenByCorporation[42].Count == 3, "Snapshot and deduplication state persist");
        System.IO.File.Delete(System.IO.Path.Combine(folder, "contracts.json"));
        System.IO.Directory.Delete(folder);
    }
    private sealed class FakeEsi : HttpMessageHandler
    {
        public bool AddNew, Deny, OpenedInGame, Throttle;
        public int ContractPages, ItemCalls, NameCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/contracts/") && Deny) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (path.EndsWith("/contracts/") && Throttle)
            {
                var limited = new HttpResponseMessage((HttpStatusCode)429);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
                return Task.FromResult(limited);
            }
            string json;
            int pages = 1;
            if (path.Contains("/openwindow/contract/"))
            { OpenedInGame = request.Method == HttpMethod.Post && request.Headers.Authorization?.Parameter == "test-only-token"; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            if (path == "/latest/universe/names/") { NameCalls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"id\":999,\"name\":\"Example Miner\"}]") }); }
            if (path.EndsWith("/items/")) { ItemCalls++; json = "[{\"type_id\":500,\"quantity\":10,\"is_included\":true},{\"type_id\":500,\"quantity\":2,\"is_included\":false}]"; }
            else if (path.EndsWith("/contracts/"))
            {
                ContractPages++; pages = 2;
                var ids = request.RequestUri.Query.Contains("page=1") ? new[] { 1L } : AddNew ? new[] { 2L, 3L } : new[] { 2L };
                json = JsonSerializer.Serialize(ids.Select(id => new { contract_id = id, issuer_id = 999, assignee_id = 42, start_location_id = 60008740, status = "outstanding", type = "item_exchange", price = 900, date_expired = DateTimeOffset.UtcNow.AddDays(3), title = "Test" }));
            }
            else if (path.Contains("/characters/")) json = "{\"corporation_id\":42,\"name\":\"Example Miner\"}";
            else if (path.Contains("/corporations/")) json = "{\"name\":\"Example Corporation\"}";
            else if (path.Contains("/stations/")) json = "{\"name\":\"Joppaya IX - Moon 9 - Ardishapur Family Bureau\"}";
            else if (path.Contains("/types/")) json = "{\"name\":\"Test ore\",\"volume\":0.1}";
            else throw new Exception("Unexpected request: " + path);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            response.Headers.Add("X-Pages", pages.ToString());
            if (path.EndsWith("/contracts/")) response.Content.Headers.Expires = DateTimeOffset.UtcNow.AddMinutes(30);
            return Task.FromResult(response);
        }
    }
    private static void Render(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        root.Resources.MergedDictionaries.Add(window.Resources);
        window.Content = null;
        TextElementForeground(root);
        int width = (int)window.Width - 40, height = (int)window.Height - 50;
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => {}, System.Windows.Threading.DispatcherPriority.ContextIdle);
        root.UpdateLayout();
        var frame = new System.Windows.Threading.DispatcherFrame();
        var until = DateTime.UtcNow.AddSeconds(2);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_,_) => { if (DateTime.UtcNow >= until) { timer.Stop(); frame.Continue=false; } };
        timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen()) context.DrawRectangle(new SolidColorBrush(Color.FromRgb(7, 24, 27)), null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path); encoder.Save(stream);
        window.Content = root;
    }
    private static void TextElementForeground(FrameworkElement element) => System.Windows.Documents.TextElement.SetForeground(element, new SolidColorBrush(Color.FromRgb(234, 247, 247)));
}
