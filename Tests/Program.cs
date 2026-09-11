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

internal static class Program
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
        foreach (string name in new[] { "Raren \u2013 Ducks Migration", "Mazitah - Eagle One" })
        {
            var structure = Row(location: 1_000_000_000_001, name: name);
            ContractService.Evaluate(structure, appraisal.RootElement, 90, 0.1m);
            Check(structure.Passed, "Approved structure exact name: " + name);
        }
        var suffix = Row(location: 1_000_000_000_001, name: "Mazitah - Eagle One EXTRA");
        ContractService.Evaluate(suffix, appraisal.RootElement, 90, 0.1m);
        Check(suffix.HasMismatch, "Partial destination match rejected");
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
        var app = new System.Windows.Application();
        var operations = BackgroundOperations.Current;
        BackgroundOperations.Stop();
        // Window field initialization gets the same cancelled instance only via the constructor below;
        // use the normal singleton, then stop its deferred poll before processing layout.
        var window = new ContractsWindow();
        var current = BackgroundOperations.Current;
        current.Contracts.State.Rows = new() { good, Row(950, 2), wrong, unknown };
        foreach (var row in current.Contracts.State.Rows.Skip(1).Take(1)) ContractService.Evaluate(row, appraisal.RootElement, 90, 0.1m);
        current.Contracts.State.CorporationName = "Example Corporation";
        current.Contracts.State.LastRefreshUtc = DateTimeOffset.UtcNow;
        typeof(ContractsWindow).GetMethod("Render", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(window, null);
        ((ComboBox)window.FindName("PilotCombo")).ItemsSource = new[] { new EvePilotProfile { CharacterName = "Corporation Data Toon" } };
        ((ComboBox)window.FindName("PilotCombo")).SelectedIndex = 0;
        BackgroundOperations.Stop();
        Check(((DataGrid)window.FindName("ContractsGrid")).Items.Count == 4, "Contract XAML loads and binds rows");
        ((DataGrid)window.FindName("ContractsGrid")).SelectedIndex = 1;
        if (args.Length > 0) Render(window, args[0]);
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
        Console.WriteLine($"{_checks} checks passed.");
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
            state.Profiles[i] = new MoonProfile { MoonId = i, StructureId = i, MoonName = system + " Moon " + i, StructureName = system + " - Refinery " + i, SystemName = system, ProfileConfigured = true, ZeolitesPercent = 50, SylvitePercent = 50, FieldLifetimeHours = 48 };
            state.Pulls["next" + i] = new MoonPullRecord { Id = "next" + i, MoonId = i, StructureId = i, MoonName = system + " Moon " + i, StructureName = system + " - Refinery " + i, SystemName = system, ExtractionStartUtc = now.AddHours(-12), ChunkArrivalUtc = now.AddDays(50), NaturalDecayUtc = now.AddDays(50).AddHours(3), SeenInLatestExtractionList = true };
        }
        // Two fields must be recovered without any ledger activity. One observed and one formerly misclassified natural fracture.
        state.Pulls["old3"] = new MoonPullRecord { Id = "old3", MoonId = 3, StructureId = 3, ExtractionStartUtc = now.AddDays(-54), ChunkArrivalUtc = now.AddHours(-14), NaturalDecayUtc = now.AddHours(-11), FracturedUtc = now.AddHours(-12), EstimatedFieldExpiryUtc = now.AddHours(36) };
        state.Pulls["old4"] = new MoonPullRecord { Id = "old4", MoonId = 4, StructureId = 4, ExtractionStartUtc = now.AddDays(-54), ChunkArrivalUtc = now.AddHours(-15), NaturalDecayUtc = now.AddHours(-12), OutcomeUnobserved = true, ExpiredUtc = now.AddHours(-10) };
        var file = System.IO.Path.Combine(directory, "moon-report.json");
        System.IO.File.WriteAllText(file, JsonSerializer.Serialize(state));
        try
        {
            using var service = new MoonReportService(new EveSsoService(), directory);
            var snapshot = service.GetSnapshot();
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
    }
    private sealed class AccessEsi : HttpMessageHandler
    {
        public string Deny = "";
        public List<string> Paths = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var denied = Deny.Length > 0 && path.Contains(Deny);
            var body = path.Contains("characters/") ? "{\"name\":\"Test Pilot\",\"corporation_id\":42}" : path.EndsWith("corporations/42/") ? "{\"name\":\"Test Corporation\"}" : "[]";
            return Task.FromResult(new HttpResponseMessage(denied ? HttpStatusCode.Forbidden : HttpStatusCode.OK) { Content = new StringContent(body) });
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
        await service.RefreshAsync(pilot, CancellationToken.None);
        Check(service.State.Rows.Count == 2 && handler.ContractPages == 2, "ESI pagination loads all outstanding contracts");
        Check(notifications == 0, "Refresh baseline does not flood notifications");
        handler.AddNew = true;
        await service.RefreshAsync(pilot, CancellationToken.None);
        Check(notifications == 1, "Refresh dispatches only the new contract");
        var items = await service.ItemsAsync(service.State.Rows[0], pilot, CancellationToken.None);
        Check(items.Count == 2 && items.Any(i => !i.Included) && items[0].Name == "Test ore", "Contents preserve receive and provide directions");
        int calls = handler.ItemCalls;
        await service.ItemsAsync(service.State.Rows[0], pilot, CancellationToken.None);
        Check(handler.ItemCalls == calls, "Contents cache avoids repeat ESI reads");
        await service.OpenInGameAsync(1, pilot, CancellationToken.None);
        Check(handler.OpenedInGame, "In-game action uses ESI POST on selected character token");
        handler.Deny = true;
        try { await service.RefreshAsync(pilot, CancellationToken.None); throw new Exception("Expected access error"); }
        catch (InvalidOperationException) { }
        Check(service.LastError != null && service.State.Rows.Count == 3 && !service.IsRefreshing, "Failed refresh preserves last successful snapshot");
        using var restored = new ContractService(new EveSsoService(), new HttpClient(new FakeEsi()), folder);
        Check(restored.State.Rows.Count == 3 && restored.State.SeenByCorporation[42].Count == 3, "Snapshot and deduplication state persist");
        System.IO.File.Delete(System.IO.Path.Combine(folder, "contracts.json"));
        System.IO.Directory.Delete(folder);
    }
    private sealed class FakeEsi : HttpMessageHandler
    {
        public bool AddNew, Deny, OpenedInGame;
        public int ContractPages, ItemCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/contracts/") && Deny) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            string json;
            int pages = 1;
            if (path.Contains("/openwindow/contract/"))
            { OpenedInGame = request.Method == HttpMethod.Post && request.Headers.Authorization?.Parameter == "test-only-token"; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
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
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen()) context.DrawRectangle(new SolidColorBrush(Color.FromRgb(7, 24, 27)), null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path); encoder.Save(stream);
    }
    private static void TextElementForeground(FrameworkElement element) => System.Windows.Documents.TextElement.SetForeground(element, new SolidColorBrush(Color.FromRgb(234, 247, 247)));
}
