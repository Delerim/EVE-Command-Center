# EVE Command Center v3.6.49

- Replaced the market view/side dropdown combination with dedicated Open buy orders, Buy history, Open sell orders, Sell history, Purchases and Sales tabs. Unconfirmed orders remain separate so missing observations are never presented as confirmed fills.
- Replaced character checkboxes with portrait cards, clear selection and hover feedback, Select all / Clear actions, immediate saved selection and a separate single-pilot view. Filtering never deletes imported evidence.
- Each tab has one relevant refresh action. Progress and safe cancellation remain visible while switching views. Search, aligned sortable quantities and explicit empty states apply throughout.
- Started Phase D with Wallet activity: persistent journal imports, money in / out / net cash movement, readable activity names, pilot and period filters, and related purchases or sales linked by EVE's exact journal reference.
- Wallet amounts are counted once. Trade totals and optional tax metadata are not added again. Transfers and other wallet activity are included; cash flow is not project profit, and fees are not automatically attributed to projects.
- Ledger schema upgrade creates a pre-upgrade backup and preserves existing projects, history and transactions. Failed pages, changed pagination and conflicting evidence preserve previous data. Repeat imports are idempotent.
- Validated with 593 regression checks and an isolated live journal import. Manual project allocation, acquisition valuation and project P&L remain the next Phase D steps.
