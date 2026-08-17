# Item 13: multi-stage .NET 9 Dockerfile for UrlShortener.Api.
# Not run/tested as part of this task (no Docker available in this environment) -- this
# is a correct, realistic multi-stage build, not a smoke-tested artifact.
#
# Build context is expected to be the solution root (src/), e.g.:
#   docker build -f UrlShortener.Api/Dockerfile -t urlshortener-api .
#
# Note on the SQLite database (data-design-guidelines.md §1): the app resolves its
# connection string by walking up from the running assembly to the nearest *.sln file
# (see Program.cs's ResolveSqliteConnectionString()) and writes to "<that folder>/db/".
# That convention is meant for local development; a containerized deployment should
# instead mount a persistent volume and point the app at it via the
# ConnectionStrings__DefaultConnection-style override once that becomes configurable --
# out of scope for this MVP's Dockerfile, called out here rather than silently ignored.

# --- Build stage -----------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj files first (and the solution + global.json) so `dotnet restore` is cached
# as its own Docker layer, independent of source-code changes.
COPY UrlShortener.sln ./
COPY global.json* ./
COPY UrlShortener.Api/UrlShortener.Api.csproj UrlShortener.Api/
COPY UrlShortener.Application/UrlShortener.Application.csproj UrlShortener.Application/
COPY UrlShortener.Domain/UrlShortener.Domain.csproj UrlShortener.Domain/
COPY UrlShortener.Infrastructure/UrlShortener.Infrastructure.csproj UrlShortener.Infrastructure/
COPY UrlShortener.Common/UrlShortener.Common.csproj UrlShortener.Common/

RUN dotnet restore UrlShortener.Api/UrlShortener.Api.csproj

# Now copy the rest of the source and publish. Only the projects UrlShortener.Api actually
# depends on are needed at runtime; test/load-test projects are intentionally not copied
# into the image.
COPY UrlShortener.Api/ UrlShortener.Api/
COPY UrlShortener.Application/ UrlShortener.Application/
COPY UrlShortener.Domain/ UrlShortener.Domain/
COPY UrlShortener.Infrastructure/ UrlShortener.Infrastructure/
COPY UrlShortener.Common/ UrlShortener.Common/

RUN dotnet publish UrlShortener.Api/UrlShortener.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# --- Runtime stage ----------------------------------------------------------------------
# The ASP.NET runtime image (not the SDK) -- smaller, no build tooling, matches this
# project's net9.0 TargetFramework.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# curl is not present in the base runtime image; installed (as root, before switching to
# the non-root "app" user below) only so the HEALTHCHECK below has something to shell out
# to -- the one extra package this stage needs over the base image.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Run as the non-root user the base image already provides, rather than root.
USER app

COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# nfr-reliability-and-availability.md §4: liveness endpoint, suitable for a container
# orchestrator's own HEALTHCHECK/probe wiring.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD ["curl", "-f", "http://localhost:8080/health/live"] || exit 1

ENTRYPOINT ["dotnet", "UrlShortener.Api.dll"]
