# Notice

The XML files in this folder are the 66 standard business test instances of the
**XRechnung 3.0.2 test suite**, release `v2026-08-31`, published by the Koordinierungsstelle für
IT-Standards (KoSIT): <https://github.com/itplr-kosit/xrechnung-testsuite>.

- Licensed under the Apache License 2.0 (see `LICENSE` in this folder).
- Copied **unmodified** from `instances/standard/` of `xrechnung-3.0.2-testsuite-2026-08-31.zip`.
  Git stores them byte-exact (`-text` in `.gitattributes`); SHA-256 checksums matched the release on 2026-11-13.
- The fictitious party data in the files is KoSIT's, not ours.

## Known inconsistency in the suite

Case **01.21**: the UBL file states the seller VAT identifier (BT-31) as `DE 123456789`, the CII file as `DE152338654`.
All other fields of the pair agree. The tests record this explicitly (`KositCorpusTests.KnownUpstreamDifferences`).
