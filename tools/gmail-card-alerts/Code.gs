/**
 * Bank card alert emails → Budget Tracker.
 *
 * Runs in Google Apps Script on your Gmail account. Every 10 minutes it reads new card
 * alert emails, pulls out amount / merchant / card, and posts them to
 * POST /api/ingest/card-alert. The API already merges an alert with the Apple Pay tap
 * for the same purchase (same amount, within 30 minutes, similar merchant).
 *
 * Setup is in README.md next to this file. In short:
 *   1. Project Settings → Script properties: BUDGET_API_URL, BUDGET_API_KEY.
 *   2. Edit BANKS below to match your banks' alert senders.
 *   3. Run previewAlerts() and read the log. Nothing is sent.
 *   4. Set script property DRY_RUN to false, then run installTrigger() once.
 */

// One entry per bank. `query` is a Gmail search: test it in the Gmail search box first.
// The sender addresses are guesses. Copy the real "from" address off one of your alerts.
const BANKS = [
  { name: 'DBS', query: 'from:(ibanking.alert@dbs.com)' },
  { name: 'OCBC', query: 'from:(notifications@ocbc.com)' },
  { name: 'UOB', query: 'from:(unialerts@uobgroup.com)' },
  { name: 'Citi', query: 'from:(alerts@citibank.com.sg)' },
];

// Only look this far back. The trigger runs every 10 minutes, so 2 days covers any outage
// of a day without re-reading your whole inbox.
const LOOKBACK = 'newer_than:2d';

// Emails the script handled get one of these labels, so you can see what it did.
const LABEL_LOGGED = 'Budget/Logged';
const LABEL_CHECK = 'Budget/Check';  // read but not sent: open it and add by hand if it was a spend

const PROCESSED_KEY = 'processedIds';
// A script property holds about 9 KB: 300 Gmail ids fit, and is far more than 2 days of alerts.
const PROCESSED_MAX = 300;

// ---- Entry points -------------------------------------------------------------------

/** What the trigger runs. Sends unless the DRY_RUN script property is "true" (the default). */
function processAlerts() {
  run_(!isDryRun_());
}

/** Reads the last 2 days of alerts and logs what would be sent. Sends nothing, marks nothing. */
function previewAlerts() {
  run_(false);
}

/** Run once to start the 10-minute schedule. Running it again doesn't add a second one. */
function installTrigger() {
  ScriptApp.getProjectTriggers()
    .filter(t => t.getHandlerFunction() === 'processAlerts')
    .forEach(t => ScriptApp.deleteTrigger(t));
  ScriptApp.newTrigger('processAlerts').timeBased().everyMinutes(10).create();
  Logger.log('Trigger installed: processAlerts every 10 minutes.');
}

/** Stops the schedule. */
function removeTrigger() {
  ScriptApp.getProjectTriggers()
    .filter(t => t.getHandlerFunction() === 'processAlerts')
    .forEach(t => ScriptApp.deleteTrigger(t));
  Logger.log('Trigger removed.');
}

// ---- The run ------------------------------------------------------------------------

function run_(send) {
  const lock = LockService.getScriptLock();
  if (!lock.tryLock(5000)) return; // the previous run is still going
  try {
    const processed = loadProcessed_();
    const logged = send ? label_(LABEL_LOGGED) : null;
    const check = send ? label_(LABEL_CHECK) : null;

    for (const bank of BANKS) {
      const threads = GmailApp.search(`${bank.query} ${LOOKBACK}`, 0, 100);
      for (const thread of threads) {
        for (const message of thread.getMessages()) {
          const id = message.getId();
          if (processed.has(id)) continue;
          if (Date.now() - message.getDate().getTime() > 2 * 24 * 3600 * 1000) continue;

          const alert = parseAlert(message.getPlainBody() || '', message.getSubject() || '', bank.name);
          const when = Utilities.formatDate(message.getDate(), 'Asia/Singapore', "yyyy-MM-dd'T'HH:mm:ssXXX");

          if (alert.skip) {
            Logger.log(`[${bank.name}] skip (${alert.skip}): ${message.getSubject()}`);
            if (send) {
              processed.add(id);
              // Not a spend we recognise. Credits and OTPs are expected; anything else needs a look.
              if (alert.skip !== 'not a spend') thread.addLabel(check);
            }
            continue;
          }

          const body = { amount: alert.amount, merchant: alert.merchant, card: alert.card, date: when };
          if (!send) {
            Logger.log(`[${bank.name}] would send ${JSON.stringify(body)}`);
            continue;
          }

          const result = post_(body);
          if (result.ok) {
            processed.add(id);
            thread.addLabel(logged);
            Logger.log(`[${bank.name}] sent: ${result.message}`);
          } else if (result.status >= 400 && result.status < 500 && result.status !== 401 && result.status !== 429) {
            // The API couldn't read it. Retrying won't help.
            processed.add(id);
            thread.addLabel(check);
            Logger.log(`[${bank.name}] rejected ${result.status}: ${result.message}`);
          } else {
            // Wrong key, rate limit, or the site is asleep/down: left for the next run.
            Logger.log(`[${bank.name}] will retry, ${result.status}: ${result.message}`);
          }
        }
      }
    }
    if (send) saveProcessed_(processed);
  } finally {
    lock.releaseLock();
  }
}

function post_(body) {
  const props = PropertiesService.getScriptProperties();
  const base = (props.getProperty('BUDGET_API_URL') || '').replace(/\/+$/, '');
  const key = props.getProperty('BUDGET_API_KEY');
  if (!base || !key) throw new Error('Set the BUDGET_API_URL and BUDGET_API_KEY script properties first.');

  try {
    const res = UrlFetchApp.fetch(`${base}/api/ingest/card-alert`, {
      method: 'post',
      contentType: 'application/json',
      headers: { 'X-Api-Key': key },
      payload: JSON.stringify(body),
      muteHttpExceptions: true,
    });
    const status = res.getResponseCode();
    let message = res.getContentText();
    try { message = JSON.parse(message).message || message; } catch (e) { /* not JSON */ }
    return { ok: status >= 200 && status < 300, status, message };
  } catch (e) {
    // Timeouts: a free Azure plan can take ~20 s to wake up.
    return { ok: false, status: 0, message: String(e) };
  }
}

// ---- Parsing ------------------------------------------------------------------------
// Pure functions (no Gmail calls), so they can be tested outside Apps Script.

// Safety wording that names OTPs, refunds etc. without being about this transaction.
const WARNING = /\b(never|do not|don't|if unauthori[sz]ed|if you did not|scam|beware|fraud)\b/i;

// A money-out alert says one of these.
const SPEND_WORDS = /\b(transaction|purchase|spent|charged|debited|payment|paid|used|withdrawal)\b/i;
// Not a spend, or not one to count: incoming money, refunds, failures, codes.
const NOT_SPEND = /\b(received|credited|credit(?:ed)? to your|deposit|incoming|refund|reversal|reversed|declined|unsuccessful|failed|one[- ]time password|OTP)\b/i;

const CURRENCY = '(?:SGD|S\\$|USD|US\\$|MYR|RM|EUR|€|GBP|£|JPY|¥|AUD|A\\$|HKD|HK\\$)';
const AMOUNT = new RegExp(`${CURRENCY}\\s?-?\\d[\\d,]*(?:\\.\\d{1,2})?`, 'i');
const LABELLED_AMOUNT = new RegExp(`\\b(?:amount|transaction amount|amt)\\s*[:：]\\s*(${CURRENCY}\\s?-?\\d[\\d,]*(?:\\.\\d{1,2})?)`, 'i');

// "To: SHOP", "Merchant: SHOP", "Merchant Name: SHOP", "Description: SHOP" on their own line.
const LABELLED_MERCHANT = /^\s*(?:to|merchant(?: name)?|payee|description|at)\s*[:：]\s*(.+?)\s*$/im;
// "... at SHOP on 12 Oct", "... at SHOP." or "... at SHOP, ..."
const SENTENCE_MERCHANT = /\bat\s+(?!\d{1,2}[:.]\d{2})(.+?)(?=\s+on\s+\d|\s+on\s+[A-Z][a-z]{2}\b|\.\s|\.$|,|\n|\s+if\s|$)/gim;

const CARD = /\b(?:card|account)\b[^\n.]{0,40}?(?:ending(?:\s+(?:with|in))?|no\.?|number|x{2,}|\*{2,})\s*[:\-]?\s*[x*]*\s*(\d{4})\b/i;

/**
 * @returns {{amount: string, merchant: string, card: string|null}} or {skip: reason}
 */
function parseAlert(body, subject, bankName) {
  const text = String(body)
    .replace(/\r\n?/g, '\n')
    .replace(/[   ]/g, ' ')
    .replace(/[ \t]+/g, ' ');
  const all = `${subject}\n${text}`;
  // Footers say things like "never share your OTP" or "refund policy": only the top decides,
  // and warning sentences in it don't count.
  const head = `${subject}\n${text.slice(0, 600)}`
    .split(/(?<=[.!?])\s+|\n/)
    .filter(s => !WARNING.test(s))
    .join('\n');

  if (NOT_SPEND.test(head)) return { skip: 'not a spend' };
  if (!SPEND_WORDS.test(head)) return { skip: 'not a spend' };

  const labelled = LABELLED_AMOUNT.exec(text);
  const amountMatch = labelled ? labelled[1] : (AMOUNT.exec(text) || [])[0];
  if (!amountMatch) return { skip: 'no amount found' };

  let merchant = cleanMerchant_((LABELLED_MERCHANT.exec(text) || [])[1]);
  // "at 14:32 at SHOP": take the first "at ..." that reads as a name.
  for (const m of text.matchAll(SENTENCE_MERCHANT)) {
    if (merchant) break;
    merchant = cleanMerchant_(m[1]);
  }
  // Without a merchant the API can't merge this with the Apple Pay tap, so it would count twice.
  if (!merchant) return { skip: 'no merchant found' };

  const card = (CARD.exec(all) || [])[1];
  return {
    amount: amountMatch.replace(/\s+/g, ' ').trim(),
    merchant,
    card: card ? `${bankName} card ending ${card}` : null,
  };
}

function cleanMerchant_(raw) {
  if (!raw) return null;
  const m = raw
    .replace(/\s+/g, ' ')
    .replace(/^(?:the\s+)?merchant\s+/i, '')
    .replace(/[.,;]+$/, '')
    .trim();
  // A merchant that is really an amount or a date means the pattern grabbed the wrong line.
  if (m.length < 2 || AMOUNT.test(m) || /^\d{1,2}([\/ :-]\d|\s*(am|pm)\b)/i.test(m)) return null;
  return m.slice(0, 200);
}

// ---- Helpers ------------------------------------------------------------------------

function isDryRun_() {
  const v = PropertiesService.getScriptProperties().getProperty('DRY_RUN');
  return v === null || String(v).toLowerCase() !== 'false';
}

function label_(name) {
  return GmailApp.getUserLabelByName(name) || GmailApp.createLabel(name);
}

function loadProcessed_() {
  const raw = PropertiesService.getScriptProperties().getProperty(PROCESSED_KEY);
  return new Set(raw ? JSON.parse(raw) : []);
}

function saveProcessed_(set) {
  // Message ids only need remembering for the lookback window; keep the newest.
  const ids = Array.from(set).slice(-PROCESSED_MAX);
  PropertiesService.getScriptProperties().setProperty(PROCESSED_KEY, JSON.stringify(ids));
}

if (typeof module !== 'undefined') module.exports = { parseAlert };
