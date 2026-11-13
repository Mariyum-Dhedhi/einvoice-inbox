# E-invoice test corpus

Reusable test data for German e-invoice processing. Everything here is either official public sample data
or synthetic; there are no real companies, people or bank accounts.

## `xrechnung/kosit/` — official XRechnung 3.0.2 samples (66 files)

33 business cases from the KoSIT test suite, each in both XRechnung syntaxes:
`NN.NNa-INVOICE_ubl.xml` (OASIS UBL 2.1) and `NN.NNa-INVOICE_uncefact.xml` (UN/CEFACT CII).
Unmodified, Apache-2.0; see [`NOTICE.md`](xrechnung/kosit/NOTICE.md) for source, version and one known inconsistency.

What the tests check on every file:
- it parses and is an invoice (type 380, 384 or 389);
- **BR-CO-10**: the line net amounts add up to BT-106;
- **BR-CO-15**: BT-109 + BT-110 = BT-112;
- the UBL and CII files of each case give the same result.

## `xrechnung/malicious/` — attack and edge cases (synthetic)

| File | What it tries | Expected result |
|---|---|---|
| `xxe-local-file.xml` | External entity reading `/etc/passwd` into the invoice number | Refused: `DtdNotAllowed` |
| `xxe-network.xml` | External entity calling a cloud metadata URL (SSRF) | Refused: `DtdNotAllowed`, no request |
| `billion-laughs.xml` | Nested entities expanding to about 3 GB | Refused: `DtdNotAllowed` |
| `external-dtd.xml` | DOCTYPE pointing at a remote DTD | Refused: `DtdNotAllowed`, no request |
| `malformed.xml` | Truncated document | Refused: `Malformed` |
| `not-an-invoice.xml` | Well-formed XHTML | Refused: `UnsupportedDocument` |
| `credit-note.xml` | UBL credit note (valid, but out of scope) | Refused: `UnsupportedDocument` |

The tests also build some cases at run time: an XXE inside an otherwise valid invoice pointing at a real temporary
file, a DOCTYPE pointing at a real listening port, an oversized document, a missing BT-1, a non-numeric amount,
UBL order-reference placeholders, and a second VAT total in a tax currency listed first.
