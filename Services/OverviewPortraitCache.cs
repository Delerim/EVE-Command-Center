using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EveCommandCenter.Services;

/// <summary>UI-thread-owned portrait cache. Failed downloads can recover without relinking a pilot.</summary>
internal sealed class OverviewPortraitCache : IDisposable
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim Downloads = new(2);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;
    private readonly Func<string, CancellationToken, Task<byte[]>> _download;
    private readonly Func<DateTime> _utcNow;

    internal OverviewPortraitCache(Func<string, CancellationToken, Task<byte[]>>? download = null, Func<DateTime>? utcNow = null)
    {
        _download = download ?? ((url, token) => Client.GetByteArrayAsync(url, token));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    internal ImageSource? Get(string url)
    {
        if (_disposed || string.IsNullOrWhiteSpace(url)) return null;
        if (!_entries.TryGetValue(url, out var entry)) _entries[url] = entry = new Entry();
        if (entry.Image == null && !entry.Busy && _utcNow() >= entry.RetryAfter)
        {
            entry.Busy = true;
            _ = LoadAsync(url, entry);
        }
        return entry.Image;
    }

    private async Task LoadAsync(string url, Entry entry)
    {
        var token = _lifetime.Token;
        bool entered = false;
        try
        {
            await Downloads.WaitAsync(token);
            entered = true;
            byte[] bytes = await _download(url, token);
            token.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 64;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            entry.Image = image;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Overview portrait] " + ex.GetType().Name);
        }
        finally
        {
            entry.RetryAfter = _utcNow().AddMinutes(1);
            entry.Busy = false;
            if (entered) Downloads.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _entries.Clear();
        // Pending tasks hold the token until cancellation has unwound.
    }

    private sealed class Entry
    {
        internal ImageSource? Image;
        internal bool Busy;
        internal DateTime RetryAfter;
    }
}
