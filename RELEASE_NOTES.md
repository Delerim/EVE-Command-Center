# EVE Command Center v3.6.48

- Market refresh now shows its current request or name-lookup progress. Switching tabs or searching keeps that progress visible instead of restoring an old saved error.
- Added Cancel refresh. Cancelling restores all refresh buttons and retains previously saved market data.
- Optional item and station lookups have a 15-second cancellation budget per pilot, so slow metadata does not indefinitely delay a successful market import. Names already resolved are retained.
- Old saved network exceptions are labelled as previous failures. Refresh orders to replace the previous open-order result; history and transactions retain their separate refresh status.
- Added regression checks for optional lookup cancellation, view changes during refresh, restored controls and preserved orders.
