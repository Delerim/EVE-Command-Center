# EVE Command Center v3.6.9

- Fixed a long-session Mining hot path: current-day continuity and break statistics are now maintained incrementally instead of rescanning and sorting every mining pull for every pilot on every one-second dashboard refresh. The current-day JSONL still restores the same totals after restart.
- Cached Mining pages now stop their one-second/three-second dashboard work while hidden and prevent overlapping refreshes; the visible dashboard reuses each pilot's activity summary instead of querying it twice.
- Reduced preview/compositor churn by removing per-layout live-preview updates, pausing and unregistering native live-preview surfaces while an EVE client is hung, and coalescing process-stat UI callbacks so a busy dispatcher cannot accumulate stale work.
- Made optional CPU-affinity management less aggressive: background clients use BelowNormal rather than Idle priority, automatic balancing gives them a pair of logical processors instead of one, topology is no longer guessed from CPU numbering, and unchanged affinity is rechecked on a five-second cadence instead of four times per second.
