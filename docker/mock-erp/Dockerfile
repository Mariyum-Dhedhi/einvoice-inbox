# Mock ERP (vendor-invoice endpoint) for the local demo: docker compose up -d mock-erp.
# Built from the repository root (see compose.yaml) so the shared build settings and locked packages apply.
# Images are pinned by tag and digest (the runtime matches the 10.0.14 packages).

FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY tests/EInvoice.MockErp/ tests/EInvoice.MockErp/
RUN dotnet restore tests/EInvoice.MockErp/EInvoice.MockErp.csproj --locked-mode \
 && dotnet publish tests/EInvoice.MockErp/EInvoice.MockErp.csproj --configuration Release --no-restore --output /app

# Chiseled: no shell, no package manager, a non-root user by default.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-chiseled@sha256:f2557fb81b1cfcfdfcde9d3bf247ed60b2cd38f00a88153f61e34737fab8ade7
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "EInvoice.MockErp.dll"]
