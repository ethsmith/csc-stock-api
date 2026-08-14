# CSX Franchise Exchange API

Play-money market for CSC franchise teams. Prices move on match surprise (vs Elo/MMR expectation) and on AMM trading pressure.

## Layout

| Project | Role |
|---|---|
| `Csx.Domain` | AMM math, shock/decay, rounding, invariants |
| `Csx.Infrastructure` | EF Core / Postgres ledger, CSC Core GraphQL client, settlement |
| `csc-stock-api` (`Csx.Api`) | REST, SignalR, Discord OAuth, background jobs |
| `Csx.Tests` | Property tests, settlement fixtures, Testcontainers integration |

## Tradeable units

Each CSC **team** (franchise × tier) is a ticker. Prefix comes from Core (`ATL`, `HG`, …) plus a tier letter:

`P` Premier · `E` Elite · `C` Challenger · `N` Contender · `S` Prospect · `R` Recruit

Example: Atlantis Premier (Leviathans) → `ATLP`.

Team Elo is the average roster MMR from Core when visible, otherwise the tier midpoint. After each settlement we update a local Elo so expectation does not require a privileged Core token.

## Run locally

```bash
docker compose up -d db
# set Discord:ClientId / ClientSecret for real OAuth; in Development, POST /api/v1/auth/dev works
dotnet run --project csc-stock-api
```

- API: http://localhost:5233
- Swagger: http://localhost:5233/swagger
- SignalR: `/hub/market`
- Health: `/health`

On first boot the API migrates Postgres, then `MatchIngestHostedService` pulls season franchises/matches from `https://core.playcsc.com/graphql` every 60s. The first sync also runs an **implied open**: it replays completed matches from `ImpliedOpen:FromSeason` (default 11) through the active season onto today's tickers (org prefix + tier), then reseeds each pool so launch prices are not a flat $10. The book is rescaled so the mean stays at `Market:InitialPrice`. Teams with no mapped history stay at $10 before that rescale. Current-season completed matches are marked settled so ingest does not apply those results twice.

The board only lists **active** CSC lines (org + tier). If Core stops fielding a line, holders are redeemed at the last mark, the pool is halted as `Delisted`, and the ticker drops off the board. A later season that fields the same org+tier reuses that ticker (Core team ids change every season; we do not mint `ATLP2`).

Re-run later (admin):

```bash
curl -X POST "http://localhost:5233/api/v1/admin/implied-open?force=true" \
  -H "Authorization: Bearer $TOKEN"
```

Dev login (Development/Testing only):

```bash
curl -X POST http://localhost:5233/api/v1/auth/dev \
  -H 'Content-Type: application/json' \
  -d '{"discordId":"123","displayName":"ethan"}'
```

SPA OAuth: browser hits `/api/v1/auth/discord` → Discord → API callback sets the `csx_refresh` cookie → 302 to `{Frontend.Origin}/login` → SPA `POST /api/v1/auth/refresh` with credentials. CORS origins are `Cors:Origins` (default Vite `http://localhost:5173`).

Seed the book from Core:

```bash
curl -X POST http://localhost:5233/api/v1/admin/sync \
  -H "Authorization: Bearer $TOKEN"
```

Public reads the SPA needs: `GET /api/v1/config`, `/franchises`, `/franchises/{ticker}`, `/matches`, `/market/status`. Money fields are strings; change/impact/weight are fraction strings (`"0.084000"` = 8.4%).

## Config

See `appsettings.json`. Important groups: `Market`, `Shock` (`Surprise` or `SignedScaled`), `Breaker`, `Decay`, `CscCore`, `ImpliedOpen`, `Discord`, `Jwt`, `Frontend`, `Cors`.

Frontend contract: [`docs/csx-frontend-design.md`](docs/csx-frontend-design.md).

## Tests

```bash
dotnet test
```

Integration tests spin up Postgres 16 via Testcontainers.
