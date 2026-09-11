# Corporation setup

Use Settings on the overview to link a personal pilot and optional corporation readers. One character can fill both roles if ESI grants both capabilities. Different corporations normally require different characters.

Moon access probes `/corporation/{corporation_id}/mining/extractions/` and `/corporations/{corporation_id}/structures/`. Contract access probes `/corporations/{corporation_id}/contracts/`. The corporation comes from the chosen character's ESI record. Empty successful lists grant access; denied or unsuccessful requests do not. Officer, director and CEO labels do not substitute for a successful read.

Capabilities start unverified on every launch and are rechecked every ten minutes. Temporary ESI failures hide affected views until verification succeeds. Settings shows each result. Personal tools remain available without corporation permissions. Mining ledger access is checked separately and missing ledger permission produces a warning.

The [official ESI schema](https://esi.evetech.net/meta/openapi.json) defines `natural_decay_time` as the automatic fracture deadline. Command Center adds the configured field lifetime to estimate despawn. A recent extraction restart can infer a previous field when its history was not observed. This uses the new extraction's duration to estimate the previous pull and is labelled as inferred. It is not proof that rocks remain in space.

The overview and calendar share pull records. New extractions do not replace previous active fields. Remaining R4 ore uses saved composition, estimated pull volume, configured waste and recorded mining; ESI does not expose a live rock scan.
