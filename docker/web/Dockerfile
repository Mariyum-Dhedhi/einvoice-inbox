# The web app for the public demo (docker compose --profile public, docs/self-hosting.md).
# Built from the repository root (see compose.yaml) so the shared build settings and locked packages apply.
# Images are pinned by tag and digest (the runtime matches the 10.0.14 packages).

FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet restore src/EInvoice.Web/EInvoice.Web.csproj --locked-mode \
 && dotnet publish src/EInvoice.Web/EInvoice.Web.csproj --configuration Release --no-restore --output /app \
 && mkdir /keys

# Chiseled: no shell, no package manager, a non-root user (1654). "extra": ICU and time zones, because amounts and
# dates are German and times are Berlin time; the plain chiseled image would run with invariant culture.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-chiseled-extra@sha256:a9298ccc39c6258ae71228479e2f3f8d2ceb6639a9927262a4b0ff7d319a0e7e
WORKDIR /app
COPY --from=build /app .
# The sign-in keys' folder (DataProtection:KeysPath), owned by the app user; a named volume mounted here starts as a copy.
COPY --from=build --chown=1654:1654 /keys /keys
USER 1654:1654
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DataProtection__KeysPath=/keys
EXPOSE 8080
ENTRYPOINT ["dotnet", "EInvoice.Web.dll"]
