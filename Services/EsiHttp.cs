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
        internal readonly SemaphoreSlim Network = new(4, 4);
        internal readonly Dictionary<string, Flight> Flights = new();
        internal readonly Dictionary<string, TimeSpan> Intervals = new();
        internal readonly Dictionary<string, DateTimeOffset> Due = new();
        internal readonly Dictionary<string, string> Groups = new();
        internal readonly Dictionary<string, Cached> Cache = new();
        internal DateTimeOffset Next, Paused;
    }

    internal sealed class Flight
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int Users;
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
            response.Content.Headers.Expires = Until;
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
            Flight? flight = null;
            if (get)
            {
                lock (scheduler.Flights)
                {
                    if (!scheduler.Flights.TryGetValue(key, out flight))
                        scheduler.Flights[key] = flight = new Flight();
                    flight.Users++;
                }
            }
            bool entered = false;
            try
            {
                if (flight != null) { await flight.Gate.WaitAsync(ct).ConfigureAwait(false); entered = true; }
                while (true)
                {
                    TimeSpan wait;
                    string bucket;
                    // Cache hits must never wait behind slow network requests.
                    await scheduler.Gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (get && scheduler.Cache.TryGetValue(key, out var cached) && cached.Until > DateTimeOffset.UtcNow)
                            return cached.Response();
                    }
                    finally { scheduler.Gate.Release(); }
                    await scheduler.Network.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await scheduler.Gate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var now = DateTimeOffset.UtcNow;
                            bucket = owner + ":" + scheduler.Groups.GetValueOrDefault(route, route);
                            wait = new[] { scheduler.Next, scheduler.Paused, scheduler.Due.GetValueOrDefault(bucket) }.Max() - now;
                            if (wait <= TimeSpan.Zero)
                            {
                                scheduler.Next = now.AddMilliseconds(250);
                                if (scheduler.Intervals.TryGetValue(bucket, out var interval))
                                    scheduler.Due[bucket] = now.Add(interval);
                            }
                        }
                        finally { scheduler.Gate.Release(); }
                        if (wait <= TimeSpan.Zero)
                            return await SendAndObserveAsync(request, ct, key, owner, route, bucket, get).ConfigureAwait(false);
                    }
                    finally { scheduler.Network.Release(); }
                    EsiDiagnostics.Write($"QUEUE {request.Method} {route} wait {wait.TotalSeconds:0}s (pacing / provider cooldown)");
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                if (flight != null)
                {
                    if (entered) flight.Gate.Release();
                    lock (scheduler.Flights)
                    {
                        if (--flight.Users == 0) { scheduler.Flights.Remove(key); flight.Gate.Dispose(); }
                    }
                }
            }
        }

        private async Task<HttpResponseMessage> SendAndObserveAsync(HttpRequestMessage request, CancellationToken ct,
            string key, string owner, string route, string bucket, bool get)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));

            EsiDiagnostics.Write($"SEND {request.Method} {route}");
            HttpResponseMessage response;
            var started = DateTimeOffset.UtcNow;
            try { response = await base.SendAsync(request, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { EsiDiagnostics.Write($"CANCEL/TIMEOUT {request.Method} {route} after {(DateTimeOffset.UtcNow - started).TotalSeconds:0.0}s"); throw; }
            catch (HttpRequestException) { EsiDiagnostics.Write($"NETWORK ERROR {request.Method} {route}"); throw; }
            response.Headers.Date ??= DateTimeOffset.UtcNow;
            byte[] body;
            try { body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false); }
            catch { response.Dispose(); throw; }
            await scheduler.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                DateTimeOffset now;
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
                    scheduler.Intervals[bucket] = TimeSpan.FromSeconds(delay);
                    scheduler.Due[bucket] = new[] { scheduler.Due.GetValueOrDefault(bucket), now.AddSeconds(delay) }.Max();
                }
                if (int.TryParse(Header("X-ESI-Error-Limit-Remain"), out var errors) && errors < 25)
                    scheduler.Paused = new[] { scheduler.Paused, now.AddSeconds(int.TryParse(Header("X-ESI-Error-Limit-Reset"), out var reset) ? Math.Max(60, reset) + 2 : 62) }.Max();
                int status = (int)response.StatusCode;
                EsiDiagnostics.Write($"HTTP {status} {request.Method} {route} {(now - started).TotalSeconds:0.0}s group={Header("X-Ratelimit-Group")} remaining={Header("X-Ratelimit-Remaining")} errors={Header("X-ESI-Error-Limit-Remain")} retry={Header("Retry-After")}");
                if (status is 420 or 429 or 503)
                {
                    var retry = response.Headers.RetryAfter;
                    var until = retry?.Date ?? now.Add(retry?.Delta ?? TimeSpan.FromMinutes(2));
                    // Unknown/deep-server limits are conservatively shared across ESI callers.
                    if (status == 429 && Header("X-Ratelimit-Group").Length > 0)
                        scheduler.Due[bucket] = new[] { scheduler.Due.GetValueOrDefault(bucket), until.AddSeconds(2) }.Max();
                    else scheduler.Paused = new[] { scheduler.Paused, until.AddSeconds(2) }.Max();
                }
                var expires = response.Content.Headers.Expires ?? now;
                if (response.Headers.CacheControl?.MaxAge is { } age)
                    expires = now.Add(age - (response.Headers.Age ?? TimeSpan.Zero));
                if (status is 403 or 404) expires = now.AddMinutes(5);
                if (status is 420 or 429 or 503) expires = new[] { scheduler.Paused, scheduler.Due.GetValueOrDefault(bucket) }.Max();
                if (get && expires > now && !(response.Headers.CacheControl?.NoStore ?? false))
                {
                    foreach (var stale in scheduler.Cache.Where(x => x.Value.Until <= now).Select(x => x.Key).ToArray()) scheduler.Cache.Remove(stale);
                    while (scheduler.Cache.Count > 0 && (scheduler.Cache.Count >= 1024 || scheduler.Cache.Sum(x => (long)x.Value.Body.Length) + body.Length > 64_000_000))
                        scheduler.Cache.Remove(scheduler.Cache.Keys.First());
                    if (body.Length <= 2_000_000)
                        scheduler.Cache[key] = new(response.StatusCode, body, response.Headers.ToArray(), response.Content.Headers.ToArray(), expires);
                }
                return response;
            }
            catch { response.Dispose(); throw; }
            finally { scheduler.Gate.Release(); }
        }
    }
}
