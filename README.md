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
- **Invest** — total value, gain/loss, mix by type, holdings. CPF and cash are entered as a single balance.
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
| Statement CSV import | — | ⏳ next, bank-specific |

**De-duplication.** If the Shortcut and a bank email both report the same purchase (same amount + currency, within 30 min, similar merchant), the second one is merged, not doubled. Two identical taps from the *same* source are kept as two — two coffees are two coffees.

**Pay periods, not calendar months.** A budget runs from payday to the next payday (`payDay` setting, default 25th).
- Salary that lands up to 7 days early (e.g. Friday before a Sunday payday) starts the new cycle that day, and the old cycle is shortened so no day is double-counted.
- A second credit in the same cycle (claims, backpay) is added to that cycle's income.
- Each period keeps the split that was set when it opened, so changing 50/30/20 → 60/20/20 doesn't rewrite past months.

**Categorisation.** About 60 seeded rules for common Singapore merchants (FairPrice, BUS/MRT, GrabFood vs Grab, SP Services…). When you correct one with `POST /api/transactions/{id}/categorise`, it learns a rule (e.g. `HANAMI RAMEN`) and re-files other unconfirmed matches.

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
- **Offline:** if the request fails, nothing is saved. Add an `If` on the result and fall back to **Append to Note** so you can enter it later.

## API reference

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | Liveness (no key) |
| POST | `/api/ingest/apple-pay` | `{amount, merchant, card, date}` all strings → `{message, duplicate, transaction}` |
| POST | `/api/ingest/card-alert` | Same shape, for parsed bank emails |
| POST | `/api/income` | `{amount, date, note, kind: "salary"\|"other"\|"refund"}` — salary opens/tops up the period |
| GET | `/api/budget/summary?date=yyyy-MM-dd` | Buckets (budget / spent / remaining / % / status / daily allowance), top categories, uncategorised |
| GET | `/api/budget/history` | Budget vs actual for each recorded pay period |
| GET | `/api/budget/periods` | All pay periods |
| GET/PUT | `/api/settings` | `payDay`, `needsPct`, `wantsPct`, `savingsPct` (must total 100) |
| GET/POST/PUT | `/api/transactions[/{id}]` | Filters: `from`, `to`, `categoryId`, `bucket`, `uncategorised`, `q`, `take` |
| POST | `/api/transactions/{id}/categorise` | `{categoryId, learnRule=true, pattern?, applyToSimilar=true}` |
| DELETE | `/api/transactions/{id}` | |
| GET/POST/PUT | `/api/categories[/{id}]` | Move a category between Needs / Wants / Savings / Income / Transfer |
| GET/POST/DELETE | `/api/rules[/{id}]` | Merchant pattern → category |
| GET | `/api/holdings` | Portfolio: cost, value, P/L, allocation, stale-price flags |
| POST/PUT/DELETE | `/api/holdings[/{id}]` | `{symbol, name, assetClass, platform, units, averageCost, currency, lastPrice, fxToBase}` |
| PUT | `/api/holdings/{id}/price` | `{price, fxToBase?}` |
| GET | `/api/export/transactions.csv` | Everything, spreadsheet-safe |

**Budget buckets.** Savings-bucket categories (Investments, Emergency Fund, Savings Goals) count as *contributions*, so you can see progress toward the 20%. Transfer and Income categories never count as spend.

**Investments.** Enter CPF OA/SA/MA as `assetClass: "Cpf"` with `units: 1` and `averageCost` = balance. Foreign holdings take `fxToBase` (e.g. USD→SGD 1.30). Prices are manual for now.

## Deploying

- **Simplest:** Azure App Service (Linux, B1) + Azure SQL.
  - `Database__Provider=SqlServer`
  - `ConnectionStrings__Budget=...`
  - `Auth__ApiKey=...`
- **Cheaper:** a small container host (Fly.io, Railway) keeping SQLite on a persistent volume.
- Either way: HTTPS only, and keep the key out of source control.

**Before the schema changes on a live database,** switch `EnsureCreated` to EF migrations (`dotnet ef migrations add Initial`).

## Next steps

1. Bank email parsers — depends on which banks and cards are used.
2. Statement CSV import for reconciliation.
3. Recurring bill / subscription detection.
4. Automatic price and FX refresh.
