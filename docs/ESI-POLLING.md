# ESI polling audit ? v3.6.89

The previous process-wide scheduler held its semaphore across HTTP and body reads. A slow request therefore delayed unrelated requests and even cached responses. Background pilot snapshots also refreshed pilots sequentially, each with a three-minute budget.

The scheduler now limits network work to four concurrent requests with a 250 ms dispatch floor. Metadata locks are short; cache reads bypass network slots. Identical concurrent GETs share a flight and reuse cacheable responses. Flight entries are removed once their last caller exits. Cached bodies are bounded to 64 MB and 1,024 entries. Cancellation releases workers and flights.

Advertised rate groups are paced per pilot, with a conservative budget. Known group 429 responses delay that pilot/group; unknown limits, server overload and low global error budgets retain shared cooldowns. Retry-After and server cache expiry remain authoritative. First requests to unknown routes still need response headers to discover their group.

Background pilot refresh uses three workers and keeps successful snapshots through failures. Token refresh is shared and serialized per pilot. Asset pagination fetches the first page, then at most three further pages concurrently; failed pages or changing page counts reject the entire new inventory. Existing name lookups already batch up to 500 IDs.

This removes local serialization; it does not promise a fixed refresh duration or real-time inventory. No live twenty-minute workload benchmark was performed. Server-side asset caches, endpoint-specific refresh intervals, network failures and provider cooldowns can still delay new data. Financial service orchestration remains unchanged because its own busy guards require separate coordination before parallelization.

Validation uses fake HTTP handlers for concurrency limits, cached reads under load, duplicate suppression, auth isolation, group/global cooldowns, cancellation, and complete asset pagination. No live pilot credentials are needed.

Official references:
- https://developers.eveonline.com/docs/services/esi/rate-limiting/
- https://developers.eveonline.com/docs/services/esi/best-practices/
- https://developers.eveonline.com/docs/services/esi/pagination/x-pages/
