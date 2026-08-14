using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Csx.Domain.Amm;
using Csx.Domain.Config;
using Csx.Domain.Errors;
using Csx.Domain.Ledger;
using Csx.Infrastructure.Data;
using Csx.Infrastructure.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Csx.Infrastructure.Trading;

public sealed class TradingService
{
    private readonly CsxDbContext _db;
    private readonly LedgerService _ledger;
    private readonly MarketOptions _market;
    private readonly QuoteOptions _quote;

    public TradingService(
        CsxDbContext db,
        LedgerService ledger,
        IOptions<MarketOptions> market,
        IOptions<QuoteOptions> quote)
    {
        _db = db;
        _ledger = ledger;
        _market = market.Value;
        _quote = quote.Value;
    }

    public async Task<QuoteRecord> QuoteAsync(
        long userId,
        long franchiseId,
        TradeSide side,
        decimal? cashIn,
        decimal? shares,
        CancellationToken ct)
    {
        var pool = await _db.Pools.Include(p => p.Franchise).AsNoTracking()
                       .SingleOrDefaultAsync(p => p.FranchiseId == franchiseId, ct)
                   ?? throw new MarketException(ErrorCodes.FranchiseNotFound, "Unknown franchise.", 404);

        if (!pool.Franchise.IsActive)
            throw MarketException.Delisted(franchiseId);

        if (pool.IsHalted)
            throw MarketException.Halted(franchiseId, pool.ResumesAt);

        AmmQuote fill;
        if (side == TradeSide.Buy)
        {
            var cash = cashIn ?? (shares is { } q
                ? AmmMath.CashInForBuyShares(pool.CashReserve, pool.ShareReserve, q, _market.FeeRate)
                : throw new MarketException(ErrorCodes.OrderTooSmall, "Provide cashIn or shares."));
            fill = AmmMath.Buy(pool.CashReserve, pool.ShareReserve, cash, _market.FeeRate);
        }
        else
        {
            var q = shares ?? throw new MarketException(ErrorCodes.OrderTooSmall, "Provide shares to sell.");
            fill = AmmMath.Sell(pool.CashReserve, pool.ShareReserve, q, _market.FeeRate);
        }

        if (fill.Side == TradeSide.Buy && fill.CashIn < _market.MinOrderCash)
            throw new MarketException(ErrorCodes.OrderTooSmall, $"Minimum order is {_market.MinOrderCash:0.00}.");
        if (fill.Side == TradeSide.Sell && fill.CashGross < _market.MinOrderCash)
            throw new MarketException(ErrorCodes.OrderTooSmall, $"Minimum order is {_market.MinOrderCash:0.00}.");

        var quote = new QuoteRecord
        {
            Id = "q_" + Guid.NewGuid().ToString("N"),
            UserId = userId,
            FranchiseId = franchiseId,
            Side = side.ToString().ToLowerInvariant(),
            CashIn = fill.CashIn,
            SharesOut = fill.Shares,
            AvgPrice = fill.AvgPrice,
            PriceBefore = fill.PriceBefore,
            PriceAfter = fill.PriceAfter,
            FeeCash = fill.FeeCash,
            PoolSeq = pool.Seq,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, _quote.TtlSeconds)),
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Quotes.Add(quote);
        await _db.SaveChangesAsync(ct);
        return quote;
    }

    public async Task<Order> FillAsync(
        long userId,
        string quoteId,
        int maxSlippageBps,
        string idempotencyKey,
        string requestHash,
        CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        IdempotencyKey existing;
        try
        {
            existing = await InsertIdempotencyAsync(userId, idempotencyKey, requestHash, ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            var stored = await _db.IdempotencyKeys.AsNoTracking()
                .SingleAsync(k => k.UserId == userId && k.Key == idempotencyKey, ct);
            if (stored.RequestHash != requestHash)
                throw new MarketException(ErrorCodes.IdempotencyKeyReuse, "Idempotency key reused with a different body.", 409);
            if (stored.ResponseBody is not null &&
                JsonSerializer.Deserialize<IdempotentOrderRef>(stored.ResponseBody) is { } rec)
            {
                return await _db.Orders.Include(o => o.Trade).SingleAsync(o => o.Id == rec.Id, ct);
            }
            throw new MarketException(ErrorCodes.IdempotencyKeyReuse, "Idempotency key is already in flight.", 409);
        }

        try
        {
            var order = await FillCoreAsync(userId, quoteId, maxSlippageBps, ct);
            existing.ResponseStatus = 201;
            existing.ResponseBody = JsonSerializer.Serialize(new IdempotentOrderRef(order.Id));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return order;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<Order> FillCoreAsync(long userId, string quoteId, int maxSlippageBps, CancellationToken ct)
    {
        var user = await _db.Users.SingleAsync(u => u.Id == userId, ct);
        if (!user.CanTrade)
            throw new MarketException(ErrorCodes.TradingRestricted, "This account is not allowed to trade.");

        var quote = await _db.Quotes.SingleOrDefaultAsync(q => q.Id == quoteId, ct)
                    ?? throw new MarketException(ErrorCodes.QuoteNotFound, "Quote not found.", 404);
        if (quote.UserId != userId)
            throw new MarketException(ErrorCodes.QuoteNotFound, "Quote not found.", 404);
        if (quote.ExpiresAt < DateTimeOffset.UtcNow)
            throw new MarketException(ErrorCodes.QuoteExpired, "Quote expired.");

        var side = quote.Side == "buy" ? TradeSide.Buy : TradeSide.Sell;
        var franchiseId = quote.FranchiseId;

        if (side == TradeSide.Buy &&
            await _db.RosterSeats.AnyAsync(r => r.FranchiseId == franchiseId && r.DiscordId == user.DiscordId, ct))
        {
            throw new MarketException(
                ErrorCodes.SelfDealingRestricted,
                "You cannot hold shares in a franchise you are rostered on.");
        }

        var pool = await _db.LockPoolAsync(franchiseId, ct);
        var franchise = await _db.Franchises.SingleAsync(f => f.Id == franchiseId, ct);
        if (!franchise.IsActive)
            throw MarketException.Delisted(franchiseId);
        if (pool.IsHalted)
            throw MarketException.Halted(franchiseId, pool.ResumesAt);

        AmmQuote fill = side == TradeSide.Buy
            ? AmmMath.Buy(pool.CashReserve, pool.ShareReserve, quote.CashIn, _market.FeeRate)
            : AmmMath.Sell(pool.CashReserve, pool.ShareReserve, quote.SharesOut, _market.FeeRate);

        if (fill.AvgPrice > 0 && quote.AvgPrice > 0)
        {
            var slip = Math.Abs(fill.AvgPrice - quote.AvgPrice) / quote.AvgPrice;
            if (slip > maxSlippageBps / 10_000m)
            {
                throw new MarketException(
                    ErrorCodes.SlippageExceeded,
                    $"Quote was {quote.AvgPrice:0.0000}, current fill is {fill.AvgPrice:0.0000}.",
                    409,
                    new Dictionary<string, object?>
                    {
                        ["quotedPrice"] = quote.AvgPrice.ToString("0.0000"),
                        ["currentPrice"] = fill.AvgPrice.ToString("0.0000")
                    });
            }
        }

        var userCashAcc = await _ledger.GetOrCreateAccountAsync(OwnerTypes.User, userId, AssetTypes.Cash, null, ct);
        var userShareAcc = await _ledger.GetOrCreateAccountAsync(OwnerTypes.User, userId, AssetTypes.Share, franchiseId, ct);
        var userCash = await _ledger.GetBalanceAsync(userCashAcc, ct);
        var userShares = await _ledger.GetBalanceAsync(userShareAcc, ct);

        if (side == TradeSide.Buy && userCash < fill.CashIn)
            throw new MarketException(ErrorCodes.InsufficientFunds, "Not enough cash.");
        if (side == TradeSide.Sell && userShares < fill.Shares)
            throw new MarketException(ErrorCodes.InsufficientShares, "Not enough shares.");

        if (side == TradeSide.Buy)
        {
            var cap = pool.TotalSupply * _market.PositionCapPct;
            if (userShares + fill.Shares > cap)
                throw new MarketException(ErrorCodes.PositionCapExceeded, "Position would exceed 15% of supply.");
        }

        var order = new Order
        {
            UserId = userId,
            FranchiseId = franchiseId,
            Side = quote.Side,
            QuoteId = quote.Id,
            CashIn = side == TradeSide.Buy ? fill.CashIn : null,
            SharesIn = side == TradeSide.Sell ? fill.Shares : null,
            MaxSlippageBps = maxSlippageBps,
            Status = OrderStatuses.Filled,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);

        var drafts = BuildTradePostings(franchiseId, userId, fill);
        var entry = await _ledger.PostAsync(
            fill.Side == TradeSide.Buy ? EntryKinds.TradeBuy : EntryKinds.TradeSell,
            "order",
            order.Id,
            drafts,
            ct,
            userId);

        pool.CashReserve = fill.CashReserveAfter;
        pool.ShareReserve = fill.ShareReserveAfter;
        pool.Seq += 1;
        pool.UpdatedAt = DateTimeOffset.UtcNow;

        await ApplyHoldingAsync(userId, franchiseId, fill, ct);

        var trade = new Trade
        {
            OrderId = order.Id,
            EntryId = entry.Id,
            FranchiseId = franchiseId,
            Side = quote.Side,
            Shares = fill.Shares,
            Cash = fill.Side == TradeSide.Buy ? fill.CashIn : fill.CashNet,
            Fee = fill.FeeCash,
            PriceBefore = fill.PriceBefore,
            PriceAfter = fill.PriceAfter,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Trades.Add(trade);

        _db.PriceTicks.Add(new PriceTick
        {
            FranchiseId = franchiseId,
            Price = fill.PriceAfter,
            Seq = pool.Seq,
            Source = TickSources.Trade,
            RefId = order.Id,
            At = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        var userSharesAfter = await SumUserSharesAsync(franchiseId, ct);
        Invariants.AssertShareSupply(pool.ShareReserve, userSharesAfter, pool.TotalSupply);
        Invariants.AssertNonNegative(fill.CashReserveAfter, "pool cash");
        Invariants.AssertNonNegative(fill.ShareReserveAfter, "pool shares");

        var cashAfter = await _ledger.GetBalanceAsync(userCashAcc, ct);
        var sharesAfter = await _ledger.GetBalanceAsync(userShareAcc, ct);
        Invariants.AssertNonNegative(cashAfter, "user cash");
        Invariants.AssertNonNegative(sharesAfter, "user shares");

        await _db.Entry(order).Reference(o => o.Trade).LoadAsync(ct);
        return order;
    }

    private List<PostingDraft> BuildTradePostings(long franchiseId, long userId, AmmQuote fill)
    {
        if (fill.Side == TradeSide.Buy)
        {
            return
            [
                new(OwnerTypes.User, userId, AssetTypes.Cash, null, -fill.CashIn),
                new(OwnerTypes.Pool, franchiseId, AssetTypes.Cash, null, fill.CashNet),
                new(OwnerTypes.Fees, null, AssetTypes.Cash, null, fill.FeeCash),
                new(OwnerTypes.Pool, franchiseId, AssetTypes.Share, franchiseId, -fill.Shares),
                new(OwnerTypes.User, userId, AssetTypes.Share, franchiseId, fill.Shares)
            ];
        }

        return
        [
            new(OwnerTypes.User, userId, AssetTypes.Share, franchiseId, -fill.Shares),
            new(OwnerTypes.Pool, franchiseId, AssetTypes.Share, franchiseId, fill.Shares),
            new(OwnerTypes.Pool, franchiseId, AssetTypes.Cash, null, -fill.CashGross),
            new(OwnerTypes.User, userId, AssetTypes.Cash, null, fill.CashNet),
            new(OwnerTypes.Fees, null, AssetTypes.Cash, null, fill.FeeCash)
        ];
    }

    private async Task ApplyHoldingAsync(long userId, long franchiseId, AmmQuote fill, CancellationToken ct)
    {
        var holding = await _db.Holdings.FindAsync([userId, franchiseId], ct);
        if (holding is null)
        {
            holding = new Holding { UserId = userId, FranchiseId = franchiseId, Shares = 0, CostBasis = 0 };
            _db.Holdings.Add(holding);
        }

        if (fill.Side == TradeSide.Buy)
        {
            holding.CostBasis += fill.CashIn;
            holding.Shares += fill.Shares;
        }
        else
        {
            var avg = holding.Shares == 0 ? 0 : holding.CostBasis / holding.Shares;
            holding.Shares -= fill.Shares;
            holding.CostBasis = holding.Shares <= 0 ? 0 : avg * holding.Shares;
            if (holding.Shares < 0) holding.Shares = 0;
        }
    }

    private async Task<decimal> SumUserSharesAsync(long franchiseId, CancellationToken ct) =>
        await _db.Holdings.Where(h => h.FranchiseId == franchiseId).SumAsync(h => (decimal?)h.Shares, ct) ?? 0m;

    private async Task<IdempotencyKey> InsertIdempotencyAsync(
        long userId, string key, string requestHash, CancellationToken ct)
    {
        var row = new IdempotencyKey
        {
            UserId = userId,
            Key = key,
            RequestHash = requestHash,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.IdempotencyKeys.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public static string HashRequest(string body)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record IdempotentOrderRef(long Id);
}
