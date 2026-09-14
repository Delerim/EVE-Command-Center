# EVE Command Center v3.5.17

- Added a PI Tax & Profit tab with manual effective POCO rates saved independently for each pilot and planet. Include the NPC component after Customs Code Expertise when applicable.
- Batch estimates expand configured factory recipes to T1 inputs and include Jita input replacement cost, import/export duties, sale tax, broker fees and optional other batch costs. Immediate sale and sell-listing scenarios are supported.
- Customs duties use fixed commodity taxable values, with import duty half the export basis. Unknown rates or prices never become zero costs.
- This is a T1-fed batch planner, not a historical profit ledger. Extra upstream customs charges, hauling and setup allocation can be entered manually. POCO owner rates are not automatically imported; the ESI corporation endpoint is restricted to owning-corporation Directors.
