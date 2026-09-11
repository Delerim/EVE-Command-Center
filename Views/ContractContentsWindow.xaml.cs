using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

public partial class ContractContentsWindow : Window
{
    private readonly ContractService _service;
    private readonly EveSsoService _sso;
    private readonly ContractRow _row;
    private readonly long _pilotId;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<ContractItem> _items = Array.Empty<ContractItem>();
    private bool _loading;
    public ContractContentsWindow(ContractService service, EveSsoService sso, ContractRow row, long pilotId)
    {
        InitializeComponent();
        _service = service; _sso = sso; _row = row; _pilotId = pilotId;
        IssuerText.Text = row.Issuer + " - " + row.PriceText;
        TitleText.Text = "Contract #" + row.Contract.Id + " | " + row.Contract.Title;
        ResultText.Text = row.Result;
        ResultText.Foreground = CheckBorder.BorderBrush = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(row.ResultColor)!;
        ChecksText.Text = "PRICE: " + row.PriceCheck + "  |  JITA BUY: " + row.ActualPercentText + "  |  LOCATION: " + row.LocationCheck;
        ReasonText.Text = row.Reason;
        LocationText.Text = row.Location + " | Expires " + row.ExpiresText;
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _lifetime.Cancel();
    }
    private async Task<EvePilotProfile> PilotAsync() => (await _sso.LoadPilotsAsync()).FirstOrDefault(p => p.CharacterId == _pilotId)
        ?? throw new InvalidOperationException("The selected character is no longer linked. Reconnect it in Contracts.");
    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true; RetryButton.IsEnabled = false;
        StatusText.Text = "Loading contract contents from ESI...";
        try
        {
            _items = await _service.ItemsAsync(_row, await PilotAsync(), _lifetime.Token);
            ApplyFilter();
            TotalsText.Text = $"{_items.Count:N0} item lines | {_items.Where(i => i.Included).Sum(i => i.Volume):N2} m3 received";
            StatusText.Text = "Contents loaded. YOU PROVIDE marks requested items; inspect both directions before accepting in EVE.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = "Could not load contents. " + ex.Message; }
        finally { _loading = false; RetryButton.IsEnabled = true; }
    }
    private void ApplyFilter()
    {
        if (ItemsGrid == null || ItemSearchBox == null) return;
        ItemsGrid.ItemsSource = _items.Where(i => i.Name.Contains(ItemSearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private void Search_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();
    private async void Retry_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private void Janice_Click(object sender, RoutedEventArgs e)
    {
        if (_row.JaniceUrl == null) { StatusText.Text = "This contract has no recognized Janice link."; return; }
        try { Process.Start(new ProcessStartInfo(_row.JaniceUrl) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void Game_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var pilot = await PilotAsync();
            await _service.OpenInGameAsync(_row.Contract.Id, pilot, _lifetime.Token);
            StatusText.Text = "Requested contract window for " + pilot.CharacterName + ". Make sure that character is logged into EVE.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
}
