using System.Windows.Threading;
namespace EveCommandCenter.Services;

// Windows system sounds share playback. Space fallbacks so one toon cannot cancel another.
internal sealed class AlertSoundQueue : IDisposable
{
    private readonly Queue<(string key, Action play)> _pending = new();
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer = new() { Interval=TimeSpan.FromMilliseconds(900) };
    internal AlertSoundQueue(){_timer.Tick+=(_,_)=>Advance();}
    internal void Enqueue(string key,Action play)
    {
        if(_pending.Count>=128 || !_keys.Add(key))return;
        _pending.Enqueue((key,play));
        if(!_timer.IsEnabled){_timer.Start();Advance();}
    }
    internal void Advance()
    {
        if(!_pending.TryDequeue(out var item)){_timer.Stop();return;}
        try{item.play();}catch(Exception ex){System.Diagnostics.Debug.WriteLine("[Alert sound] "+ex.Message);}
        finally{_keys.Remove(item.key);}
    }
    public void Dispose(){_timer.Stop();_pending.Clear();_keys.Clear();}
}
