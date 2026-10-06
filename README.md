# Budget Tracker — API (v0.1)

Backend for a personal budget tracker: Apple Pay taps and bank card alerts come in automatically, a salary credit opens a 50/30/20 budget for the pay period, and investments are tracked alongside.

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

- **Budget** — what's left from this pay, the three buckets with progress and a daily allowance, where the money went, and earlier pay periods.
- **Spending** — every entry by day, search, filters, tap to categorise (and remember the shop), add by hand, delete.
- **Accounts** — every bank account and card with its balance now, the total in the bank, and what's owed on cards. Import a statement to add anything the app missed.
- **Invest** — total value, gain/loss, mix by type, holdings. Share, ETF and crypto prices and exchange rates update each time the screen opens. CPF and cash are entered as a single balance, and a salary recorded from base pay adds its CPF (yours and your employer's) to the CPF balance until you type in a new figure; an insurance plan (ILP) as what you've paid in and what it's worth. Regular payments from the bank (a monthly GIRO premium, a savings plan) are counted as they come in.
- **Settings** — record salary, change the split and payday, move categories between buckets, export CSV.

**Updating the live site:** in Visual Studio, right-click **Budget.Api → Publish → Publish**. Existing data and settings in Azure are kept.

**On the iPhone:** open the site in Safari → Share → **Add to Home Screen**. On first open it asks once for the secret key and remembers it on that phone only.

There is no offline mode: the app needs a connection, and on the free Azure plan the first load after a quiet spell can take about 20 seconds.

## How the pieces fit

| Source | Endpoint | Status |
|---|---|---|
| Apple Pay tap (iPhone Shortcut) | `POST /api/ingest/apple-pay` | ✅ ready |
| Bank card alert email (parsed elsewhere) | `POST /api/ingest/card-alert` | ✅ endpoint ready — parsers depend on your banks |
| Manual entry | `POST /api/transactions` | ✅ ready |
| Salary / other income | `POST /api/income` | ✅ ready |
| Statement import (PDF, CSV or Excel) | `POST /api/accounts/{id}/import[/preview]` | ✅ ready — any bank; PDF needs a real (not scanned) statement |

**De-duplication.** If the Shortcut and a bank email both report the same purchase (same amount + currency, within 30 min, similar merchant), the second one is merged, not doubled. Two identical taps from the *same* source are kept as two — two coffees are two coffees.

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

**Fixing an entry.** Tap any entry under Spending → *Fix this entry* to change its amount, description or date. A salary's amount or date can't be edited there (it set the period's budget) — delete it and record it again.

What it does for you: card bill payments are filed as **Transfer** so they don't count as spending twice — recognised by their wording ("PAYMENT - THANK YOU", "BILL PAYMENT … CARD/DBSC"), and, when both the bank account and the card are in the app, by pairing money out of the bank with the same amount arriving on the card within 5 days, whatever the bank calls it and whichever statement you import first (undo puts the other side back). A card purchase never pairs with money coming into a bank account; unknown money in is filed as Other Income; a statement `BUS/MRT` charge replaces the S$0 pending taps; a pay credit near a salary you already recorded is flagged ("same one?") instead of doubling your income. Importing the same file twice adds nothing.

Limits: scanned PDFs can't be read. A consolidated statement covering several accounts lists all of their lines — untick the ones that belong elsewhere. Up to 4 MB per PDF or Excel file, 2 MB per CSV. Balances in a currency other than SGD aren't added to the total.

**Existing databases** are upgraded on startup (the `Accounts` and `ImportBatches` tables and `Transactions.AccountId` / `ImportBatchId` are added if missing), so publishing over the live site keeps your data.

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

## API reference

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | Liveness (no key) |
| POST | `/api/ingest/apple-pay` | `{amount, merchant, card, date}` all strings → `{message, duplicate, transaction}` |
| POST | `/api/ingest/card-alert` | Same shape, for parsed bank emails |
| POST | `/api/income` | `{amount, date, note, kind: "salary"\|"other"\|"refund", cpf?}` — salary opens/tops up the period; `cpf` is added to the CPF holdings |
| GET/PUT | `/api/profile` | `{birthDate: "yyyy-MM-dd" \| null}` — sets the CPF age band for salaries |
| GET | `/api/budget/summary?date=yyyy-MM-dd` | Buckets (budget / spent / remaining / % / status / daily allowance), top categories, uncategorised |
| GET | `/api/budget/history` | Budget vs actual for each recorded pay period |
| GET | `/api/budget/periods` | All pay periods |
| GET/PUT | `/api/settings` | `payDay`, `needsPct`, `wantsPct`, `savingsPct` (must total 100) |
| GET/POST/PUT | `/api/transactions[/{id}]` | Filters: `from`, `to`, `categoryId`, `bucket`, `uncategorised`, `q`, `take`, `accountId` (0 = unfiled). PUT `{amount?, merchant?, date?, notes?, categoryId?, accountId?}` — `accountId` 0 unfiles; salary amount/date are refused |
| POST | `/api/transactions/{id}/categorise` | `{categoryId, learnRule=true, pattern?, applyToSimilar=true}` |
| DELETE | `/api/transactions/{id}` | |
| GET/POST/PUT | `/api/categories[/{id}]` | Move a category between Needs / Wants / Savings / Income / Transfer |
| GET/POST/DELETE | `/api/rules[/{id}]` | Merchant pattern → category |
| GET | `/api/holdings` | Portfolio: cost, value, P/L, allocation, stale-price flags |
| POST/PUT/DELETE | `/api/holdings[/{id}]` | `{symbol, name, assetClass, platform, units, averageCost, currency, lastPrice, fxToBase, autoPrice?, contributionMatch?, contributionAmount?, contributionAccountId?}` |
| PUT | `/api/holdings/{id}/price` | `{price, fxToBase?}` |
| POST | `/api/holdings/refresh?force=` | Fetch due prices (`autoPrice` holdings) and exchange rates from Yahoo Finance |
| GET | `/api/export/transactions.csv` | Everything, spreadsheet-safe |
| GET | `/api/accounts?archived=true` | Balances: in accounts, owed on cards, net, unfiled entry count |
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
2. Recurring bill / subscription detection.
3. Automatic price and FX refresh.
