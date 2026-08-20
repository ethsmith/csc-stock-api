using Csx.Domain.Config;
using Csx.Domain.Ledger;
using Csx.Domain.Shock;
using Csx.Infrastructure.Data;
using Csx.Infrastructure.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Csx.Infrastructure.Market;

public sealed class MarketOpsService
{
    private readonly CsxDbContext _db;
    private readonly LedgerService _ledger;
    private readonly MarketOptions _market;
    private readonly DecayOptions _decay;

    public MarketOpsService(
        CsxDbContext db,
        LedgerService ledger,
        IOptions<MarketOptions> market,
        IOptions<DecayOptions> decay)
    {
        _db = db;
        _ledger = ledger;
        _market = market.Value;
        _decay = decay.Value;
    }

    public async Task SeedPoolAsync(Franchise franchise, CancellationToken ct)
    {
        if (await _db.Pools.AnyAsync(p => p.FranchiseId == franchise.Id, ct))
            return;

        var supply = _market.TotalSupply;
        var cash = _market.InitialPrice * supply;

        await _ledger.GetOrCreateAccountAsync(OwnerTypes.Supply, franchise.Id, AssetTypes.Share, franchise.Id, ct);
        await _ledger.GetOrCreateAccountAsync(OwnerTypes.Pool, franchise.Id, AssetTypes.Share, franchise.Id, ct);
        await _ledger.GetOrCreateAccountAsync(OwnerTypes.Pool, franchise.Id, AssetTypes.Cash, null, ct);

        var pool = new Pool
        {
            FranchiseId = franchise.Id,
            CashReserve = cash,
            ShareReserve = supply,
            TotalSupply = supply,
            Seq = 0,
            IsHalted = false,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _db.Pools.Add(pool);
        await _db.SaveChangesAsync(ct);

        await _ledger.PostAsync(
            EntryKinds.PoolSeed,
            "franchise",
            franchise.Id,
            [
                new PostingDraft(OwnerTypes.Supply, franchise.Id, AssetTypes.Share, franchise.Id, -supply),
                new PostingDraft(OwnerTypes.Pool, franchise.Id, AssetTypes.Share, franchise.Id, supply),
                new PostingDraft(OwnerTypes.Mint, null, AssetTypes.Cash, null, -cash),
                new PostingDraft(OwnerTypes.Pool, franchise.Id, AssetTypes.Cash, null, cash)
            ],
            ct);

        _db.PriceTicks.Add(new PriceTick
        {
            FranchiseId = franchise.Id,
            Price = _market.InitialPrice,
            Seq = 0,
            Source = TickSources.Admin,
            At = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task GrantSignupCashAsync(long userId, CancellationToken ct)
    {
        var existing = await _db.Entries.AnyAsync(
            e => e.Kind == EntryKinds.SignupGrant && e.RefType == "user" && e.RefId == userId, ct);
        if (existing) return;

        await _ledger.PostAsync(
            EntryKinds.SignupGrant,
            "user",
            userId,
            [
                new PostingDraft(OwnerTypes.Mint, null, AssetTypes.Cash, null, -_market.StartingCash),
                new PostingDraft(OwnerTypes.User, userId, AssetTypes.Cash, null, _market.StartingCash)
            ],
            ct);
    }

    public async Task DecayTickAsync(CancellationToken ct)
    {
        if (!_decay.IsActive) return;

        var meanElo = await _db.Franchises.Where(f => f.IsActive).AverageAsync(f => (decimal?)f.Elo, ct) ?? 1000m;
        var pools = await _db.Pools.Include(p => p.Franchise)
            .Where(p => !p.IsHalted && p.Franchise.IsActive)
            .ToListAsync(ct);

        foreach (var pool in pools)
        {
            var price = pool.ShareReserve == 0 ? 0 : pool.CashReserve / pool.ShareReserve;
            var f = ShockMath.Fundamental(
                _market.InitialPrice,
                pool.Franchise.Elo,
                meanElo,
                _decay.Kappa,
                _decay.PriceFloor,
                _decay.PriceCeiling);
            var next = ShockMath.DecayTick(price, f, _decay.Lambda);
            var delta = (next * pool.ShareReserve) - pool.CashReserve;
            if (delta == 0m) continue;

            await _ledger.PostAsync(
                EntryKinds.Revalue,
                "decay",
                pool.FranchiseId,
                [
                    new PostingDraft(OwnerTypes.Mint, null, AssetTypes.Cash, null, -delta),
                    new PostingDraft(OwnerTypes.Pool, pool.FranchiseId, AssetTypes.Cash, null, delta)
                ],
                ct);

            pool.CashReserve += delta;
            pool.Seq += 1;
            pool.UpdatedAt = DateTimeOffset.UtcNow;
            _db.PriceTicks.Add(new PriceTick
            {
                FranchiseId = pool.FranchiseId,
                Price = next,
                Seq = pool.Seq,
                Source = TickSources.Decay,
                At = DateTimeOffset.UtcNow
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task ForcedLiquidateAsync(long userId, long franchiseId, CancellationToken ct)
    {
        var holding = await _db.Holdings.FindAsync([userId, franchiseId], ct);
        if (holding is null || holding.Shares <= 0) return;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var pool = await _db.LockPoolAsync(franchiseId, ct);
        var fill = Csx.Domain.Amm.AmmMath.Sell(pool.CashReserve, pool.ShareReserve, holding.Shares, _market.FeeRate);

        var entry = await _ledger.PostAsync(
            EntryKinds.ForcedLiquidation,
            "user",
            userId,
            [
                new PostingDraft(OwnerTypes.User, userId, AssetTypes.Share, franchiseId, -fill.Shares),
                new PostingDraft(OwnerTypes.Pool, franchiseId, AssetTypes.Share, franchiseId, fill.Shares),
                new PostingDraft(OwnerTypes.Pool, franchiseId, AssetTypes.Cash, null, -fill.CashGross),
                new PostingDraft(OwnerTypes.User, userId, AssetTypes.Cash, null, fill.CashNet),
                new PostingDraft(OwnerTypes.Fees, null, AssetTypes.Cash, null, fill.FeeCash)
            ],
            ct,
            userId);

        pool.CashReserve = fill.CashReserveAfter;
        pool.ShareReserve = fill.ShareReserveAfter;
        pool.Seq += 1;
        pool.UpdatedAt = DateTimeOffset.UtcNow;
        holding.Shares = 0;
        holding.CostBasis = 0;

        _db.PriceTicks.Add(new PriceTick
        {
            FranchiseId = franchiseId,
            Price = fill.PriceAfter,
            Seq = pool.Seq,
            Source = TickSources.Admin,
            RefId = entry.Id,
            At = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public const string DelistReason = "Delisted";

    /// <summary>
    /// Redeem every holder at the last mark, halt the pool, and drop the ticker from the live book.
    /// Used when a CSC line (org + tier) is no longer fielded — not when Core issues a new team id.
    /// </summary>
    public async Task DelistAsync(Franchise franchise, CancellationToken ct)
    {
        if (!franchise.IsActive && franchise.Pool is { IsHalted: true, HaltReason: DelistReason })
            return;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var pool = await _db.LockPoolAsync(franchise.Id, ct);
        var mark = pool.ShareReserve == 0 ? 0m : pool.CashReserve / pool.ShareReserve;

        var holdings = await _db.Holdings
            .Where(h => h.FranchiseId == franchise.Id && h.Shares > 0)
            .ToListAsync(ct);
        foreach (var holding in holdings)
        {
            var shares = holding.Shares;
            var cash = MoneyRounding.RoundCashCredit(mark * shares);
            await _ledger.PostAsync(
                EntryKinds.DelistRedeem,
                "franchise",
                franchise.Id,
                [
                    new PostingDraft(OwnerTypes.User, holding.UserId, AssetTypes.Share, franchise.Id, -shares),
                    new PostingDraft(OwnerTypes.Pool, franchise.Id, AssetTypes.Share, franchise.Id, shares),
                    new PostingDraft(OwnerTypes.Mint, null, AssetTypes.Cash, null, -cash),
                    new PostingDraft(OwnerTypes.User, holding.UserId, AssetTypes.Cash, null, cash)
                ],
                ct,
                holding.UserId);
            pool.ShareReserve += shares;
            holding.Shares = 0;
            holding.CostBasis = 0;
        }

        franchise.IsActive = false;
        pool.IsHalted = true;
        pool.HaltReason = DelistReason;
        pool.ResumesAt = null;
        pool.Seq += 1;
        pool.UpdatedAt = DateTimeOffset.UtcNow;

        _db.MarketEvents.Add(new MarketEvent
        {
            FranchiseId = franchise.Id,
            Kind = EventKinds.Delist,
            Headline = DelistHeadline(franchise.Ticker, mark, holdings.Count),
            At = DateTimeOffset.UtcNow
        });
        _db.PriceTicks.Add(new PriceTick
        {
            FranchiseId = franchise.Id,
            Price = pool.ShareReserve == 0 ? 0 : pool.CashReserve / pool.ShareReserve,
            Seq = pool.Seq,
            Source = TickSources.Admin,
            At = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task ReactivateAsync(Franchise franchise, CancellationToken ct)
    {
        var wasInactive = !franchise.IsActive;
        franchise.IsActive = true;
        var pool = await _db.Pools.SingleOrDefaultAsync(p => p.FranchiseId == franchise.Id, ct);
        if (pool is not null && pool.HaltReason == DelistReason)
        {
            pool.IsHalted = false;
            pool.HaltReason = null;
            pool.ResumesAt = null;
            pool.UpdatedAt = DateTimeOffset.UtcNow;
        }
        if (wasInactive)
        {
            _db.MarketEvents.Add(new MarketEvent
            {
                FranchiseId = franchise.Id,
                Kind = EventKinds.Resume,
                Headline = $"{franchise.Ticker} relisted",
                At = DateTimeOffset.UtcNow
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    private static string DelistHeadline(string ticker, decimal mark, int holders) =>
        holders == 0
            ? $"{ticker} delisted at ${mark:0.00}"
            : $"{ticker} delisted at ${mark:0.00}; {holders} position(s) redeemed";
}
