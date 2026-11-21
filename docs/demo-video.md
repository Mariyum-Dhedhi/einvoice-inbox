# Demo video: script (2 minutes)

A screen recording of the local app with voice-over. Everything shown is synthetic: the seeded vendors and orders,
and the files in `demo/invoices` (what each one does: [demo/README.md](../demo/README.md)).

## Before recording

1. **Start from a fresh database**, so every demo file can be uploaded once: `docker compose down -v`,
   `docker compose up -d`, then start the app with AI and the ERP on (the command is in
   [demo/README.md](../demo/README.md#with-ai-plain-pdf-invoices)), at https://localhost:7169.
2. **Warm up the model.** Upload `09-ai-full-match.pdf` once and throw the database away again (step 1), or keep
   it and leave it out of the video. The first PDF after a start is slow while Ollama loads the model.
3. **Set up the browser.** Window 1440 x 900, zoom 100 %, English. Close other tabs and turn off notifications.
   Open `demo/invoices` in a file window next to the browser, ready to drag files in.
4. **Sign in to two browsers:** the clerk in one, the approver in a private window of the other. That way the
   video never shows a password being typed.
5. **Record** with OBS or the Windows Game Bar (Win + Alt + R), at 1080p and 30 frames per second. Speak slowly:
   the text below fits two minutes when read calmly.

## Script

| Time | On screen | Say |
|---|---|---|
| 0:00–0:12 | Inbox, as the clerk | "This is E-Invoice Inbox: accounts payable for German e-invoices. It takes XRechnung, ZUGFeRD and plain PDF invoices, checks them, matches them against the order and the goods receipt, and sends anything doubtful to a person." |
| 0:12–0:25 | Drag in `01-full-match.xml`. It opens, **Matched** | "An XRechnung invoice. It is validated, including with the government's official KoSIT validator, and matched line by line. Order, delivery and invoice agree, so it is Matched." |
| 0:25–0:45 | Back to the inbox, drag in `03-price-variance.xml`. **Exception**: point at the match panel, then at *Why is this an exception?* | "Here the supplier billed 389 euros for a desk ordered at 349. The three-way match shows the difference against the 2 % tolerance, and the app says why in plain words, with a next step." |
| 0:45–1:05 | Drag in `13-ai-letterhead-with-logo.pdf`. Wait for **Matched**, point at *AI · read from PDF* | "A plain PDF with no data inside. A language model reads it, running locally on this laptop, so no invoice leaves the machine. The model is treated as untrusted: every value must be found in the document, and the same rules run as for XML." |
| 1:05–1:25 | Drag in `17-ai-letterhead-new-bank-account.pdf`. **Exception**, the *Bank account not approved* finding | "This one looks genuine, but asks for payment to a new bank account. That is classic invoice fraud. The account is not approved for this supplier, so the invoice stops here. A new account needs two different admins to confirm it." |
| 1:25–1:45 | Approver window: `/approvals`, open 03, type a reason, *Approve* | "Only an approver decides, never on their own upload. An exception needs a reason, and every decision goes into an audit trail that is hash-chained, so a changed row is detected." |
| 1:45–2:00 | `/dashboard`, then the GitHub repository's README | "Measured, not claimed: zero wrong AI values reached Matched in the evaluation, and the load test found a deadlock that is now fixed. About 2,000 automated tests run on every pull request. The code is on GitHub." |

## Keep it true

- The ERP is a mock with a documented contract; say "posts to an ERP interface", never "posts to SAP".
- Quote AI and load numbers only as the README states them, with what was measured.
- If a step goes wrong (a slow model, a wrong click), record that part again rather than explaining it.
