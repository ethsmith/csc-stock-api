# CSX Franchise Exchange — Frontend Design

**Component:** React + TypeScript client (`csx-web`)
**Companion:** this repo's REST + SignalR API (`csc-stock-api`); original notes in `csx-backend-api-design.md`
**Status:** Draft v1.1 — aligned to the shipped API
**Owner:** TBD

---

## 1. What this client is

A trading terminal for a play-money market in league franchises. Users start with $500, buy and sell shares, and watch prices move when matches settle.

**The client renders. It never computes money.** Every price, balance, fee, and share count comes from the server. There is no client-side AMM math, no local P/L calculation, no optimistic price prediction. The one exception is display formatting.

### Product goals, in priority order

1. **Match nights feel like events.** The moment a settlement lands, the board should move and you should feel it.
2. **A trade takes under five seconds** from opening a franchise to a confirmed fill.
3. **Every price move is explainable.** Tap the candle, see why: expected +5.2, actual +2, surprise −3.2, shock −6.1%.
4. **The leaderboard is legible at a glance** and makes people want to beat someone specific.

### Non-goals

- Pro-trader density. This is 80 people in a Discord, not a prop desk.
- Offline support.
- Desktop-only. Roughly half of traffic will be phone-in-hand during a stream.

---

## 2. Stack

| Concern | Choice | Why |
|---|---|---|
| Build | Vite | |
| Framework | React 19 + TypeScript `strict` | `noUncheckedIndexedAccess` on too |
| Routing | React Router v7 (data mode) | Loaders pair well with TanStack Query |
| Server state | TanStack Query v5 | Cache, dedupe, retry, invalidation |
| Client state | Zustand | Only for genuine UI state: trade ticket, theme, toasts |
| Styling | Tailwind v4 + CSS custom properties | Tokens in CSS vars so the chart library can read them |
| Charts | `lightweight-charts` (TradingView) | Candlesticks are most of what makes this read as a real trading app. Recharts can't do this convincingly. |
| Realtime | `@microsoft/signalr` | |
| Forms | Controlled inputs; no form library | The only real form is the trade ticket |
| Types | `openapi-typescript` from the backend's OpenAPI doc | Contract types are generated in CI, never hand-written |
| Testing | Vitest + React Testing Library + MSW; Playwright for the trade flow | |

**No `number` for money.** Ever. Money and share values arrive as strings and stay strings until formatted. Use `dinero.js` or a thin `Decimal` wrapper for any client-side arithmetic (there should be almost none — mostly just summing displayed holdings).

```ts
// tokens the whole app uses
type Money = string & { readonly __brand: 'Money' };   // "1234.5600"
type Shares = string & { readonly __brand: 'Shares' };
type Bps = number;                                      // 100 = 1%
```

Branded types make it a compile error to pass a raw `number` where cash is expected. Cheap, and it catches the class of bug that matters most here.

---

## 3. Design direction

The default answer for a trading app is a black screen with acid-green numbers. It's the default because it's easy, and it will make this look like every crypto dashboard on the internet.

**Direction: "The Board."** A franchise exchange rendered as a physical quote board — enamel panel, brass fittings, mechanical split-flap digits. It borrows from the trading floor and the departure board rather than from the terminal. It's dark, because the audience is dark-mode-native and it'll live next to a stream, but it's warm dark, and the accent is brass rather than neon.

### Tokens

```css
:root {
  /* surface */
  --ink:    #0E0D0C;  /* board enamel — page background */
  --slab:   #1A1715;  /* raised panel — cards, rows */
  --edge:   #2C2724;  /* hairline rules, borders */

  /* content */
  --chalk:  #E8E3DA;  /* primary text, warm off-white */
  --dust:   #9A9188;  /* secondary text, labels */

  /* accent */
  --brass:  #C9A227;  /* CTAs, focus, active states, the bell */

  /* market semantics */
  --bull:   #4FA96C;  /* up — desaturated, earthy */
  --bear:   #D2503C;  /* down — vermilion brick, not fire-engine red */
  --flat:   #9A9188;
}
```

Brass as the interactive color is the choice that carries the whole thing: it means green and red are reserved *exclusively* for market direction. Nothing else on the screen is ever green or red, so those colors always mean one thing.

### Type

| Role | Face | Treatment |
|---|---|---|
| Display | **Archivo Expanded** 700 | Uppercase, tracking `0.02em`. Signage, not headlines. Franchise names, section headers. |
| UI / body | **Instrument Sans** 400/500 | Sentence case, normal tracking |
| Data | **Martian Mono** 400/600 | All prices, share counts, percentages, tickers. Wide and mechanical — reads as machine output. |

```css
.tabular { font-variant-numeric: tabular-nums; font-feature-settings: "tnum" 1; }
```

Every number in the app gets `.tabular`. Digits that shift width during a live price update is the single most amateur-looking thing a trading UI can do.

### Signature element

**The split-flap ticker.** A horizontal board across the top of the market page. On settlement, affected tickers physically flip — digits rotating through intermediate characters before landing on the new price. Not a fade, not a slide: a mechanical flip with staggered per-character delay.

This is the one place to spend animation budget. It fires a few times a night, it makes settlement feel like an event, and it's the thing someone screenshots into the Discord. Everything else in the UI stays still and quiet.

Reduced motion: the flip becomes a cross-fade with the same timing and stagger. The board still updates left-to-right so the sense of a sweep survives.

### Restraint

- No gradients except one: a 1px brass top-edge highlight on raised panels, suggesting a lit board.
- Border radius `2px` throughout. Board panels aren't rounded.
- One shadow depth. Cards sit on the board, they don't float above it.
- No skeleton shimmer. Loading states show the slab with a static `— — —` in the data face, like a board waiting for a quote.

---

## 4. Routes and information architecture

| Route | Screen | Job |
|---|---|---|
| `/` | **Market** | The board. Everything tradeable, sorted and scannable. |
| `/f/:ticker` | **Franchise** | Chart, trade, news. Where time gets spent. |
| `/portfolio` | **Portfolio** | What you own, what it's worth, how you got here. |
| `/leaderboard` | **Standings** | Ranked traders. Rivalry engine. |
| `/matches` | **Schedule** | Upcoming with lockout countdowns; settled with shock outcomes. |
| `/u/:id` | **Trader** | Public portfolio — holdings visible, cash hidden. |
| `/rules` | **How it works** | Shock copy plus numbers from `GET /config` |
| `/login` | Discord OAuth handoff | API callback redirects here; SPA then `POST /auth/refresh` |

**Auth sequence (Discord):**

1. SPA navigates the browser to `{API}/api/v1/auth/discord` (top-level GET, not XHR).
2. Discord returns to the **API** callback (`Discord:RedirectUri`, default `http://localhost:5233/api/v1/auth/discord/callback`).
3. API sets the httpOnly `csx_refresh` cookie and **302s** to `{Frontend.Origin}{PostLoginPath}` (`http://localhost:5173/login`).
4. `/login` calls `POST /api/v1/auth/refresh` with `credentials: 'include'`, stores the access token **in memory**, then routes to `/`.
5. REST calls send `Authorization: Bearer`. SignalR connects with `access_token` as a query param (`/hub/market`). On 401, retry refresh once.

Dev-only: `POST /api/v1/auth/dev` still returns JSON tokens (no redirect). Use it in MSW/local without Discord.

All SPA `fetch` / axios calls that hit `/auth/*` need `credentials: 'include'`. CORS origins are explicit (`Cors:Origins`); do not send `*`.

Mobile: bottom tab bar — Market, Portfolio, Standings, Schedule. The trade ticket is a bottom sheet, never a route.

---

## 5. Screens

### 5.1 Market

```
┌────────────────────────────────────────────────────────────┐
│  ▓ SPLIT-FLAP TICKER ▓  NAV $2,481.20  ▲ 3.2%  ●LIVE       │
├────────────────────────────────────────────────────────────┤
│  [ All ][ Held ][ Movers ][ Halted ]  tier: P E C N S R   sort: change ▾  │
├────────────────────────────────────────────────────────────┤
│  TICKER  FRANCHISE          PRICE     24H      SPARK    HOLD│
│  ▸ NVT   Nova Tactical    $14.20   ▲ 8.4%   ╱╲╱‾    142.5  │
│  ▸ RGE   Ragebait GC      $ 9.05   ▼ 3.1%   ‾╲╱╲       —   │
│  ▸ HRS   Harrison Ridge   $11.80   ● HALTED ─────     20.0  │
└────────────────────────────────────────────────────────────┘
```

- Row click → franchise page. Long-press / right-click → quick-trade sheet.
- Halted rows are visually inert: dimmed, sparkline flat-lined, `HALTED` badge with `resumesAt` on hover. A locked market should look locked, not just fail on submit.
- Sparklines are 24h, drawn as inline SVG from the **list payload's `spark` array** (hourly closes, 5m fallback). Do not fetch `/candles` per row and do not mount a chart instance per row — that's ~96 canvases.
- Filter by tier using `division` (`Premier` … `Recruit`) or the ticker's last letter (`P E C N S R`). Season 21 is ~96 tickers (22 orgs × 6 tiers), not ~40.
- Sort by change / price / holdings / ticker. Persist in URL params so a sorted board is linkable into Discord.

### 5.2 Franchise

```
┌────────────────────────────────────────────────────────────┐
│  NVT  NOVA TACTICAL                       $14.20  ▲ 8.4%   │
│  Elo 1712 · Fair value $13.40 · Next: vs RGE in 2h 14m     │
├──────────────────────────────────────┬─────────────────────┤
│  [5m][1h][1D]                        │  TRADE              │
│                                      │  ┌ Buy ─┬─ Sell ┐   │
│      ▮ candlestick chart ▮           │  │ $ [____250] │   │
│      settlement markers ●            │  │ [25][50][MAX]│   │
│                                      │  ├──────────────┤   │
├──────────────────────────────────────┤  │ ≈ 17.6056 sh │   │
│  MARKET NEWS                         │  │ avg  $14.20  │   │
│  ● Beat RGE 13-7 · expected +2.1 ·   │  │ impact +0.9% │   │
│    surprise +3.9 · shock +6.1%       │  │ fee    $1.25 │   │
│  ● Roster: kx3 → active              │  ├──────────────┤   │
│  ● Halted for match settlement       │  │ [   BUY   ]  │   │
└──────────────────────────────────────┴──┴──────────────┴───┘
```

- Settlement markers on the chart are the payoff. Click one → popover with the full expectation math from `GET /franchises/{id}/settlements` (or the nested `settlement` on the event). **Do not recompute shock.** This is what makes the game feel fair rather than arbitrary.
- Fair-value line drawn as a dashed horizontal at `fundamental`, so "trading above fair value" is visible rather than a thing you have to reason about.
- News feed is `GET /franchises/{id}/events`, cursor-paginated. Settlement rows include a nested `settlement` object with expected/actual/surprise/shock/prices.

### 5.3 Trade ticket

The most important component in the app. State machine, not ad-hoc booleans:

```
idle ──amount>0──▶ quoting ──▶ quoted ──confirm──▶ submitting ──▶ filled
                      │           │  │                  │
                      ▼           │  └──ttl expires──▶ expired ──requote──┐
                   error ◀────────┘                     │                 │
                      ▲                                 ▼                 │
                      └──────────────────── rejected ◀──┘◀────────────────┘
```

```ts
type TicketState =
  | { status: 'idle' }
  | { status: 'quoting'; input: TradeInput }
  | { status: 'quoted'; input: TradeInput; quote: Quote; expiresAt: number }
  | { status: 'expired'; input: TradeInput; staleQuote: Quote }
  | { status: 'submitting'; quote: Quote; idempotencyKey: string }
  | { status: 'filled'; fill: Fill }
  | { status: 'rejected'; code: ErrorCode; detail: string; quote?: Quote };
```

Rules:

- **Debounce quoting 300ms** on amount change. Every keystroke hitting `/quotes` will get you rate-limited.
- **Quote TTL** comes from `GET /config` (`quoteTtlSeconds`, default 15). Show a thin brass countdown bar under the preview. On expiry, don't silently re-quote — go to `expired` and require a tap. Someone who walked away shouldn't come back and fill at a price they never saw.
- **Generate the idempotency key when the ticket opens**, not on submit. Same key survives retries, network drops, and impatient double-taps. New key only on a new ticket or an explicit re-quote.
- **Slippage tolerance** defaults to 100bps, adjustable in a disclosure. Show `impact` (the price move this trade causes) prominently — it teaches the mechanic without a tutorial.
- **Disable submit** while `!isActive`, `market.halted`, `!canTrade`, `restrictedFranchiseIds.includes(id)`, or amount `< $1`. Each disabled state shows *why* inline. Never a dead button with no explanation. Delisted tickers are omitted from the board; a stale `/f/:ticker` link shows inactive and a dead ticket.
- **MAX on sell** uses exact share balance from the server, not a rounded display value.

### 5.4 Portfolio

- Equity curve (area chart, `lightweight-charts`) from `/portfolio/history?window=`.
- Cash + holdings table: use server `avgCost`, `mark`, `unrealizedPnl`, `unrealizedPnlPct`, `weight`. Weight is mark ÷ (cash + holdings) on the private book, mark ÷ holdings on the public book.
- Allocation bar — a single horizontal stacked bar rather than a donut. Reads faster and fits mobile.
- `feesPaid` and `realizedPnl` come from the portfolio payload. Show the fees; it's honest and it discourages churn.

### 5.5 Standings

- Rank, trader, total value, window `change`, top holding (`topHoldingTicker` / `topHoldingName`).
- **Caller's row is pinned to the bottom** as a sticky bar when off-screen, showing `callerRank` and `gapAbove` (cash to the rank above). That gap is the whole reason someone opens the app on a Wednesday.
- Windows: `season` / `month` / `week` — pass as `?window=` (season is 120 days of snapshots).

### 5.6 Match night

When any match is live (`market/status.liveMatches`), a persistent banner appears across all routes: matchup, score if available, and a countdown to `nextLockoutAt` / settlement. Tapping it opens `/matches`.

After a settlement batch, a **market movers modal** fires once per session: the split-flap board of what moved, plus the caller's portfolio delta. Dismissible, never repeated, and skipped entirely if the user has no position in any affected franchise.

---

## 6. Data layer

### 6.1 Query keys and freshness

```ts
export const qk = {
  me:              ['me'] as const,
  config:          ['config'] as const,
  franchises:      ['franchises'] as const,
  franchise:       (t: string) => ['franchise', t] as const,
  candles:  (id: number, tf: Timeframe) => ['candles', id, tf] as const,
  events:          (id: number) => ['events', id] as const,
  settlements:     (id: number) => ['settlements', id] as const,
  matches:         (status?: string) => ['matches', status] as const,
  match:           (id: number) => ['match', id] as const,
  portfolio:       ['portfolio'] as const,
  portfolioHistory:(w: Window) => ['portfolio', 'history', w] as const,
  leaderboard:     (w: Window) => ['leaderboard', w] as const,
  orders:          ['orders'] as const,
  marketStatus:    ['market', 'status'] as const,
};
```

| Query | `staleTime` | Refetch | Notes |
|---|---|---|---|
| `me` | ∞ | on focus | Invalidate on restriction change |
| `config` | ∞ | never | Market constants, tier letters, quote TTL |
| `franchises` | 5s | 15s poll | SignalR is primary; poll is the safety net |
| `franchise` | 5s | on focus | Load by **ticker**: `GET /franchises/ATLP` |
| `candles` | 30s | on settlement event | Latest bar patched from `price.updated` |
| `events` / `settlements` | 30s | on `match.settled` | Nested `settlement` is display-ready |
| `matches` | 15s | 15s | `?status=scheduled\|live\|final\|settled` |
| `portfolio` | 0 | on focus | Invalidated on every fill; also patched from `portfolio.updated` |
| `leaderboard` | 60s | 60s | `?window=week\|month\|season` |
| `orders` | 30s | on focus | |

Polling continues alongside SignalR, at a slower cadence. Sockets drop, phones sleep, and a stale price on a trading screen is worse than a redundant request.

### 6.2 Realtime reconciliation

The rule that keeps the UI honest:

```ts
function applyPriceUpdate(qc: QueryClient, ev: PriceUpdated) {
  qc.setQueryData<FranchiseListItem[]>(qk.franchises, (prev) => {
    if (!prev) return prev;
    return prev.map((f) =>
      f.id !== ev.franchiseId ? f
        : ev.seq <= f.seq ? f              // stale — drop it
        : { ...f, price: ev.price, seq: ev.seq }
    );
  });
}
```

**Always compare `seq` before applying.** On reconnect, SignalR may replay an event older than what a REST refetch already delivered. Without the guard, the board flickers backwards and the app lies about the market.

Connection lifecycle:

- `withAutomaticReconnect([0, 2000, 5000, 10000, 30000])`.
- Connection status is visible in the header: `●LIVE` (brass), `●RECONNECTING` (dust, pulsing), `●OFFLINE` (bear). Users watching a match need to know whether they're seeing the real board.
- On `onreconnected`, invalidate `franchises`, `portfolio`, and the open franchise. Don't trust the socket to have caught you up.
- Coalesce inbound `price.updated` through a 250ms rAF batch. During settlement you'll get bursts across ~96 franchises and 96 individual React commits will jank the flip animation.

### 6.3 Trade submission — and why it isn't optimistic

Optimistic updates are wrong here. The fill price is unknowable client-side (the pool may have moved), and showing a share count that then corrects downward is worse than a 400ms spinner.

```ts
const useSubmitOrder = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { quoteId: string; maxSlippageBps: number; idempotencyKey: string }) =>
      api.post('/orders', { quoteId: v.quoteId, maxSlippageBps: v.maxSlippageBps }, {
        headers: { 'Idempotency-Key': v.idempotencyKey },
      }),
    retry: (count, err) => isNetworkError(err) && count < 3,   // safe: idempotent
    onSuccess: (fill) => {
      qc.invalidateQueries({ queryKey: qk.portfolio });
      qc.invalidateQueries({ queryKey: qk.orders });
      qc.invalidateQueries({ queryKey: qk.franchise(fill.ticker) });
      toast.filled(fill);
    },
  });
};
```

Network errors retry automatically — that's what the idempotency key buys. Business rejections (`slippage_exceeded`, `insufficient_funds`) never retry.

The one optimistic touch worth having: on submit, immediately grey the ticket and show the *quoted* numbers with a "confirming" label. It reads as responsive without asserting a result.

### 6.4 Error mapping

Backend `code` → UI copy. Errors explain what happened and what to do; they don't apologize.

| Code | Copy | Action |
|---|---|---|
| `slippage_exceeded` | "Price moved past your limit. It's now $14.41." | `Re-quote` |
| `quote_expired` | "This quote expired." | `Get a new quote` |
| `insufficient_funds` | "You have $182.40 available." | Set amount to max |
| `insufficient_shares` | "You hold 12.4 shares." | Set to max |
| `market_halted` | "Trading is paused until this match settles." | Show countdown |
| `market_delisted` | "This franchise left the league. Your shares were cashed out at the last price." | Back to market |
| `position_cap_exceeded` | "You can hold up to 15% of a franchise. You're at the cap." | — |
| `self_dealing_restricted` | "You can't trade a franchise you're rostered on." | Link to rules |
| `order_too_small` | "Minimum order is $1.00." | — |
| `rate_limited` | "Too many requests. Try again in a few seconds." | Disable 5s |
| unknown 5xx | "Something broke on our end. The trade didn't go through." | `Try again` |

Every one names the state and the fix. None say "an error occurred."

### 6.5 Backend contract (shipped)

Base URL: `http://localhost:5233/api/v1`. Hub: `http://localhost:5233/hub/market`. JSON is camelCase. **Money, shares, prices, and rates are strings.** Rates (`change24h`, `impact`, `unrealizedPnlPct`, `weight`, `positionCapPct`, leaderboard `change`) are **fractions**, not percents: `"0.084000"` displays as ▲ 8.4%.

| Job | Endpoint |
|---|---|
| Public constants, tier letters, quote TTL | `GET /config` |
| Board | `GET /franchises` — array, not `{ items }`. Includes `haltReason`, `resumesAt`, `spark[]` |
| Franchise by ticker (route `/f/:ticker`) | `GET /franchises/{ticker}` e.g. `/franchises/ATLP` |
| Franchise by id | `GET /franchises/{id}` |
| Chart | `GET /franchises/{id}/candles?tf=5m\|1h\|1d` |
| News | `GET /franchises/{id}/events` — `settlement` nested when kind is settlement/correction |
| Settlement math (popover) | `GET /franchises/{id}/settlements` |
| Schedule | `GET /matches?status=&cursor=&limit=` and `GET /matches/{id}`. `lockoutAt` = `scheduledAt − haltPreMatchMinutes` |
| Live banner | `GET /market/status` — `liveMatches[]`, `nextLockoutAt` |
| Quote | `POST /quotes` → includes `impact` |
| Fill | `POST /orders` + `Idempotency-Key`; response includes `ticker` |
| Portfolio | `GET /portfolio` — `feesPaid`, `realizedPnl`; holdings have `avgCost`, `unrealizedPnlPct`, `weight` |
| Standings | `GET /leaderboard?window=week\|month\|season` — `callerRank`, `gapAbove`, row `change` + `topHoldingTicker` |
| Public book | `GET /users/{id}/portfolio` — holdings only, cash omitted |

**Display-only vs server-only**

- Safe to format on the client: `impact`, `avgCost`, `weight`, `unrealizedPnlPct`, `change24h` (already computed).
- Never recompute: AMM fill, fees, realized P/L, week/season standings change, shock / expected / surprise. Read them.

**SignalR events**

| Event | Who | Payload |
|---|---|---|
| `price.updated` | all | `{ franchiseId, price, prevPrice, seq, at }` |
| `market.halted` | all | `{ franchiseId, halted, reason, resumesAt }` |
| `match.settled` | all | `{ matchId, map, roundsHome, roundsAway, franchises: [{ franchiseId, ticker, name, expectedMargin, actualMargin, surprise, shock, priceBefore, priceAfter, seq }] }` |
| `trade.filled` | caller | `{ orderId, franchiseId, ticker, side, shares, cash, price }` |
| `portfolio.updated` | caller | full `PortfolioResponse` (cash, holdings, fees, realized) |

On `match.settled`, flip only the tickers in `franchises` (two per match, not the whole board). Invalidate `matches`, `settlements`, `events`, and the two franchise details.

**Tickers:** `{PREFIX}{tierLetter}` — P Premier, E Elite, C Challenger, N Contender, S Prospect, R Recruit. Example `ATLP`. Universe is ~96 names.

---

## 7. Formatting

Centralized, tested, used everywhere. Getting this wrong is how a play-money game starts feeling fake.

```ts
export const fmt = {
  // "$14.20" — always 2dp, always tabular
  price: (v: Money) => usd.format(Number(v)),

  // "$2,481.20"
  cash: (v: Money) => usd.format(Number(v)),

  // "142.5600" → "142.56" · trailing zeros trimmed, max 4dp
  shares: (v: Shares) => trimZeros(Number(v).toFixed(4)),

  // "▲ 8.4%" / "▼ 3.1%" / "—" — API sends a fraction string ("0.084000")
  change: (v: string | number) => {
    const n = typeof v === 'string' ? Number(v) : v;
    return n === 0 ? '—' : `${n > 0 ? '▲' : '▼'} ${Math.abs(n * 100).toFixed(1)}%`;
  },

  // "+$412.80" / "−$88.10" — U+2212 minus, not hyphen
  pnl: (v: Money) => (Number(v) >= 0 ? '+' : '−') + usd.format(Math.abs(Number(v))),
};
```

- `Number()` conversion is **display only**. Never feed it back into a request.
- Use U+2212 MINUS SIGN in output, not a hyphen. It aligns with digits in a mono face.
- Direction is always encoded by **glyph and sign**, not color alone (§8).
- Countdowns: `2h 14m` above an hour, `14:32` under, `LOCKED` at zero.

---

## 8. Accessibility

- **Never color-only.** Every up/down carries ▲/▼ and a sign. Roughly 8% of your male audience has some form of red-green deficiency, and this app is built on red and green.
- Fill confirmations announce via `aria-live="polite"`. Rejections via `role="alert"`.
- Trade ticket is a proper focus trap; ESC closes; focus returns to the trigger.
- Visible brass focus rings, `:focus-visible`, never `outline: none`.
- The split-flap respects `prefers-reduced-motion` (§3).
- Chart data is available as an accessible table behind a "View as table" toggle — canvas is invisible to screen readers.
- 44px minimum touch targets. The Buy button on mobile is 52px.

---

## 9. Performance

- **Virtualize the market list** past 30 rows (`@tanstack/react-virtual`).
- Sparklines are memoized inline SVG keyed on `(franchiseId, seq)`. No chart instances in rows.
- One `lightweight-charts` instance per page, disposed on unmount. Leaking these will eat a phone alive.
- Batch socket updates through rAF (§6.2).
- Route-level code splitting; the chart library loads only on `/f/:ticker` and `/portfolio`.
- Self-host fonts, `font-display: swap`, subset to Latin. Three families is already the budget — no additional weights without cutting one.
- Targets: LCP < 1.8s on 4G, market list interaction < 100ms, settlement burst renders without a dropped frame on a mid-range Android.

---

## 10. Component inventory

```
app/
  layout/         AppShell · TopBar · ConnectionStatus · MobileTabBar · MatchBanner
  market/         SplitFlapTicker · MarketTable · MarketRow · Sparkline · FilterBar
  franchise/      FranchiseHeader · PriceChart · SettlementMarker · SettlementPopover
                  FairValueLine · NewsFeed · NewsItem
  trade/          TradeTicket · SideToggle · AmountInput · QuickAmounts
                  QuotePreview · QuoteTimer · SlippageControl · SubmitButton
                  FillToast · RejectNotice
  portfolio/      EquityCurve · HoldingsTable · HoldingRow · AllocationBar · PnlStat
  leaderboard/    StandingsTable · StandingsRow · SelfRankBar
  matches/        MatchList · MatchCard · LockoutCountdown
  common/         Money · Shares · Change · Ticker · Panel · Sheet · Empty · Boundary
```

`<Money>`, `<Shares>`, `<Change>`, `<Ticker>` are the load-bearing primitives. Every number on screen goes through one of them — that's how formatting, tabular figures, and the color/glyph rules stay consistent without discipline.

---

## 11. Testing

**Unit (Vitest)** — formatters against a fixture table including negatives, zero, and very large values. Ticket state machine transitions, especially expiry mid-submit.

**Component (RTL + MSW)** —
- Every backend error code renders its mapped copy and action.
- Quote expiry disables submit and offers re-quote.
- Halted franchise disables the ticket with the reason visible.
- A stale `seq` price update is dropped.
- Reconnect triggers invalidation.

**E2E (Playwright)** —
- Login → open franchise → quote → fill → portfolio reflects the position.
- Double-click submit produces exactly one order (assert on network calls).
- Settlement over the socket flips the ticker and updates the row.
- Full mobile trade flow on a 390px viewport.

**Visual** — Playwright screenshots of Market, Franchise, and the ticket in each state. Prevents the design direction from quietly eroding into the default dark dashboard.

---

## 12. Build order

| Phase | Ships | Depends on |
|---|---|---|
| **0** | Design tokens, primitives, generated API types, MSW fixtures | Backend OpenAPI doc |
| **1** | Auth, app shell, Market table (polling only) | Backend phase 3 |
| **2** | Franchise page, chart, trade ticket end-to-end | |
| **3** | Portfolio, standings | |
| **4** | SignalR, split-flap ticker, settlement markers | Backend settlement |
| **5** | Match night banner, movers modal, mobile polish | |

Phases 0–3 run entirely against MSW with no backend. Build the frontend against fixtures in parallel with the API — the trade ticket is the hardest component here and it deserves more iteration than a serial schedule would allow.

---

## 13. Open questions

1. **Does the split-flap survive contact with reality?** It's the signature element and the biggest animation risk. Prototype it in phase 0 against a **~96-ticker** board with a two-name settlement burst (typical) and a worst-case multi-match night. If it can't hold 60fps on a mid-range phone, cut it to a staggered cross-fade and spend the boldness on the settlement popover instead.
2. **Sell flow input mode.** Shares or dollars? Buying in dollars is obviously right. Selling is genuinely ambiguous — a share-count input matches the mental model of "sell my position," a dollar input matches "take $200 off the table." Ship shares with quick 25/50/100% buttons, revisit after preseason.
3. **How much math to surface by default.** The settlement popover has expected margin, actual, surprise, and shock. That's four numbers and possibly three too many for a casual user. Consider a one-line plain-English summary with the math behind a disclosure.
4. **Dark only, or a light board variant?** The direction is built for dark. A light variant would need a genuinely different treatment (paper stock rather than enamel) and isn't a token swap. Defer.
5. **Discord embed parity.** Most people will experience the market through the digest post, not the site. Worth generating shareable OG images for franchise and portfolio pages so links unfurl as board panels rather than blank cards.
