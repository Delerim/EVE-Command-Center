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
    private sealed class EsiAsyncFake(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>reply(request,ct);
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

        var ageOnly = new EsiFake(_ => {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            r.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge=TimeSpan.FromHours(1) };
            return r;
        });
        using var cacheClient = new HttpClient(new EsiHttp.Handler(new(), ageOnly));
        using var fetched = await cacheClient.GetAsync("https://esi.evetech.net/latest/corporation/1/mining/observers/1/");
        using var cacheOne = await cacheClient.GetAsync("https://esi.evetech.net/latest/corporation/1/mining/observers/1/");
        using var cacheTwo = await cacheClient.GetAsync("https://esi.evetech.net/latest/corporation/1/mining/observers/1/");
        Check(cacheOne.Content.Headers.Expires.HasValue && cacheOne.Content.Headers.Expires == cacheTwo.Content.Headers.Expires && ageOnly.Calls == 1, "Cache hits preserve an absolute ledger expiry instead of postponing freshness");

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

        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active=0, peak=0, started=0;
        var slow=new EsiAsyncFake(async (r,ct)=> {
            if(r.RequestUri!.AbsolutePath.EndsWith("/cached/"))
            {
                var hit=new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("cached")};
                hit.Content.Headers.Expires=DateTimeOffset.UtcNow.AddMinutes(5); return hit;
            }
            int current=Interlocked.Increment(ref active);
            Interlocked.Increment(ref started);
            int previous;
            do {previous=peak;} while(current>previous && Interlocked.CompareExchange(ref peak,current,previous)!=previous);
            if(current==4)fourStarted.TrySetResult();
            try {await release.Task.WaitAsync(ct); return new(HttpStatusCode.OK){Content=new StringContent("ok")};}
            finally {Interlocked.Decrement(ref active);}
        });
        using var parallel=new HttpClient(new EsiHttp.Handler(new(),slow));
        using var primed=await parallel.GetAsync("https://esi.evetech.net/latest/cached/");
        var slowRequests=Enumerable.Range(0,8).Select(i=>parallel.GetAsync($"https://esi.evetech.net/latest/slow/{i}/")).ToArray();
        try
        {
            await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(started==4 && peak==4,"Independent ESI requests overlap with a four-request maximum");
            using var cacheDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(1));
            using var quick=await parallel.GetAsync("https://esi.evetech.net/latest/cached/",cacheDeadline.Token);
            Check(await quick.Content.ReadAsStringAsync()=="cached","Cached reads bypass occupied network slots");
        }
        finally {release.TrySetResult();}
        foreach(var response in await Task.WhenAll(slowRequests))response.Dispose();
        Check(peak==4,"Queued requests never exceed the global concurrency bound");

        var grouped=new EsiFake(r=> {
            bool blocked=r.Headers.Authorization?.Parameter=="pilot-a";
            var response=new HttpResponseMessage(blocked?(HttpStatusCode)429:HttpStatusCode.OK){Content=new StringContent("{}")};
            response.Headers.TryAddWithoutValidation("X-Ratelimit-Group","location");
            if(blocked)response.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return response;
        });
        using var groupedClient=new HttpClient(new EsiHttp.Handler(new(),grouped));
        HttpRequestMessage ForPilot(string pilot,string query="")
        {
            var request=new HttpRequestMessage(HttpMethod.Get,"https://esi.evetech.net/latest/characters/1/ship/"+query);
            request.Headers.Authorization=new("Bearer",pilot);return request;
        }
        using var a=ForPilot("pilot-a");using var blockedA=await groupedClient.SendAsync(a);
        using var b=ForPilot("pilot-b");using var bDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var allowedB=await groupedClient.SendAsync(b,bDeadline.Token);
        Check(allowedB.IsSuccessStatusCode && grouped.Calls==2,"A known group cooldown does not pause unrelated pilots");
        using var aAgain=ForPilot("pilot-a","?page=2");using var aDeadline=new CancellationTokenSource(100);
        try {await groupedClient.SendAsync(aAgain,aDeadline.Token);Check(false,"Same pilot group must remain paused");}
        catch(OperationCanceledException) {Check(grouped.Calls==2,"Group Retry-After also blocks uncached requests in that pilot bucket");}

        var pageRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pagesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int pageCalls = 0;
        bool failPage = false;
        var assetObserved = DateTimeOffset.UtcNow.AddMinutes(-25);
        var assetExpires = DateTimeOffset.UtcNow.AddMinutes(20);
        var pagesFake = new EsiAsyncFake(async (r, ct) =>
        {
            int page = int.Parse(r.RequestUri!.Query.Split('=')[1]);
            if (page > 1)
            {
                if (Interlocked.Increment(ref pageCalls) == 3) pagesStarted.TrySetResult();
                await pageRelease.Task.WaitAsync(ct);
            }
            var response = new HttpResponseMessage(failPage && page == 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent("[{\"item_id\":" + page + ",\"type_id\":34,\"quantity\":1}]") };
            response.Headers.TryAddWithoutValidation("X-Pages", "4");
            response.Content.Headers.LastModified = assetObserved;
            response.Content.Headers.Expires = assetExpires;
            return response;
        });
        using var pagesClient = new HttpClient(pagesFake);
        var service = new EveSsoService();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var httpField = typeof(EveSsoService).GetField("_http", flags)!;
        ((HttpClient)httpField.GetValue(service)!).Dispose();
        httpField.SetValue(service, pagesClient);
        var method = typeof(EveSsoService).GetMethod("GetCharacterAssetsAsync", flags)!;
        async Task<object> Assets()
        {
            var task = (Task)method.Invoke(service, new object[] { 1L, "fake", CancellationToken.None })!;
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task)!;
        }
        var assetTask = Assets();
        try
        {
            await pagesStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(pageCalls == 3, "Asset pages after the first are fetched concurrently within three workers");
        }
        finally { pageRelease.TrySetResult(); }
        var assets = ((System.Collections.IEnumerable)await assetTask).Cast<object>().ToArray();
        Check(assets.Length == 4, "All asset pages are combined only after successful completion");
        var snapshotMethod = typeof(EveSsoService).GetMethod("GetCharacterAssetsSnapshotAsync", flags)!;
        var snapshotTask = (Task)snapshotMethod.Invoke(service, new object[] { 1L, "fake", CancellationToken.None })!;
        await snapshotTask;
        var snapshot = snapshotTask.GetType().GetProperty("Result")!.GetValue(snapshotTask)!;
        var observed = (DateTimeOffset)snapshot.GetType().GetProperty("ObservedUtc")!.GetValue(snapshot)!;
        var expires = (DateTimeOffset)snapshot.GetType().GetProperty("ExpiresAt")!.GetValue(snapshot)!;
        Check(Math.Abs((observed - assetObserved).TotalSeconds) < 1 && Math.Abs((expires - assetExpires).TotalSeconds) < 1,
            "Asset snapshot retains provider observation time and expiry instead of resetting age on reuse");
        failPage = true;
        try { await Assets(); Check(false, "A failed asset page must reject the snapshot"); }
        catch (InvalidOperationException) { Check(true, "Failed pagination cannot publish a partial inventory"); }
    }
}
