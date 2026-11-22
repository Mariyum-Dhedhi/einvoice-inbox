# The ERP sync (package 16c): a static Go binary on an empty image. Built from sync/erp-sync (its own go.mod, standard
# library only, so nothing is downloaded). The build image is pinned by tag and digest, the same one CI tests with.
# Run it read-only and as the non-root user below; settings and secrets come from environment variables only.

FROM golang:1.27.3-bookworm@sha256:dc0963f32053e4bc451d24d87e3892d42d01021b61f093e98eb95c0352bb84b0 AS build
WORKDIR /src
COPY go.mod ./
COPY main.go ./
COPY internal/ internal/
RUN CGO_ENABLED=0 GOFLAGS=-trimpath go build -ldflags="-s -w" -o /out/erp-sync . \
 && go version -m /out/erp-sync | head -1

# scratch: no shell, no package manager, nothing but the binary and the CA certificates for an https ERP.
FROM scratch
COPY --from=build /etc/ssl/certs/ca-certificates.crt /etc/ssl/certs/ca-certificates.crt
COPY --from=build /out/erp-sync /erp-sync
USER 65532:65532
ENTRYPOINT ["/erp-sync"]
