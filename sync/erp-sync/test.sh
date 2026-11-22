#!/bin/sh
# Checks the Go sync as CI does: formatting, go vet, and the tests with the race detector.
# Run inside the pinned Go image from the repository root (the tests read contracts/d365):
#   docker run --rm -v "$PWD:/repo" -w /repo/sync/erp-sync golang:1.27.3-bookworm@sha256:dc0963f32053e4bc451d24d87e3892d42d01021b61f093e98eb95c0352bb84b0 sh test.sh
set -eu
unformatted=$(gofmt -l .)
if [ -n "$unformatted" ]; then
  echo "Not gofmt-formatted:"; echo "$unformatted"; exit 1
fi
go vet ./...
go test -race -count=1 ./...
