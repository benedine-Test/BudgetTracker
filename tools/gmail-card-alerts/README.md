# Bank card alerts from Gmail

A Google Apps Script that reads your banks' card alert emails every 10 minutes and posts each spend to `POST /api/ingest/card-alert`. It catches what the Apple Pay Shortcut can't: physical card swipes, online checkouts, card-on-file charges. When the same purchase also came through Apple Pay, the API merges the two (same amount, within 30 minutes, similar merchant).

## Set it up (about 15 minutes)

1. **Turn on alerts at each bank** for every card transaction (often there's a minimum amount, so set it to the lowest the bank allows).
2. Go to [script.google.com](https://script.google.com) signed in as the Gmail account that gets the alerts → **New project**. Paste `Code.gs` over the empty file and save.
3. **Project Settings → Script properties**, add:
   | Property | Value |
   |---|---|
   | `BUDGET_API_URL` | your site, e.g. `https://<your-app>.azurewebsites.net` |
   | `BUDGET_API_KEY` | the same key the app asks for |
   | `DRY_RUN` | `true` |
4. **Fix the senders.** In `BANKS` at the top of the script, the `from:` addresses are guesses. Open a real alert from each bank, copy its sender address, and test the query in the Gmail search box. Remove banks you don't use.
5. **Preview.** Choose `previewAlerts` in the toolbar → **Run** → allow the permissions (read Gmail, connect to your site). Open **Execution log**: each alert from the last 2 days shows as `would send {...}` or `skip (reason)`. Nothing is sent or labelled.
6. **Link the cards.** Each alert is sent with a card like `DBS card ending 1234`. In the app, Accounts → the account → card names, add `ending 1234` (comma-separated after any Apple Pay names) so those spends are filed to that account.
7. **Go live.** Set `DRY_RUN` to `false`, then run `installTrigger` once. `removeTrigger` stops it.

## What it does with each email

- **Sent**: labelled `Budget/Logged` in Gmail.
- **Credits, refunds, declined payments, OTPs**: skipped quietly. Money in still comes from statement imports, as before.
- **A spend it couldn't read** (no amount, no merchant, or the API refused it): labelled `Budget/Check`. Open it and add it by hand, or send me a sample with personal details removed and the patterns can be adjusted.
- **The site didn't answer** (asleep, down, wrong key): left for the next run, 10 minutes later. Alerts are retried for 2 days.

The time recorded is when the email arrived, not the time written in it. Alerts arrive within a minute or two, well inside the 30-minute window used to merge with Apple Pay taps.

## When an alert is read wrongly

Parsing is in `parseAlert` and has no Gmail calls, so it can be tested in Node:

```bash
node -e "const {parseAlert}=require('./Code.gs'); console.log(parseAlert(require('fs').readFileSync('alert.txt','utf8'), 'subject', 'DBS'))"
```
