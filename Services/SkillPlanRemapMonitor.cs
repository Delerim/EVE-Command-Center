using System;
using Application = System.Windows.Application;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using EveCommandCenter.Models;
using EveCommandCenter.Views;

namespace EveCommandCenter.Services;

/// <summary>Checks linked pilots while ECC is running; reminders stay until explicit acknowledgement.</summary>
public sealed class SkillPlanRemapMonitor : IDisposable
{
    public static SkillPlanRemapMonitor Instance { get; } = new();
    private readonly EveSsoService _sso = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(2) };
    private readonly Dictionary<long, (EvePilotProfile Pilot, EvePilotDashboard Data)> _last = new();
    private readonly Dictionary<string, (EvePilotProfile Pilot, SkillRemapAssessment Alert)> _due = new();
    private SkillPlanRemapReminderWindow? _window;
    private bool _started, _busy, _disposed;

    private SkillPlanRemapMonitor() { _timer.Tick += Tick; }
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _timer.Start();
    }
    private async void Tick(object? sender, EventArgs e)
    {
        _timer.Interval = TimeSpan.FromMinutes(30);
        await RefreshAllAsync();
    }
    private async Task RefreshAllAsync()
    {
        if (_busy || _disposed) return;
        _busy = true;
        try
        {
            var profiles = await _sso.LoadPilotsAsync();
            foreach (var pilot in profiles)
            {
                if (_shutdown.IsCancellationRequested) break;
                // No plan = no extra ESI requests.
                try { if (SkillPlanImportService.Load(pilot.CharacterId) == null) continue; }
                catch (Exception ex) { EsiDiagnostics.Write("Skill plan load: " + ex.Message); continue; }
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    var data = await _sso.GetDashboardAsync(pilot, timeout.Token);
                    if (!_shutdown.IsCancellationRequested) Observe(pilot, data);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
                catch (Exception ex) { EsiDiagnostics.Write("Remap monitor " + pilot.CharacterName + ": " + ex.Message); }
            }
        }
        catch (Exception ex) { EsiDiagnostics.Write("Remap monitor refresh: " + ex.Message); }
        finally { _busy = false; }
    }
    public void Observe(EvePilotProfile pilot, EvePilotDashboard data)
    {
        if (_disposed || pilot.CharacterId != data.Summary.CharacterId) return;
        _last[pilot.CharacterId] = (pilot, data);
        CheckCached();
    }
    public void CheckCached()
    {
        if (_disposed) return;
        _due.Clear();
        foreach (var pair in _last.Values)
        {
            StoredSkillImport? plan;
            try { plan = SkillPlanImportService.Load(pair.Pilot.CharacterId); }
            catch (Exception ex) { EsiDiagnostics.Write("Remap state: " + ex.Message); continue; }
            if (plan == null) continue;
            foreach (var reminder in SkillPlanImportService.Assess(plan, pair.Data).Where(x => x.Due && !x.Acknowledged))
            {
                string key = pair.Pilot.CharacterId + ":" + SkillPlanImportService.RemapKey(plan.Document, reminder.Remap);
                _due[key] = (pair.Pilot, reminder);
            }
        }
        if (_disposed || Application.Current == null) return;
        Application.Current.Dispatcher.BeginInvoke(new Action(ShowDue));
    }
    private void ShowDue()
    {
        if (_disposed) return;
        if (_due.Count == 0)
        {
            if (_window != null) { _window.Close(); _window = null; }
            return;
        }
        var alerts = _due.OrderBy(x => x.Key).Select(x => x.Value).ToArray();
        if (_window == null)
        {
            _window = new SkillPlanRemapReminderWindow();
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        _window.Display(alerts, (pilot, remap) => {
            SkillPlanImportService.Acknowledge(pilot.CharacterId, remap.Id);
            CheckCached();
        });
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop(); _timer.Tick -= Tick;
        _shutdown.Cancel(); _shutdown.Dispose();
        _window?.Close(); _window = null;
    }
}
