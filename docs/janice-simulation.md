# Corp payout comparison

Open Mining Command Center > Janice simulation, paste the appraisal link and choose LOAD APPRAISAL. This fetches the saved public appraisal without modifying it.

The default scenario is your corp paying 90% Jita buy. It compares that payout against 100% Jita sell with a maximum acceptable discount of 10%. Change the reference to Jita buy for the immediate-sale alternative, or split for the midpoint. Payout basis and percentage can also be changed, including 90% split. The summary always shows all three 100% values and all three 90% scenarios.

The comparison is not accounting profit. The gap measures the value given up for the corp route. Jita sell is a quoted listing value, not guaranteed executable revenue for the whole basket. Enter direct-sale fees and hauling to compare net alternatives. Corp route costs are separate. Zero excludes that cost. Costs are shared across lines in proportion to their 100% reference value.

Every ore line must stay within the configured discount limit. A good basket total cannot hide a bad individual line. The required payout percentage is the largest requirement among the lines, rounded up in the summary. A requirement above 100% means the corp's selected price basis would need a premium to meet the requested limit.

## Supplied examples

The saved 90% appraisal [4S2IIm](https://janice.e-351.com/a/4S2IIm) has a corp buy payout of 6,884,967,906.72 ISK. Its displayed 7,757,138,511.00 ISK sell value is also already at 90%. Unscaled values are 7,649,964,340.80 ISK buy, 8,134,503,565.40 ISK split and 8,619,042,790.00 ISK sell. The corp payout is 764,996,434.08 ISK below 100% buy (10%) and 1,734,074,883.28 ISK below gross 100% sell (20.12%). The buy/sell quote spread itself is 11.24%, before applying the corp's additional discount.

The [2coDfZ](https://janice.e-351.com/a/2coDfZ) snapshot has a 9.28% buy/sell spread. Its 90% buy payout is 4,770,752,755.68 ISK, while 100% sell is 5,843,009,260.10 ISK, an 18.35% gross opportunity gap. The 90% split scenario is 5,014,730,544.885 ISK. A 90% quote must not be used as the 100% comparison or discounted a second time.

Loading an old link does not refresh its prices. The screen shows the saved dataset date and warns about older data; it has no order-depth execution guarantee. Create a fresh appraisal in Janice before trading. Missing prices or unpriced input block the comparison.

Historical HIGH/LOW/MID labels elsewhere describe price position against regional history; they are not sell recommendations.

Validation: build Tests/CommandCenter.Checks.csproj, then run the checks executable with `--audit-janice` (optional PNG path) or `--audit-janice-live` for the two public appraisal requests. The normal full regression suite includes offline comparison and UI checks.
