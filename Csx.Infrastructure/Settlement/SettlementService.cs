using Csx.Domain.Amm;
using Csx.Domain.Config;
using Csx.Domain.Errors;
using Csx.Domain.Ledger;
using Csx.Domain.Shock;
using Csx.Infrastructure.Data;
using Csx.Infrastructure.Ledger;
using SettlementRow = Csx.Infrastructure.Data.Settlement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Csx.Infrastructure.Settlement;

public sealed record SettlementBatchResult(
    long MatchId,
    IReadOnlyList<SettlementRow> Settlements,
    bool AlreadySettled);

public sealed class SettlementService
{
    private readonly CsxDbContext _db;
    private readonly LedgerService _ledger;
    private readonly MarketOptions _market;
    private readonly ShockOptions _shock;
    private readonly BreakerOptions _breaker;
    private readonly ILogger<SettlementService> _log;

    public SettlementService(
        CsxDbContext db,
        LedgerService ledger,
        IOptions<MarketOptions> market,
        IOptions<ShockOptions> shock,
        IOptions<BreakerOptions> breaker,
        ILogger<SettlementService> log)
    {
        _db = db;
        _ledger = ledger;
        _market = market.Value;
        _shock = shock.Value;
        _breaker = breaker.Value;
        _log = log;
    }

    public async Task HaltMatchPoolsAsync(long matchId, string reason, DateTimeOffset? resumesAt, CancellationToken ct)
    {
        var match = await _db.Matches.SingleOrDefaultAsync(m => m.Id == matchId, ct)
                    ?? throw new MarketException(ErrorCodes.MatchNotFound, "Match not found.", 404);
        await HaltFranchiseAsync(match.FranchiseA, reason, resumesAt, ct);
        await HaltFranchiseAsync(match.FranchiseB, reason, resumesAt, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task HaltFranchiseAsync(long franchiseId, string reason, DateTimeOffset? resumesAt, CancellationToken ct)
    {
        var pool = await _db.Pools.SingleOrDefaultAsync(p => p.FranchiseId == franchiseId, ct);
        if (pool is null) return;
        if (pool.IsHalted && pool.HaltReason == reason)
        {
            pool.ResumesAt = resumesAt ?? pool.ResumesAt;
            return;
        }
        pool.IsHalted = true;
        pool.HaltReason = reason;
        pool.ResumesAt = resumesAt;
        pool.UpdatedAt = DateTimeOffset.UtcNow;
        _db.MarketEvents.Add(new MarketEvent
        {
            FranchiseId = franchiseId,
            Kind = EventKinds.Halt,
            Headline = reason,
            At = DateTimeOffset.UtcNow
        });
    }

    public async Task ResumeFranchiseAsync(long franchiseId, CancellationToken ct)
    {
        var pool = await _db.Pools.SingleOrDefaultAsync(p => p.FranchiseId == franchiseId, ct);
        if (pool is null) return;
        pool.IsHalted = false;
        pool.HaltReason = null;
        pool.ResumesAt = null;
        pool.UpdatedAt = DateTimeOffset.UtcNow;
        _db.MarketEvents.Add(new MarketEvent
        {
            FranchiseId = franchiseId,
            Kind = EventKinds.Resume,
            Headline = "Market resumed",
            At = DateTimeOffset.UtcNow
        });
    }

    public async Task<SettlementBatchResult> SettleMatchAsync(long matchId, CancellationToken ct, bool isCorrection = false, long? correctsId = null)
    {
        var match = await _db.Matches
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .SingleOrDefaultAsync(m => m.Id == matchId, ct)
            ?? throw new MarketException(ErrorCodes.MatchNotFound, "Match not found.", 404);

        if (!isCorrection)
        {
            var already = await _db.Settlements
                .Where(s => s.MatchId == matchId && !s.IsCorrection)
                .ToListAsync(ct);
            if (already.Count > 0)
                return new SettlementBatchResult(matchId, already, true);
        }

        if (match.RoundsA is null || match.RoundsB is null)
            throw new MarketException(ErrorCodes.MatchNotFound, "Match has no round scores.", 400);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var poolA = await _db.LockPoolAsync(match.FranchiseA, ct);
        var poolB = await _db.LockPoolAsync(match.FranchiseB, ct);

        var eloA = match.TeamA.Elo;
        var eloB = match.TeamB.Elo;
        _db.EloSnapshots.Add(new EloSnapshot { FranchiseId = match.FranchiseA, Elo = eloA, MatchId = match.Id, At = DateTimeOffset.UtcNow });
        _db.EloSnapshots.Add(new EloSnapshot { FranchiseId = match.FranchiseB, Elo = eloB, MatchId = match.Id, At = DateTimeOffset.UtcNow });

        var maps = Math.Max(1, ShockMath.EstimateMapCount(match.IsBo3, match.RoundsA ?? 0, match.RoundsB ?? 0));
        var actualA = match.RoundsA.Value - match.RoundsB.Value;
        var actualB = -actualA;

        var mode = _shock.Mode.Equals("SignedScaled", StringComparison.OrdinalIgnoreCase)
            ? ShockMode.SignedScaled
            : ShockMode.Surprise;

        var sA = await ApplySideAsync(match, poolA, eloA, eloB, actualA, maps, mode, isCorrection, correctsId, ct);
        var sB = await ApplySideAsync(match, poolB, eloB, eloA, actualB, maps, mode, isCorrection, correctsId, ct);

        var (eloAAfter, eloBAfter) = ShockMath.EloAfter(match.TeamA.Elo, match.TeamB.Elo, actualA > 0, actualA == 0);
        match.TeamA.Elo = eloAAfter;
        match.TeamB.Elo = eloBAfter;

        match.Status = MatchStatuses.Settled;
        await ResumeFranchiseAsync(match.FranchiseA, ct);
        await ResumeFranchiseAsync(match.FranchiseB, ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _log.LogInformation(
            "Settled match {MatchId}: A shock {ShockA} {BeforeA}->{AfterA}; B shock {ShockB} {BeforeB}->{AfterB}",
            match.Id, sA.ShockApplied, sA.PriceBefore, sA.PriceAfter, sB.ShockApplied, sB.PriceBefore, sB.PriceAfter);

        return new SettlementBatchResult(match.Id, [sA, sB], false);
    }

    private async Task<SettlementRow> ApplySideAsync(
        LeagueMatch match,
        Pool pool,
        decimal elo,
        decimal oppElo,
        int actualMargin,
        int maps,
        ShockMode mode,
        bool isCorrection,
        long? correctsId,
        CancellationToken ct)
    {
        var expected = ShockMath.ExpectedMargin(elo, oppElo, _shock.EloDivisor) * maps;
        var surprise = actualMargin - expected;
        var won = actualMargin > 0;
        var shockRaw = ShockMath.ComputeShock(mode, surprise, won, _shock.Alpha, _shock.Beta);

        var price = AmmMath.Price(pool.CashReserve, pool.ShareReserve);
        var price24h = await Price24hAgoAsync(pool.FranchiseId, price, ct);
        var target = price * (1m + shockRaw);
        var final = ShockMath.ApplyCircuitBreaker(price, price24h, target, _breaker.DailyMovePct);
        var clamped = final != target;
        var applied = price == 0m ? 0m : (final / price) - 1m;
        var delta = (final * pool.ShareReserve) - pool.CashReserve;

        await _ledger.PostAsync(
            EntryKinds.Revalue,
            "settlement",
            match.Id,
            [
                new PostingDraft(OwnerTypes.Mint, null, AssetTypes.Cash, null, -delta),
                new PostingDraft(OwnerTypes.Pool, pool.FranchiseId, AssetTypes.Cash, null, delta)
            ],
            ct);

        pool.CashReserve += delta;
        pool.Seq += 1;
        pool.UpdatedAt = DateTimeOffset.UtcNow;

        var row = new SettlementRow
        {
            MatchId = match.Id,
            FranchiseId = pool.FranchiseId,
            EloBefore = elo,
            OppEloBefore = oppElo,
            ExpectedMargin = expected,
            ActualMargin = actualMargin,
            Surprise = surprise,
            ShockRaw = shockRaw,
            ShockApplied = applied,
            ShockClamped = clamped,
            PriceBefore = price,
            PriceAfter = final,
            IsCorrection = isCorrection,
            CorrectsId = correctsId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Settlements.Add(row);

        _db.PriceTicks.Add(new PriceTick
        {
            FranchiseId = pool.FranchiseId,
            Price = final,
            Seq = pool.Seq,
            Source = TickSources.Settlement,
            RefId = match.Id,
            At = DateTimeOffset.UtcNow
        });

        var ticker = pool.Franchise?.Ticker
                     ?? await _db.Franchises.Where(f => f.Id == pool.FranchiseId).Select(f => f.Ticker).SingleAsync(ct);

        var ev = new MarketEvent
        {
            FranchiseId = pool.FranchiseId,
            Kind = isCorrection ? EventKinds.Correction : EventKinds.Settlement,
            Headline = FormatHeadline(ticker, surprise, applied),
            At = DateTimeOffset.UtcNow
        };
        _db.MarketEvents.Add(ev);
        await _db.SaveChangesAsync(ct);

        ev.PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = row.Id,
            matchId = row.MatchId,
            franchiseId = row.FranchiseId,
            ticker,
            eloBefore = row.EloBefore.ToString("0.00"),
            oppEloBefore = row.OppEloBefore.ToString("0.00"),
            expectedMargin = row.ExpectedMargin.ToString("0.0000"),
            actualMargin = row.ActualMargin,
            surprise = row.Surprise.ToString("0.0000"),
            shockRaw = row.ShockRaw.ToString("0.000000"),
            shockApplied = row.ShockApplied.ToString("0.000000"),
            shockClamped = row.ShockClamped,
            priceBefore = row.PriceBefore.ToString("0.0000"),
            priceAfter = row.PriceAfter.ToString("0.0000"),
            isCorrection = row.IsCorrection,
            at = row.CreatedAt
        });
        await _db.SaveChangesAsync(ct);
        return row;
    }

    private static string FormatHeadline(string ticker, decimal surprise, decimal shock)
    {
        var pct = shock * 100m;
        var dir = shock >= 0 ? "up" : "down";
        return $"{ticker} settled: surprise {surprise:+0.0;-0.0}, price {dir} {Math.Abs(pct):0.00}%";
    }

    private async Task<decimal> Price24hAgoAsync(long franchiseId, decimal fallback, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        var tick = await _db.PriceTicks
            .Where(t => t.FranchiseId == franchiseId && t.At <= cutoff)
            .OrderByDescending(t => t.At)
            .FirstOrDefaultAsync(ct);
        return tick?.Price ?? fallback;
    }
}
