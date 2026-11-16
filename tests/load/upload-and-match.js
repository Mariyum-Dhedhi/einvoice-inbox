// Load test: clerks upload XRechnung invoices, open them, re-match them and look at the exception queue.
// Runs in the pinned grafana/k6 image against a local app on the load database; see tests/load/README.md.
//
// Every iteration (one clerk's turn):
//   1. upload    POST /api/invoices            a new invoice for order PO-0101 (tests/load/invoice-template.xml)
//   2. detail    GET  /api/invoices/{id}       must show no findings: the filled template is a valid invoice
//   3. rematch   POST /api/invoices/{id}/match the same invoice, matched again (it is an Exception: PO-0101
//                                              received 13 packs, the invoices ask for more)
//   4. queue     GET  /api/invoices?status=Exception, first page
//
// Open model: a fixed number of turns per second (RATE), whatever the response times; latency is what a clerk waits.
import http from 'k6/http';
import { check, fail } from 'k6';
import exec from 'k6/execution';

const BASE = __ENV.BASE_URL || 'https://host.docker.internal:7270';
const USER = __ENV.LOAD_USER || 'clerk@example.com';
const PASSWORD = __ENV.LOAD_PASSWORD;
const TEMPLATE = open('./invoice-template.xml');

// One quantity per invoice, cycling: no two invoices of a day share an amount (POSSIBLE_DUPLICATE would stop them
// before matching, and matching is what this measures).
const QUANTITIES = 400;
const NEWEST = Date.UTC(2026, 10, 13); // 2026-11-13

export const options = {
  // The app runs with the local ASP.NET Core development certificate, which the container does not trust.
  insecureSkipTLSVerify: true,
  // Keep each clerk's sign-in cookie across iterations (k6 empties the jar before every iteration by default).
  noCookiesReset: true,
  scenarios: {
    clerks: {
      executor: 'constant-arrival-rate',
      rate: Number(__ENV.RATE || 10),
      timeUnit: '1s',
      duration: __ENV.DURATION || '3m',
      preAllocatedVUs: 20,
      maxVUs: 100,
    },
  },
  // Listed per step so the summary shows each one; the limits only guard against a broken run.
  thresholds: {
    checks: ['rate>0.99'],
    http_req_failed: ['rate<0.01'],
    'http_req_duration{step:upload}': ['p(95)<5000'],
    'http_req_duration{step:detail}': ['p(95)<5000'],
    'http_req_duration{step:rematch}': ['p(95)<5000'],
    'http_req_duration{step:queue}': ['p(95)<5000'],
  },
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

function signIn() {
  const page = http.get(`${BASE}/account/login`, { tags: { step: 'signin' } });
  const token = page.html().find('input[name=__RequestVerificationToken]').attr('value');
  const handler = page.html().find('input[name=_handler]').attr('value');
  if (!token || !handler) {
    fail('login form not found');
  }

  const res = http.post(`${BASE}/account/login`, {
    'Input.UserName': USER,
    'Input.Password': PASSWORD,
    __RequestVerificationToken: token,
    _handler: handler,
  }, { tags: { step: 'signin' } });
  // A failed sign-in shows the form again; a successful one has left it.
  return res.status === 200 && !res.url.includes('/account/login');
}

export function setup() {
  if (!PASSWORD) {
    fail('Set LOAD_PASSWORD (the DemoUsers:Password of the app under test).');
  }

  // Once, before any load: a wrong password stops here after one failure (lockout counts five).
  if (!signIn()) {
    fail(`Sign-in as ${USER} failed.`);
  }

  // Invoices are numbered after those already stored, so a second run never repeats an amount and date.
  const first = http.get(`${BASE}/api/invoices?pageSize=1`).json();
  return { offset: first.totalCount, run: Date.now().toString(36) };
}

function cents(c) {
  return `${Math.floor(c / 100)}.${String(c % 100).padStart(2, '0')}`;
}

// Exactly LoadInvoiceTemplate.Fill (LoadTemplateTests proves that one against the invoice builder).
function invoice(number, date, quantity) {
  const net = quantity * 489;
  const tax = Math.floor((net * 19 + 50) / 100);
  return TEMPLATE
    .replaceAll('{{NUMBER}}', number)
    .replaceAll('{{DATE}}', date)
    .replaceAll('{{QUANTITY}}', String(quantity))
    .replaceAll('{{NET}}', cents(net))
    .replaceAll('{{TAX}}', cents(tax))
    .replaceAll('{{GROSS}}', cents(net + tax));
}

let signedIn = false;

export default function (data) {
  if (!signedIn) {
    signedIn = signIn();
    if (!signedIn) {
      fail(`Sign-in as ${USER} failed.`);
    }
  }

  const n = data.offset + exec.scenario.iterationInTest;
  const quantity = 1 + (n % QUANTITIES);
  const date = new Date(NEWEST - Math.floor(n / QUANTITIES) * 86400000).toISOString().slice(0, 10);
  const number = `LOAD-${data.run}-${exec.scenario.iterationInTest}`;

  const upload = http.post(`${BASE}/api/invoices`, {
    file: http.file(invoice(number, date, quantity), `${number}.xml`, 'application/xml'),
  }, { tags: { step: 'upload' } });
  if (!check(upload, { 'upload: 201 Created': (r) => r.status === 201 })) {
    // Problem details carry a safe "outcome" name (never document content): enough to see why.
    let outcome = '';
    try { outcome = upload.json().outcome || upload.json().title || ''; } catch (e) { /* not JSON */ }
    console.warn(`upload ${number}: ${upload.status} ${outcome}`);
    return;
  }

  const id = upload.json().invoiceId;
  const detail = http.get(`${BASE}/api/invoices/${id}`, { tags: { step: 'detail', name: 'GET /api/invoices/{id}' } });
  check(detail, {
    'detail: 200': (r) => r.status === 200,
    'detail: no findings': (r) => r.status === 200 && r.json().findings.length === 0,
    'detail: matched': (r) => r.status === 200 && r.json().matchResults.length > 0,
  });

  if (upload.json().status === 'Exception') {
    const rematch = http.post(`${BASE}/api/invoices/${id}/match`, null, { tags: { step: 'rematch', name: 'POST /api/invoices/{id}/match' } });
    check(rematch, { 'rematch: 200': (r) => r.status === 200 });
  }

  const queue = http.get(`${BASE}/api/invoices?status=Exception&page=1&pageSize=25`, { tags: { step: 'queue' } });
  check(queue, { 'queue: 200': (r) => r.status === 200 });
}
