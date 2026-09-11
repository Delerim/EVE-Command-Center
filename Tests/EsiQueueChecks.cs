using System.Net;
using System.Net.Http;
using EveCommandCenter.Services;

internal static partial class Program
{
    private sealed class EsiFake(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromResult(reply(request)); }
    }
    private static async Task CheckEsiQueue()
    {
        var fake = new EsiFake(_ => {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            response.Content.Headers.Expires = DateTimeOffset.UtcNow.AddMinutes(5);
            response.Headers.TryAddWithoutValidation("X-Pages", "3");
            return response;
        });
        var shared = new EsiHttp.Scheduler();
        using var client = new HttpClient(new EsiHttp.Handler(shared, fake));
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.GetAsync("https://esi.evetech.net/latest/universe/types/1/")));
        Check(fake.Calls == 1, "Concurrent ESI reads reuse one response until cache expiry");
        Check(responses.All(r => r.Headers.GetValues("X-Pages").Single() == "3"), "Cached ESI pages retain pagination headers");
        foreach (var r in responses) r.Dispose();
        using var authenticated = new HttpRequestMessage(HttpMethod.Get, "https://esi.evetech.net/latest/universe/types/1/");
        authenticated.Headers.Authorization = new("Bearer", "different-reader");
        using var isolated = await client.SendAsync(authenticated);
        Check(fake.Calls == 2, "Authenticated data is isolated from another reader's cache");

        var throttle = new EsiFake(_ => {
            var r = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("limited") };
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return r;
        });
        using var limited = new HttpClient(new EsiHttp.Handler(new(), throttle));
        using var first = await limited.GetAsync("https://esi.evetech.net/latest/characters/1/ship/");
        using var repeat = await limited.GetAsync("https://esi.evetech.net/latest/characters/1/ship/");
        Check(throttle.Calls == 1 && (int)repeat.StatusCode == 429, "Repeated limited requests cannot hammer ESI during Retry-After");
        using var cancel = new CancellationTokenSource(100);
        try { await limited.GetAsync("https://esi.evetech.net/latest/characters/2/ship/", cancel.Token); Check(false, "Cooldown must queue"); }
        catch (OperationCanceledException) { Check(throttle.Calls == 1, "Shared cooldown queues other callers and supports cancellation"); }

        var budget = new EsiFake(_ => {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            r.Headers.TryAddWithoutValidation("X-Ratelimit-Group", "char-location");
            r.Headers.TryAddWithoutValidation("X-Ratelimit-Limit", "150/15m");
            r.Headers.TryAddWithoutValidation("X-Ratelimit-Remaining", "148");
            return r;
        });
        using var paced = new HttpClient(new EsiHttp.Handler(new(), budget));
        using var one = await paced.GetAsync("https://esi.evetech.net/latest/characters/1/ship/");
        using var stop = new CancellationTokenSource(900);
        try { await paced.GetAsync("https://esi.evetech.net/latest/characters/1/ship/", stop.Token); Check(false, "Group budget must queue"); }
        catch (OperationCanceledException) { Check(budget.Calls == 1, "Advertised group budget spaces requests beyond the global pacing floor"); }
    }
}
