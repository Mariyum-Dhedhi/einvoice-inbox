# KoSIT validator container

Runs the official [KoSIT validator](https://github.com/itplr-kosit/validator) with the
[XRechnung configuration](https://github.com/itplr-kosit/validator-configuration-xrechnung) as an HTTP service.
The application posts each XML invoice to it; `200` means acceptable, `406` not acceptable (EN 16931 + XRechnung 3.0.2).

| Component | Version | Licence |
|---|---|---|
| Validator | 1.6.3 | Apache-2.0 |
| XRechnung configuration | 2026-08-31 (XRechnung 3.0.2; same release as `testdata/xrechnung/kosit`) | Apache-2.0 |
| Runtime | `eclipse-temurin:21.0.13_9-jre-noble`, pinned by digest | GPLv2 + Classpath Exception |

## Security

- Both downloads are pinned by URL **and** SHA-256 (`ADD --checksum`); the build fails if a byte differs.
- Base images are pinned by tag and digest.
- Runs as user `10001`; files are root-owned and read-only.
- In `compose.yaml`: read-only root filesystem, `/tmp` as tmpfs, all capabilities dropped,
  `no-new-privileges`, 1 GB memory limit, port bound to `127.0.0.1` only.
- The KoSIT demo GUI is disabled (`--disable-gui`); KoSIT states it is not for production.
- The application **fails closed**: if this service is down or answers anything other than 200/406, the invoice
  goes to a human with `CONFORMANCE_UNAVAILABLE`. It is never treated as accepted.

## Updating

Dependabot proposes new base images. The validator and configuration downloads must be updated by hand:

1. Pick releases at least two weeks old, and keep the configuration in step with `testdata/xrechnung/kosit`.
2. Download both assets and run `sha256sum` on them.
3. Change the URLs and `--checksum` values in the `Dockerfile`, and the image tag in `compose.yaml`.
4. CI must still accept all 66 samples (`Category=Kosit` tests).
