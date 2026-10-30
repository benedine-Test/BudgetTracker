# Budget Tracker — API (v0.1)

Backend for a personal budget tracker: Apple Pay taps, Android payment notifications and bank card alerts come in automatically, a salary credit opens a 50/30/20 budget for the pay period, and investments are tracked alongside.

**Stack:** ASP.NET Core 8 minimal API · EF Core · SQLite locally / SQL Server when deployed · xUnit.

## Run it locally

```bash
export Auth__ApiKey="$(openssl rand -base64 32)"   # required — server refuses to start without one
cd src/Budget.Api
dotnet run
# → http://localhost:5171/health
```

Every `/api/*` call needs the header `X-Api-Key: <your key>`.

Run the tests with `dotnet test` from the repo root.

## The app screen (Budget.Web)

A Blazor WebAssembly app served by the same site as the API, so **publishing `Budget.Api` deploys both**.

- **Budget** — what's left from this pay, the three buckets with progress and a daily allowance, where the money went, and earlier pay periods, plus **Add by hand** buttons for spending and income.
- **Spending** — every entry by day, search, filters, tap to categorise (and remember the shop), add by hand, delete.
- **Accounts** — every bank account and card with its balance now, the total in the bank, and what's owed on cards. Import a statement to add anything the app missed.
- **Invest** — total value, gain/loss, mix by type, holdings. Share, ETF and crypto prices and exchange rates update each time the screen opens. CPF and cash are entered as a single balance, and a salary recorded from base pay adds its CPF (yours and your employer's) to the CPF balance until you type in a new figure; an insurance plan (ILP) as what you've paid in and what it's worth. Regular payments from the bank (a monthly GIRO premium, a savings plan) are counted as they come in.
- **Subscriptions** (linked from Budget) — regular charges found in your spending, with what they cost a month and a year, when each is next due, price changes, and ones that look cancelled. See *Subscriptions* below.
- **Total assets** (linked from Accounts and Invest) — bank and cash balances plus everything under Invest in one figure, with the mix, every item, and net worth after what's owed on cards.
- **Settings** — record salary, change the split and payday, move categories between buckets, export CSV.

**Updating the live site:** in Visual Studio, right-click **Budget.Api → Publish → Publish**. Existing data and settings in Azure are kept.

**On the iPhone:** open the site in Safari → Share → **Add to Home Screen**. On first open it asks once for the secret key and remembers it on that phone only.

**Adding from the iPhone home screen.** iOS doesn't let a web app put its own widget on the home screen, so use a Shortcut, the same way Apple Pay taps come in. It saves without opening the app:
1. Shortcuts → new shortcut → **Ask for Input** (Number, "Amount") → **Ask for Input** (Text, "Shop or payee").
2. **Get Contents of URL**: `https://<your site>/api/transactions`, method **POST**, header `X-Api-Key: <your key>`, JSON body `amount` = the first input, `merchant` = the second. The category is worked out from the shop name, as with Apple Pay.
3. For income, make a second one posting to `/api/income` with `amount`, `kind` = `other` (or `refund`; `salary` sets the budget) and an optional `note`.
4. Add each to the home screen (Share → Add to Home Screen) or put them in a Shortcuts widget.

On Android, long-press the app icon for **Add spending** / **Add income**; they open the app at `/?add=spend` and `/?add=income`.

There is no offline mode: the app needs a connection, and on the free Azure plan the first load after a quiet spell can take about 20 seconds.

## How the pieces fit

| Source | Endpoint | Status |
|---|---|---|
| Apple Pay tap (iPhone Shortcut) | `POST /api/ingest/apple-pay` | ✅ ready |
| Bank card alert email (parsed elsewhere) | `POST /api/ingest/card-alert` | ✅ endpoint ready — parsers depend on your banks |
| Android notification (Google Wallet or bank app, via MacroDroid) | `POST /api/ingest/android` | 🧪 ready — wording guessed, tune on real notifications |
| Manual entry | `POST /api/transactions` | ✅ ready |
| Salary / other income | `POST /api/income` | ✅ ready |
| Statement import (PDF, CSV or Excel) | `POST /api/accounts/{id}/import[/preview]` | ✅ ready — any bank; PDF needs a real (not scanned) statement |

**De-duplication.** If the Shortcut, a bank email or an Android notification both report the same purchase (same amount + currency, within 30 min, similar merchant), the second one is merged, not doubled. Two identical taps from the *same* source are kept as two — two coffees are two coffees.

**Pay periods, not calendar months.** A budget runs from payday to the next payday (`payDay` setting, default 25th).
- Salary that lands up to 7 days early (e.g. Friday before a Sunday payday) starts the new cycle that day, and the old cycle is shortened so no day is double-counted.
- A second credit in the same cycle (claims, backpay) is added to that cycle's income.
- Each period keeps the split that was set when it opened, so changing 50/30/20 → 60/20/20 doesn't rewrite past months.

**Categorisation.** About 60 seeded rules for common Singapore merchants (FairPrice, BUS/MRT, GrabFood vs Grab, SP Services…). When you correct one with `POST /api/transactions/{id}/categorise`, it learns a rule (e.g. `HANAMI RAMEN`) and re-files other unconfirmed matches.

## Bank accounts and statement import

**Balances.** Add each account with the balance your banking app shows *right now* (cards: the amount owed). From that moment every linked entry moves it: spending takes it down, money in puts it up. A card's balance goes negative as you spend — that's what you owe.

**Which account an entry belongs to.**
- Apple Pay taps and bank alerts: give the account its **card names** as Apple Pay reports them (e.g. `DBS Visa Debit, PayLah`). Matching taps are filed there automatically, including ones already recorded.
- Added by hand / salary: pick the account in the form.
- Anything else: open it under Spending and choose the account. Accounts shows a banner while entries are unfiled.

**Importing a statement** (Accounts → an account → Import a statement):
1. Pick the **PDF statement** (the one your bank emails or lets you download), or a **CSV or Excel (.xlsx / .xls) transaction history**. No per-bank setup for any of them:
   - **PDF:** reads the text, takes lines that start with a date and end with amounts, and uses the column headings (Withdrawal / Deposit / Balance, or Amount with `CR` on credits) to tell money out from money in. Wrapped descriptions are joined; balance brought/carried forward, totals and page footers are skipped; dates without a year ("01 OCT") take the statement's year (December lines on a January statement go to the year before). Where there's a running balance, every line is checked against it and the preview warns if any don't add up. Password-protected PDFs ask for the password (used once, never stored). Scanned/photographed statements have no text and can't be read.
   - **CSV:** finds the header row and works out the date, description, money out / money in (or a signed amount, or DR/CR markers) and balance columns. Dates are read day-first. If it can't tell, it asks you to point at the columns.
   - **Excel:** read the same way as CSV, using the first sheet that has transactions (summary sheets are skipped). Real date cells are read as dates, so Excel's display format can't flip day and month; dates typed as text are read day-first. Password-protected files ask for the password. A ".xls" that is really a text file is read as text; one that is really a web page has to be saved as .xlsx or CSV first.
   CSV or Excel is more reliable than PDF where your bank offers it.
2. It shows each line as **already in the app** or **missing**. A purchase matches an existing entry with the same amount up to 5 days earlier (banks post card taps 1–3 days late); each entry is used once, so two identical coffees need two entries. Entries already filed under a *different* account are never matched.
3. Untick anything you don't want, tick **This is my salary** on a pay credit to open the budget period, and add.
4. If the file has a balance column, it compares the bank's closing balance with the app's and offers to reset to the bank's figure — the bank is the truth.
5. **Wrong, or just a test?** The account lists its recent imports with **Undo**: the import's entries are removed (a salary line's budget too), entries it filed are unfiled, and the balance goes back to what it was — unless you've set the balance by hand since, which is kept. Only the latest import of an account can be undone (undo newer ones first). Then fix things and import again. Imports made before this feature existed can't be undone.

**Paying for friends.** Paid S$100 for dinner and your friend owes you half? Open the entry under Spending → *I paid for others too*, enter your share (S$50) and, optionally, who owes the rest.
- Only your share counts toward the budget, in the pay period you paid, however late they pay you back. The entry keeps its full S$100, so the account balance and statement matching stay right.
- The rest shows as owed: on the entry, under the **Shared** filter, and as *Friends owe you* on Accounts.
- When the money comes in (a PayNow, from a statement import or added by hand), open that entry → *Is this someone paying you back?* and pick the bill; bills owed exactly that amount are listed first. It's filed as **Paid Back**, which never counts as income or spend. Paid in cash? Use *They paid me back* on the bill instead. Several part-payments are fine; more than is owed is refused.
- They won't pay? *They won't pay* turns what's still owed into your spending, in the period you paid the bill.
- Deleting a bill (or undoing the import that added it) keeps its repayments as Other Income.

**Fixing an entry.** Tap any entry under Spending → *Fix this entry* to change its amount, description or date. A salary's amount or date can't be edited there (it set the period's budget) — delete it and record it again.

What it does for you: card bill payments are filed as **Transfer** so they don't count as spending twice — recognised by their wording ("PAYMENT - THANK YOU", "BILL PAYMENT … CARD/DBSC"), and, when both the bank account and the card are in the app, by pairing money out of the bank with the same amount arriving on the card within 5 days, whatever the bank calls it and whichever statement you import first (undo puts the other side back). A card purchase never pairs with money coming into a bank account; unknown money in is filed as Other Income; a statement `BUS/MRT` charge replaces the S$0 pending taps; a pay credit near a salary you already recorded is flagged ("same one?") instead of doubling your income. Importing the same file twice adds nothing.

**Card bills paid from the bank show on the card straight away.** The card's own statement only lists your payment weeks later, so until then the card would still look owed in full. When a bank statement has a card bill payment (e.g. DBS iBanking's `BILL DBSC-4119… : I-BANK`) and the card's side isn't in the app yet, the import also adds it to the card as **Bill paid from <bank account>**, so what's owed drops that day. Which card: the card number's last four digits if a card lists them in its name or card names (`yuu 3939`), else the bank (`DBSC` → your one DBS card), else your only card. When the card's statement comes in, its `PAYMENT - THANK YOU` line matches that entry instead of being added twice; undoing the bank import takes it off the card. For a payment already in the app, or one it couldn't place, open it under Spending (filed as Transfer) → *Paid a card bill?* → pick the card → **Log it on the card**. A plain transfer (`ICT UOB:… I-BANK Transfer`) is never taken for a card bill on its own — log it by hand if it was one.

Limits: scanned PDFs can't be read. A consolidated statement covering several accounts lists all of their lines — untick the ones that belong elsewhere. Up to 4 MB per PDF or Excel file, 2 MB per CSV. Balances in a currency other than SGD aren't added to the total.

**Existing databases** are upgraded on startup (the `Accounts`, `ImportBatches`, `PolicyTerms`, `PolicyValuations` and `DismissedRecurring` tables, `Transactions.AccountId` / `ImportBatchId` / `MyShare` / `SharedWith` / `RepaysId` and the Paid Back category are added if missing), so publishing over the live site keeps your data.

## Subscriptions

Nothing to set up: the app looks through the last 25 months of spending for the same shop charging about the same amount every month, quarter or year.

- **What counts.** Same merchant (matched the way category rules are, so `NETFLIX.COM 866-579-7172` and `NETFLIX.COM` are one), amounts within 15% (or S$1) of each other, and gaps of 25–35 days (monthly), 80–100 (quarterly) or 350–380 (yearly). One missed or unimported charge is fine. It needs **three charges**, or **two** if they're filed under Subscriptions — so a yearly plan shows once its category is Subscriptions. Transfers, income and bus/MRT are left out; weekly patterns aren't looked for (they're mostly groceries and coffee). On a shared bill only your share counts.
- **One shop, several plans.** Charges at clearly different amounts are kept apart (Apple: iCloud at S$1.28 and Music at S$10.98), while a price rise stays one subscription and is flagged *Up from S$10.98*.
- **Next due** is the last charge plus one cycle, on the usual day (a plan charged on the 31st is due on the 31st again after February).
- **Late / looks cancelled.** Past due by more than 5 days (monthly; 10 quarterly, 20 yearly) it shows *Not charged yet*. With no charge 35 days past due (60 for quarterly and yearly) it moves to *Looks cancelled* and drops out of the totals; it disappears altogether after a few months.
- **Totals** are the running ones in your base currency: per month (a yearly plan ÷ 12), per year, and due in the next 30 days.
- **Not a subscription.** Tap one → *Not a subscription* to hide it. It's remembered by merchant and amount, so another plan from the same shop can still show. *Show* at the bottom of the screen brings hidden ones back.

## iPhone Shortcut (Apple Pay auto-capture)

Needs the API deployed on **HTTPS** first (see below).

1. **Shortcuts → Automation → New Automation → Transaction.** Pick your cards, choose **When I tap**, set **Run Immediately**.
2. Add **Get Contents of URL**:
   - URL: `https://<your-host>/api/ingest/apple-pay`
   - Method: `POST`
   - Headers: `X-Api-Key` = your key
   - Request Body: **JSON** with these keys (tap each value and pick from *Shortcut Input*):
     - `amount` → Amount
     - `merchant` → Merchant
     - `card` → Card or Pass
     - `date` → Current Date, formatted **ISO 8601**
3. Add **Get Dictionary Value** → key `message` from *Contents of URL*.
4. Add **Show Notification** → the dictionary value.

You'll get a line like: *S$7.80 at Starbucks → Dining & Food Delivery. Wants left: S$1,492.20 (25d, ~S$59.69/day).*

Things to check in your first week:
- The input field names above are from memory of the iOS 17/18 Transaction trigger. If yours differ, pick the closest ones. Amount is parsed leniently ("S$12.50", "12.50", "SGD 12,50" all work).
- Whether the automation fires for **online / in-app** Apple Pay, not just in-store taps. Anything it misses gets caught by bank emails.
- **Bus / MRT:** a transit tap has no fare yet (SimplyGo charges it later, usually as one `BUS/MRT` charge per day). A tap with a S$0 or blank amount is saved as *Fare pending* and doesn't count toward the budget. When the real `BUS/MRT` charge arrives (bank alert or added by hand), the pending trips from the 3 days before it are removed, so the charge is the only entry that costs money.
- **Offline:** if the request fails, nothing is saved. Add an `If` on the result and fall back to **Append to Note** so you can enter it later.

## Android phone (notification capture)

Android has no "when I tap" automation like the iPhone, so the phone forwards payment **notifications** and the server reads them. The phone sends the raw text; all the parsing is on the server, so when a bank changes its wording only the server needs fixing.

1. Install **MacroDroid**. Give it notification access, and in Android settings turn **battery optimisation off** for it (Samsung/Xiaomi otherwise stop it in the background, and taps are missed without a word).
2. **Trigger:** Notification → *Notification Received* → pick the apps: **Google Wallet**, and your bank apps (**UOB TMRW**, **DBS digibank**…). Text: *Any*.
3. **Action:** Connectivity → *HTTP Request*:
   - Method `POST`, URL `https://<your-host>/api/ingest/android`
   - Header `X-Api-Key` = your key
   - Body, content type `application/json`:
     ```json
     {"app": "[not_app_name]", "title": "[not_title]", "text": "[notification]"}
     ```
   - Save the response to a string variable (e.g. `reply`).
4. Optional while testing: **Action** → *Display Notification* with `{v=reply}`.

`date` can be left out: the notification's arrival time is used. Notifications that aren't a purchase (promos, OTPs, declined, refunds) come back as `ignored: true` and save nothing.

Things to check in your first week:
- Send one real notification from each app through. If one is saved as *Unknown merchant* or ignored when it shouldn't be, the parser (`NotificationParser.cs`) needs that bank's wording added.
- **Bank app alerts cover every use of the card**, including Apple Pay on the iPhone and the plastic card. Those are merged with the iPhone tap when the amount matches within 30 min and the shop looks the same, so they aren't counted twice, but check the first few.
- Taps are filed under an account when its **card names** include the card's last four digits (e.g. `UOB One, 1234`). The card is saved as the app name plus those digits, e.g. `UOB TMRW 1234`.

## API reference

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | Liveness (no key) |
| POST | `/api/ingest/apple-pay` | `{amount, merchant, card, date}` all strings → `{message, duplicate, transaction}` |
| POST | `/api/ingest/card-alert` | Same shape, for parsed bank emails |
| POST | `/api/ingest/android` | `{app, title, text, date?}` raw notification → `{message, ignored, duplicate, transaction?}` |
| POST | `/api/income` | `{amount, date, note, kind: "salary"\|"other"\|"refund", cpf?}` — salary opens/tops up the period; `cpf` is added to the CPF holdings |
| GET/PUT | `/api/profile` | `{birthDate: "yyyy-MM-dd" \| null}` — sets the CPF age band for salaries |
| GET | `/api/budget/summary?date=yyyy-MM-dd` | Buckets (budget / spent / remaining / % / status / daily allowance), top categories, uncategorised |
| GET | `/api/budget/history` | Budget vs actual for each recorded pay period |
| GET | `/api/budget/periods` | All pay periods |
| GET/PUT | `/api/settings` | `payDay`, `needsPct`, `wantsPct`, `savingsPct` (must total 100) |
| GET/POST/PUT | `/api/transactions[/{id}]` | Filters: `from`, `to`, `categoryId`, `bucket`, `uncategorised`, `q`, `take`, `accountId` (0 = unfiled), `shared`. PUT `{amount?, merchant?, date?, notes?, categoryId?, accountId?}` — `accountId` 0 unfiles; salary amount/date are refused |
| GET/POST | `/api/transactions/{id}/card-payment` | On money out of a bank account: GET → `{onCard, suggestedAccountId}`; POST `{accountId}` logs it as paid onto that card (both sides filed as Transfer; refused if the card already has it) |
| PUT | `/api/transactions/{id}/share` | `{myShare, sharedWith?}` — only your share counts toward the budget; `myShare: null` makes it all yours again (refused once anything's repaid) |
| POST | `/api/transactions/{id}/repayments` | `{repaymentId}` links money already in the app, or `{amount, date?, accountId?, note?}` records a new repayment |
| DELETE | `/api/transactions/{id}/repays` | On a repayment: unlink it (back to Other Income) |
| POST | `/api/transactions/{id}/write-off` | What's still owed becomes your share |
| GET | `/api/owed?amount=` | Bills with money owed back, total; those matching `amount` first |
| POST | `/api/transactions/{id}/categorise` | `{categoryId, learnRule=true, pattern?, applyToSimilar=true}` |
| DELETE | `/api/transactions/{id}` | |
| GET/POST/PUT | `/api/categories[/{id}]` | Move a category between Needs / Wants / Savings / Income / Transfer |
| GET/POST/DELETE | `/api/rules[/{id}]` | Merchant pattern → category |
| GET | `/api/holdings` | Portfolio: cost, value, P/L, allocation, stale-price flags |
| POST/PUT/DELETE | `/api/holdings[/{id}]` | `{symbol, name, assetClass, platform, units, averageCost, currency, lastPrice, fxToBase, autoPrice?, contributionMatch?, contributionAmount?, contributionAccountId?}` |
| PUT | `/api/holdings/{id}/price` | `{price, fxToBase?}` |
| POST | `/api/holdings/refresh?force=` | Fetch due prices (`autoPrice` holdings) and exchange rates from Yahoo Finance |
| GET | `/api/policy-plans` | Standard terms of the plans the app knows |
| GET | `/api/policies` | All ILPs together: monthly premium, premiums paid, policy and surrender value |
| GET/PUT | `/api/holdings/{id}/policy` | ILP terms: `{commencementDate, monthlyPremium, initialPeriodMonths, minimumInvestmentYears, premiumTermYears?, schedule?, bankText?}` → statements with surrender value, bank premium check |
| POST/DELETE | `/api/holdings/{id}/policy/valuations[/{valuationId}]` | `{asOf, initialUnits, accumulationUnits}` from a statement |
| GET | `/api/holdings/{id}/policy/projection?rate=4&rate=8&from=start` | Account and surrender value per policy year (`from=latest` starts at the newest statement) |
| GET | `/api/subscriptions` | Detected subscriptions: `{monthlyTotal, yearlyTotal, dueNext30Days, currency, items: [{pattern, name, cadence, amount, previousAmount, monthlyCost, lastCharged, nextDue, daysToNext, status: active\|late\|cancelled, …}], dismissed}` |
| POST | `/api/subscriptions/dismiss` | `{pattern, amount}` — not a subscription; returns the updated list |
| GET/DELETE | `/api/subscriptions/dismissed[/{id}]` | Hidden ones; delete brings one back |
| GET | `/api/export/transactions.csv` | Everything, spreadsheet-safe |
| GET | `/api/accounts?archived=true` | Balances: in accounts, owed on cards, net, unfiled entry count, owed to you by friends |
| POST/PUT/DELETE | `/api/accounts[/{id}]` | `{name, kind: Bank\|CreditCard\|Cash, balance, asOf?, cardNames}` — delete keeps entries, unfiled |
| PUT | `/api/accounts/{id}/balance` | `{balance, asOf?}` — "the bank shows this now" |
| POST | `/api/accounts/{id}/import/preview` | `{csv, positiveIsSpend?, columns?}`, `{excel: base64, password?, positiveIsSpend?, columns?}` or `{pdf: base64, password?}` → each line matched or missing, bank vs app balance, warnings. Saves nothing |
| POST | `/api/accounts/{id}/import` | `{add: [{date, description, amount, isCredit, isSalary}], link: [ids], statementBalance?, statementBalanceDate?, fileName?}` → includes `batchId` |
| GET | `/api/accounts/{id}/imports` | Recent imports, newest first, with `canUndo` |
| DELETE | `/api/accounts/{id}/imports/{batchId}` | Undo an import (latest only; 409 otherwise) |

**Budget buckets.** Savings-bucket categories (Investments, Emergency Fund, Savings Goals) count as *contributions*, so you can see progress toward the 20%. Transfer and Income categories never count as spend.

**Investments.** Enter CPF OA/SA/MA as `assetClass: "Cpf"` with `units: 1` and `averageCost` = balance. Foreign holdings take `fxToBase` (e.g. USD→SGD 1.30).

- **Automatic prices.** With `autoPrice: true` the symbol is looked up on Yahoo Finance (ES3.SI, VWRA.L, AAPL, BTC-USD), and every foreign holding's exchange rate is refreshed too. Yahoo's endpoint is unofficial: if it fails, the last price stays and the reason shows on the holding. Prices fetched in the last 15 minutes are skipped unless forced.
- **Insurance plans (ILPs).** `assetClass: "Policy"`, `units: 1`, `averageCost` = total paid in, `lastPrice` = the value the insurer shows. There is no public price, so the value is updated by hand.
- **ILP with contract terms** (Invest → the plan → *Policy details*). For a regular-premium ILP with an Initial Units Account (IUA) and an Accumulation Units Account (AUA). Standard terms are included for **HSBC Life Wealth Accelerate** and **FWD Invest First Summit**; both run through the same calculation, with the differences held as per-policy settings:
  - **Per-policy settings.** Initial contribution period, minimum investment period (0 = none) and premium term (no premiums after it). Each fee or bonus runs to a year or to the end of a named period, a fee can have a yearly cap (FWD's accumulation charge: 1.5%, at most S$756 a year), and a bonus is paid monthly or at policy-year end, on the total or the accumulation account, optionally scaled by premiums paid in the year (FWD's Loyalty Bonus). The premium bonus (HSBC start-up, FWD booster) always goes to the initial account and isn't paid on a missed premium. FWD's premium shortfall charge applies in projections with missed months; the premium reduction charge, withdrawal and top-up terms are stored only.
  - **Combined view** on Invest: monthly premiums, premiums paid, policy value and surrender value across plans, each from its latest statement.
  - **Terms are data.** Start date, monthly premium, initial contribution period and minimum investment period, plus the fee, start-up bonus, account-value bonus, surrender-charge (EEC) and fund tables, stored per policy and editable on the policy screen. New policies start from the HSBC Life Wealth Accelerate terms; correct anything that differs from your contract.
  - **Values only from statements.** Enter the IUA and AUA values with the statement date; nothing is estimated from fund prices. For each statement: premiums paid to that date, account value, surrender value (account value − the EEC for that date's policy year × IUA) and the gap to premiums paid. Policy years run anniversary to anniversary (started 8 Mar 2024 → year 3 from 8 Mar 2026).
  - **In the portfolio**, cost is premiums due so far and value is the latest statement plus premiums due since, at cost.
  - **In the budget**, the bank text (e.g. `HSBC LIFE`) becomes a rule filing the premium under Investments (Savings bucket); unconfirmed entries already in the app are re-filed. The screen flags months with no bank debit within 15 days of the due date, from the first debit in the app onwards.
  - **Projection** per policy year to the year after the minimum investment period, at two gross returns (default 4% and 8%), from day one or from the latest statement. Each month: premium in (to the IUA with start-up bonus during the initial period, then to the AUA), growth at the gross return less fund charges, IUA/AUA fees off, then account-value bonus into the AUA. It's an illustration, not HSBC's own method; their benefit illustration is the check.
- **CPF from salary.** A salary recorded with `cpf` (the salary form fills it in when it works pay out from base pay: employee + employer share, 2026 rates, age band from the date of birth on the Profile page when set) adds that amount to the `Cpf` holdings dated on payday. With several (OA/SA/MA) it is shared in proportion to their balances, which is only an estimate; the real split depends on age. Entering a new balance takes in everything before it, and deleting the salary takes its CPF back out. Interest is not added.
- **Regular contributions.** `contributionMatch` is text the bank entry contains (e.g. `FWD`), optionally narrowed by `contributionAmount` and `contributionAccountId`. Matching spends after the holding's units/cost were last entered are added on top, at cost, until the figures are updated. A typed-in balance (CPF, an ILP's value) takes in every payment made before it. The bank entry has to be in the app first, usually from a statement import.

## Deploying

- **Simplest:** Azure App Service (Linux, B1) + Azure SQL.
  - `Database__Provider=SqlServer`
  - `ConnectionStrings__Budget=...`
  - `Auth__ApiKey=...`
- **Cheaper:** a small container host (Fly.io, Railway) keeping SQLite on a persistent volume.
- Either way: HTTPS only, and keep the key out of source control.

**Schema changes on a live database** go in `Data/SchemaUpgrade.cs` (checked, idempotent SQL for SQLite and SQL Server), since `EnsureCreated` never alters an existing database. Moving to EF migrations is still the long-term fix, but needs a baseline for databases created by `EnsureCreated` and separate migrations per provider.

## Next steps

1. Bank email parsers — depends on which banks and cards are used.
2. Automatic price and FX refresh.
