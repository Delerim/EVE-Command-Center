using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EveCommandCenter.Services;

/// <summary>One process-wide ESI queue. Queue time does not consume the network timeout.</summary>
public static class EsiHttp
{
    private static readonly Scheduler Shared = new();
    public static HttpClient CreateClient() => new(new Handler(Shared, new HttpClientHandler()))
    { Timeout = Timeout.InfiniteTimeSpan };

    public sealed class Scheduler
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal readonly Dictionary<string, DateTimeOffset> Due = new();
        internal readonly Dictionary<string, string> Groups = new();
        internal readonly Dictionary<string, Cached> Cache = new();
        internal DateTimeOffset Next, Paused;
    }

    internal sealed record Cached(HttpStatusCode Status, byte[] Body,
        KeyValuePair<string, IEnumerable<string>>[] Headers,
        KeyValuePair<string, IEnumerable<string>>[] ContentHeaders, DateTimeOffset Until)
    {
        internal HttpResponseMessage Response()
        {
            var response = new HttpResponseMessage(Status) { Content = new ByteArrayContent(Body) };
            foreach (var h in Headers) response.Headers.TryAddWithoutValidation(h.Key, h.Value);
            foreach (var h in ContentHeaders) response.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return response;
        }
    }

    public sealed class Handler(Scheduler scheduler, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.Host != "esi.evetech.net")
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(35));
                return await base.SendAsync(request, timeout.Token).ConfigureAwait(false);
            }
            string auth = request.Headers.Authorization?.ToString() ?? "public";
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(auth)));
            string owner = identity;
            // Group limits survive access-token renewal; response caches remain token-isolated.
            try
            {
                string payload = (request.Headers.Authorization?.Parameter ?? "").Split('.')[1];
                payload = payload.Replace('-', '+').Replace('_', '/');
                using var json = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
                owner = json.RootElement.GetProperty("sub").GetString() ?? identity;
            }
            catch { }
            string route = request.RequestUri.AbsolutePath;
            string key = identity + request.Method + request.RequestUri + string.Join(";", request.Headers.AcceptLanguage) +
                (request.Headers.TryGetValues("X-Compatibility-Date", out var dates) ? string.Join(";", dates) : "");
            bool get = request.Method == HttpMethod.Get;
            while (true)
            {
                TimeSpan wait;
                await scheduler.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    if (get && scheduler.Cache.TryGetValue(key, out var cached) && cached.Until > now)
                        return cached.Response();
                    string bucket = owner + ":" + scheduler.Groups.GetValueOrDefault(route, route);
                    var due = new[] { scheduler.Next, scheduler.Paused, scheduler.Due.GetValueOrDefault(bucket) }.Max();
                    wait = due - now;
                    if (wait <= TimeSpan.Zero)
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(35));
                        scheduler.Next = now.AddMilliseconds(750);
                        var response = await base.SendAsync(request, timeout.Token).ConfigureAwait(false);
                        byte[] body;
                        try { body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false); }
                        catch { response.Dispose(); throw; }
                        now = DateTimeOffset.UtcNow;
                        string Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() ?? "" : "";
                        if (Header("X-Ratelimit-Group") is { Length: > 0 } group)
                        {
                            scheduler.Groups[route] = group;
                            bucket = owner + ":" + group;
                        }
                        var limit = Header("X-Ratelimit-Limit").Split('/');
                        if (limit.Length == 2 && limit[1].Length > 1 && double.TryParse(limit[0], out var tokens) && tokens > 0 &&
                            double.TryParse(limit[1][..^1], out var count) && count > 0 && count <= 86400)
                        {
                            double seconds = count * (limit[1][^1] == 'h' ? 3600 : limit[1][^1] == 'm' ? 60 : 1);
                            // Budget at 70% of the advertised rate, assuming successful responses cost two tokens.
                            double delay = seconds * 2 / (tokens * 0.7);
                            if (double.TryParse(Header("X-Ratelimit-Remaining"), out var left) && left < Math.Max(10, tokens * 0.2))
                                delay = Math.Max(delay, seconds);
                            scheduler.Due[bucket] = now.AddSeconds(delay);
                        }
                        if (int.TryParse(Header("X-ESI-Error-Limit-Remain"), out var errors) && errors < 25)
                            scheduler.Paused = now.AddSeconds(int.TryParse(Header("X-ESI-Error-Limit-Reset"), out var reset) ? Math.Max(60, reset) + 2 : 62);
                        int status = (int)response.StatusCode;
                        if (status is 420 or 429 or 503)
                        {
                            var retry = response.Headers.RetryAfter;
                            var until = retry?.Date ?? now.Add(retry?.Delta ?? TimeSpan.FromMinutes(2));
                            // Unknown/deep-server limits are conservatively shared across ESI callers.
                            scheduler.Paused = new[] { scheduler.Paused, until.AddSeconds(2) }.Max();
                        }
                        var expires = response.Content.Headers.Expires ?? now;
                        if (response.Headers.CacheControl?.MaxAge is { } age)
                            expires = now.Add(age - (response.Headers.Age ?? TimeSpan.Zero));
                        if (status is 403 or 404) expires = now.AddMinutes(5);
                        if (status is 420 or 429 or 503) expires = scheduler.Paused;
                        if (get && expires > now && ! (response.Headers.CacheControl?.NoStore ?? false))
                        {
                            foreach (var stale in scheduler.Cache.Where(x => x.Value.Until <= now).Select(x => x.Key).ToArray()) scheduler.Cache.Remove(stale);
                            if (scheduler.Cache.Count >= 256) scheduler.Cache.Remove(scheduler.Cache.Keys.First());
                            if (body.Length <= 2_000_000)
                                scheduler.Cache[key] = new(response.StatusCode, body, response.Headers.ToArray(), response.Content.Headers.ToArray(), expires);
                        }
                        return response;
                    }
                }
                finally { scheduler.Gate.Release(); }
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }
    }
}
