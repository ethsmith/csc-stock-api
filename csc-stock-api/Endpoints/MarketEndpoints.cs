using Csx.Api.Contracts;
using Csx.Domain;
using Csx.Domain.Config;
using Csx.Domain.Shock;
using Csx.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Csx.Api.Endpoints;

public static class MarketEndpoints
{
    public static IEndpointRouteBuilder MapMarketEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1").WithTags("Market");

        g.MapGet("/config", (
            IOptions<MarketOptions> market,
            IOptions<ShockOptions> shock,
            IOptions<BreakerOptions> breaker,
            IOptions<DecayOptions> decay,
            IOptions<HaltOptions> halt,
            IOptions<QuoteOptions> quote,
            IOptions<CscCoreOptions> core) =>
        {
            var m = market.Value;
            var s = shock.Value;
            var b = breaker.Value;
            var d = decay.Value;
            var tiers = Tickers.TierLetters
                .Select(kv => new TierDto(kv.Key, kv.Value.ToString()))
                .ToList();
            return Results.Ok(new MarketConfigDto(
                MoneyFormat.Cash(m.StartingCash),
                MoneyFormat.Price(m.InitialPrice),
                MoneyFormat.Shares(m.TotalSupply),
                m.FeeBps,
                MoneyFormat.Cash(m.MinOrderCash),
                MoneyFormat.Pct(m.PositionCapPct),
                s.Mode,
                s.Alpha.ToString("0.00"),
                s.Beta.ToString("0.00"),
                s.EloDivisor.ToString("0.00"),
                MoneyFormat.Pct(b.DailyMovePct),
                (d.IsActive ? d.Lambda : 0m).ToString("0.0000"),
                MoneyFormat.Price(d.PriceFloor),
                MoneyFormat.Price(d.PriceCeiling),
                halt.Value.PreMatchMinutes,
                quote.Value.TtlSeconds,
                core.Value.Season,
                tiers));
        }).AllowAnonymous();

        g.MapGet("/franchises", async (CsxDbContext db, IMemoryCache cache, CancellationToken ct) =>
        {
            if (cache.TryGetValue("franchises", out IReadOnlyList<FranchiseListItem>? cached) && cached is not null)
                return Results.Ok(cached);

            var list = await BuildFranchiseList(db, ct);
            cache.Set("franchises", list, TimeSpan.FromSeconds(5));
            return Results.Ok(list);
        }).AllowAnonymous();

        g.MapGet("/franchises/{id:long}", async (
            long id,
            CsxDbContext db,
            IOptions<MarketOptions> market,
            IOptions<DecayOptions> decay,
            IOptions<HaltOptions> halt,
            CancellationToken ct) =>
        {
            var dto = await BuildDetail(db, id, market.Value, decay.Value, halt.Value.PreMatchMinutes, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        }).AllowAnonymous();

        g.MapGet("/franchises/{ticker}", async (
            string ticker,
            CsxDbContext db,
            IOptions<MarketOptions> market,
            IOptions<DecayOptions> decay,
            IOptions<HaltOptions> halt,
            CancellationToken ct) =>
        {
            var id = await db.Franchises
                .Where(f => f.Ticker == ticker.ToUpperInvariant())
                .Select(f => (long?)f.Id)
                .SingleOrDefaultAsync(ct);
            if (id is null) return Results.NotFound();
            var dto = await BuildDetail(db, id.Value, market.Value, decay.Value, halt.Value.PreMatchMinutes, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        }).AllowAnonymous();

        g.MapGet("/franchises/{id:long}/candles", async (
            long id, string? tf, DateTimeOffset? from, DateTimeOffset? to, CsxDbContext db, CancellationToken ct) =>
        {
            tf = tf is "1h" or "1d" ? tf : "5m";
            var start = from ?? DateTimeOffset.UtcNow.AddDays(-7);
            var end = to ?? DateTimeOffset.UtcNow;
            var rows = await db.Candles
                .Where(c => c.FranchiseId == id && c.Timeframe == tf && c.Bucket >= start && c.Bucket <= end)
                .OrderBy(c => c.Bucket)
                .Take(1000)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(c => new CandleDto(
                c.Bucket,
                MoneyFormat.Price(c.Open),
                MoneyFormat.Price(c.High),
                MoneyFormat.Price(c.Low),
                MoneyFormat.Price(c.Close),
                MoneyFormat.Cash(c.VolumeCash))));
        }).AllowAnonymous();

        g.MapGet("/franchises/{id:long}/events", async (long id, long? cursor, CsxDbContext db, CancellationToken ct) =>
        {
            var q = db.MarketEvents.Where(e => e.FranchiseId == id);
            if (cursor is { } c) q = q.Where(e => e.Id < c);
            var rows = await q.OrderByDescending(e => e.Id).Take(50).ToListAsync(ct);
            return Results.Ok(rows.Select(e => new MarketEventDto(
                e.Id, e.Kind, e.Headline, e.At, ContractMapper.TryParseSettlement(e.PayloadJson))));
        }).AllowAnonymous();

        g.MapGet("/franchises/{id:long}/settlements", async (long id, CsxDbContext db, CancellationToken ct) =>
        {
            var ticker = await db.Franchises.Where(f => f.Id == id).Select(f => f.Ticker).SingleOrDefaultAsync(ct);
            if (ticker is null) return Results.NotFound();
            var rows = await db.Settlements
                .Where(s => s.FranchiseId == id)
                .OrderByDescending(s => s.CreatedAt)
                .Take(200)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(s => ContractMapper.ToSettlement(s, ticker)));
        }).AllowAnonymous();

        g.MapGet("/market/movers", async (string? window, CsxDbContext db, CancellationToken ct) =>
        {
            var hours = window == "7d" ? 168 : 24;
            var cutoff = DateTimeOffset.UtcNow.AddHours(-hours);
            var franchises = await db.Franchises.Include(f => f.Pool).Where(f => f.IsActive).ToListAsync(ct);
            var prevTicks = await db.PriceTicks.Where(t => t.At <= cutoff)
                .GroupBy(t => t.FranchiseId)
                .Select(g => new { g.Key, Price = g.OrderByDescending(x => x.At).Select(x => x.Price).First() })
                .ToListAsync(ct);
            var prev = prevTicks.ToDictionary(x => x.Key, x => x.Price);

            var movers = franchises
                .Where(f => f.Pool is not null)
                .Select(f =>
                {
                    var price = f.Pool!.Price;
                    prev.TryGetValue(f.Id, out var p0);
                    if (p0 == 0m) p0 = price;
                    var change = p0 == 0m ? 0m : (price - p0) / p0;
                    return new { f, price, change };
                })
                .OrderByDescending(x => Math.Abs(x.change))
                .Take(20)
                .Select(x => new MoverDto(x.f.Id, x.f.Ticker, x.f.Name, MoneyFormat.Price(x.price), MoneyFormat.Pct(x.change)))
                .ToList();
            return Results.Ok(movers);
        }).AllowAnonymous();

        g.MapGet("/market/status", async (
            CsxDbContext db,
            IOptions<HaltOptions> halt,
            CancellationToken ct) =>
        {
            var pools = await db.Pools.Include(p => p.Franchise)
                .Where(p => p.Franchise.IsActive)
                .ToListAsync(ct);
            var next = await db.Matches
                .Where(m => m.Status == MatchStatuses.Scheduled && m.ScheduledAt != null)
                .OrderBy(m => m.ScheduledAt)
                .Select(m => m.ScheduledAt)
                .FirstOrDefaultAsync(ct);
            DateTimeOffset? nextLockout = next is { } at
                ? at.AddMinutes(-halt.Value.PreMatchMinutes)
                : null;
            var live = await db.Matches
                .Include(m => m.TeamA)
                .Include(m => m.TeamB)
                .Where(m => m.Status == MatchStatuses.Live)
                .OrderBy(m => m.ScheduledAt)
                .Take(20)
                .ToListAsync(ct);
            return Results.Ok(new MarketStatusDto(
                pools.Count > 0 && pools.All(p => p.IsHalted),
                pools.Count(p => p.IsHalted),
                pools.Count(p => !p.IsHalted),
                nextLockout,
                live.Select(m => new LiveMatchDto(
                    m.Id, m.Status, m.ScheduledAt, m.TeamA.Ticker, m.TeamB.Ticker, m.RoundsA, m.RoundsB)).ToList()));
        }).AllowAnonymous();

        return app;
    }

    private static async Task<List<FranchiseListItem>> BuildFranchiseList(CsxDbContext db, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        var rows = await db.Franchises
            .Include(f => f.Pool)
            .Include(f => f.Org)
            .Where(f => f.IsActive)
            .OrderBy(f => f.Ticker)
            .ToListAsync(ct);

        var ticks = await db.PriceTicks
            .Where(t => t.At <= cutoff)
            .GroupBy(t => t.FranchiseId)
            .Select(g => new { FranchiseId = g.Key, Price = g.OrderByDescending(x => x.At).Select(x => x.Price).First() })
            .ToListAsync(ct);
        var prev = ticks.ToDictionary(t => t.FranchiseId, t => t.Price);

        var sparks = await db.Candles
            .Where(c => (c.Timeframe == "1h" || c.Timeframe == "5m") && c.Bucket >= cutoff)
            .OrderBy(c => c.Bucket)
            .ToListAsync(ct);
        var sparkById = sparks
            .GroupBy(c => c.FranchiseId)
            .ToDictionary(g => g.Key, PickSpark);

        var recentTicks = await db.PriceTicks
            .Where(t => t.At >= cutoff)
            .OrderBy(t => t.At)
            .Select(t => new { t.FranchiseId, t.Price })
            .ToListAsync(ct);
        var tickSparkById = recentTicks
            .GroupBy(t => t.FranchiseId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(t => MoneyFormat.Price(t.Price)).ToList());

        return rows.Select(f =>
        {
            var pool = f.Pool;
            var price = pool is null || pool.ShareReserve == 0 ? 0m : pool.CashReserve / pool.ShareReserve;
            prev.TryGetValue(f.Id, out var p24);
            if (p24 == 0m) p24 = price;
            var change = p24 == 0m ? 0m : (price - p24) / p24;
            var cap = price * (pool?.TotalSupply ?? 0);
            sparkById.TryGetValue(f.Id, out var spark);
            if ((spark is null || spark.Count < 2) && tickSparkById.TryGetValue(f.Id, out var fromTicks) && fromTicks.Count >= 2)
                spark = fromTicks;
            if (spark is null || spark.Count < 2)
                spark = [MoneyFormat.Price(price), MoneyFormat.Price(price)];
            return new FranchiseListItem(
                f.Id, f.Ticker, f.Name, f.Org?.Name, f.Division, f.IsActive,
                pool?.IsHalted ?? false,
                pool?.HaltReason,
                pool?.ResumesAt,
                MoneyFormat.Price(price),
                MoneyFormat.Pct(change),
                MoneyFormat.Cash(cap),
                pool?.Seq ?? 0,
                spark);
        }).ToList();
    }

    private static IReadOnlyList<string> PickSpark(IGrouping<long, Candle> g)
    {
        var hourly = g.Where(c => c.Timeframe == "1h").Select(c => MoneyFormat.Price(c.Close)).ToList();
        var five = g.Where(c => c.Timeframe == "5m").Select(c => MoneyFormat.Price(c.Close)).ToList();
        if (hourly.Count >= 2) return hourly;
        if (five.Count >= 2) return five;
        if (hourly.Count > 0) return hourly;
        return five;
    }

    private static async Task<FranchiseDetail?> BuildDetail(
        CsxDbContext db, long id, MarketOptions market, DecayOptions decay, int preMatchMinutes, CancellationToken ct)
    {
        var f = await db.Franchises
            .Include(x => x.Pool)
            .Include(x => x.Org)
            .Include(x => x.Roster)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (f?.Pool is null) return null;

        var mean = await db.Franchises.Where(x => x.IsActive).AverageAsync(x => (decimal?)x.Elo, ct) ?? 1000m;
        var fund = ShockMath.Fundamental(
            market.InitialPrice, f.Elo, mean, decay.Kappa, decay.PriceFloor, decay.PriceCeiling);

        var next = await db.Matches
            .Where(m => (m.FranchiseA == id || m.FranchiseB == id) && m.Status == MatchStatuses.Scheduled)
            .OrderBy(m => m.ScheduledAt)
            .FirstOrDefaultAsync(ct);
        FranchiseMatchSummary? nextDto = null;
        if (next is not null)
        {
            var oppId = next.FranchiseA == id ? next.FranchiseB : next.FranchiseA;
            var opp = await db.Franchises.FindAsync([oppId], ct);
            DateTimeOffset? lockout = next.ScheduledAt is { } at ? at.AddMinutes(-preMatchMinutes) : null;
            nextDto = new FranchiseMatchSummary(next.Id, next.ScheduledAt, lockout, opp?.Ticker ?? "?", opp?.Name ?? "?");
        }

        return new FranchiseDetail(
            f.Id, f.Ticker, f.Name, f.Org?.Name, f.Org?.LogoUrl, f.Division, f.IsActive,
            f.Pool.IsHalted, f.Pool.HaltReason, f.Pool.ResumesAt,
            MoneyFormat.Price(f.Pool.Price),
            MoneyFormat.Cash(f.Pool.CashReserve),
            MoneyFormat.Shares(f.Pool.ShareReserve),
            MoneyFormat.Shares(f.Pool.TotalSupply),
            MoneyFormat.Price(fund),
            f.Elo.ToString("0.00"),
            f.Pool.Seq,
            nextDto,
            f.Roster.Select(r => new RosterPlayer(r.PlayerName, r.DiscordId, r.PlayerType)).ToList());
    }
}
