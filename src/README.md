# UrlShortener

A minimal URL shortener MVP: create short links and redirect through them. ASP.NET Core
Web API / MVC on **.NET 9**, EF Core over SQLite, layered/Clean-Architecture solution
structure. See `documentation/` for the full design trail (requirements → design → this
as-built MVP).

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (pinned via `global.json` to `9.0.310`)

## Running locally

```bash
# From the src/ folder (this folder):
dotnet run --project UrlShortener.Api
```

- The API listens on `http://localhost:5236` (see `UrlShortener.Api/Properties/launchSettings.json` for the full profile, including the HTTPS profile).
- **No separate migration step is required** — `Program.cs` calls `AppDbContext.Database.Migrate()` on startup, which creates (or updates) the SQLite database file automatically. The file lives at `src/db/urlshortener.db`, resolved relative to the solution root regardless of where `dotnet run` is launched from.
- If you do want to apply migrations explicitly ahead of time (e.g. in a CI step), run:
  ```bash
  dotnet ef database update --project UrlShortener.Infrastructure --startup-project UrlShortener.Api
  ```

### Swagger UI

In the `Development` environment (the default for `dotnet run`), Swagger UI is available at:

```
http://localhost:5236/swagger
```

### Health checks

- `GET /health/live` — process-level liveness (no dependency checks).
- `GET /health/ready` — readiness, including a SQLite `DbContext` check.

## Trying the API

The easiest way is `requests.http` (in this folder) — open it in VS Code with the
[REST Client extension](https://marketplace.visualstudio.com/items?itemName=humao.rest-client)
(or in Rider/Visual Studio's built-in HTTP client) and click "Send Request" above each
block. It covers both the create and fetch/redirect endpoints, valid and invalid cases.

Equivalent `curl` examples:

```bash
# Create a short URL
curl -i -X POST http://localhost:5236/api/v1/short-urls \
  -H "Content-Type: application/json" \
  -d '{"originalUrl":"https://www.anthropic.com/some/very/long/path?query=1"}'

# Fetch/redirect (replace {code} with the "code" field from the response above)
curl -i http://localhost:5236/{code}
```

Note the API surface is split by design:

- `POST /api/v1/short-urls` — the versioned API contract (item 11: `Asp.Versioning.Mvc`, `api/v{version:apiVersion}/...`).
- `GET /{code}` — deliberately **unversioned**, at the application root. It's a public short-link surface, not a versioned API a client negotiates against — the whole point of a short link is a short, stable path.

## Running the tests

```bash
dotnet test UrlShortener.sln
```

This runs `UrlShortener.Application.Tests` (unit tests, Moq + xUnit, no database) and
`UrlShortener.IntegrationTests` (`WebApplicationFactory<Program>` + a private in-memory
SQLite connection per test class — a real ASP.NET Core pipeline, but never touches
`src/db/urlshortener.db`). `UrlShortener.LoadTests` is **not** an xUnit project and is
excluded from `dotnet test` automatically — see below.

## Load testing (smoke/harness only)

`UrlShortener.LoadTests` (NBomber) defines two scenarios — hammering the create endpoint
and the fetch/redirect endpoint — against a configurable base URL (default
`http://localhost:5236`, override with the `LOADTEST_BASE_URL` environment variable).
It is **not run as part of this task** and is not wired into CI; it only needs to build.
To actually run it once you want to:

```bash
dotnet run --project UrlShortener.LoadTests --configuration Release
```

Measuring this MVP against the v2 design's extreme-scale numbers
(`documentation/02-design/v2/design/considerations/`) would require the actual v2
infrastructure (Redis caching, distributed rate limiting, database
partitioning/sharding, etc.) to be a meaningful comparison — this project is currently
single-instance/SQLite, so a load test here is a smoke/harness check, not a
scalability benchmark.

## Solution layout

See `documentation/02-design/v3.MVP/design/api-project-structure.md` for the full,
as-built project structure and dependency-direction diagram. Briefly:

| Project | Responsibility |
|---|---|
| `UrlShortener.Api` | ASP.NET Core host: controllers, middleware, Swagger, health checks, the DI composition root (`Program.cs`). |
| `UrlShortener.Application` | Use-case orchestration, DTOs, validation. |
| `UrlShortener.Domain` | Entities, repository/strategy interfaces, domain constants & exceptions. Zero third-party dependencies. |
| `UrlShortener.Infrastructure` | EF Core `DbContext`, repositories, the NULL cache/event-publisher placeholders. |
| `UrlShortener.Common` | Zero-dependency shared helpers (guard clauses). |
| `UrlShortener.Application.Tests` | Unit tests (Moq + xUnit). |
| `UrlShortener.IntegrationTests` | Integration tests (`WebApplicationFactory` + in-memory SQLite). |
| `UrlShortener.LoadTests` | NBomber load-test harness (build-only in this task; see above). |

## Docker

A multi-stage `Dockerfile` for `UrlShortener.Api` is at `UrlShortener.Api/Dockerfile`
(SDK build stage → ASP.NET runtime stage). Build from this folder (`src/`) as the
context:

```bash
docker build -f UrlShortener.Api/Dockerfile -t urlshortener-api .
```

Not run/tested as part of this task (no Docker available in this environment) — see the
Dockerfile's own header comment for the SQLite-path caveat when containerizing.
