using Csx.Domain.Errors;
using Csx.Domain.Ledger;

namespace Csx.Domain.Amm;

public enum TradeSide
{
    Buy,
    Sell
}

public sealed record AmmQuote(
    TradeSide Side,
    decimal CashIn,
    decimal CashGross,
    decimal CashNet,
    decimal Shares,
    decimal FeeCash,
    decimal RoundingResidual,
    decimal PriceBefore,
    decimal PriceAfter,
    decimal AvgPrice,
    decimal CashReserveAfter,
    decimal ShareReserveAfter,
    decimal KAfter);

public static class AmmMath
{
    public static decimal Price(decimal cashReserve, decimal shareReserve)
    {
        if (shareReserve <= 0) throw new MarketException(ErrorCodes.PoolEmpty, "Share reserve is empty.", 500);
        return cashReserve / shareReserve;
    }

    public static decimal K(decimal cashReserve, decimal shareReserve) => cashReserve * shareReserve;

    public static AmmQuote Buy(decimal cashReserve, decimal shareReserve, decimal cashIn, decimal feeRate)
    {
        if (cashIn <= 0)
            throw new MarketException(ErrorCodes.OrderTooSmall, "Cash in must be positive.");
        if (cashReserve <= 0 || shareReserve <= 0)
            throw new MarketException(ErrorCodes.PoolEmpty, "Pool reserves must be positive.", 500);

        var charged = MoneyRounding.RoundCashDebit(cashIn);
        var fee = MoneyRounding.RoundCashCredit(charged * feeRate);
        var cNet = charged - fee;
        if (cNet <= 0)
            throw new MarketException(ErrorCodes.OrderTooSmall, "Fee consumed the entire order.");

        var k = K(cashReserve, shareReserve);
        var rPrime = cashReserve + cNet;
        var tPrime = k / rPrime;
        var qRaw = shareReserve - tPrime;
        var q = MoneyRounding.RoundSharesOut(qRaw);
        if (q <= 0)
            throw new MarketException(ErrorCodes.OrderTooSmall, "Order is too small to buy any shares.");

        // Recompute pool from the rounded share amount so k only increases by fees.
        tPrime = shareReserve - q;
        if (tPrime <= 0)
            throw new MarketException(ErrorCodes.PoolEmpty, "Trade would empty the share reserve.");

        var priceBefore = Price(cashReserve, shareReserve);
        var priceAfter = Price(rPrime, tPrime);
        var avg = cNet / q;

        // Rounding leftover on shares stays in the pool (user got fewer shares).
        var shareDust = qRaw - q;

        return new AmmQuote(
            Side: TradeSide.Buy,
            CashIn: charged,
            CashGross: charged,
            CashNet: cNet,
            Shares: q,
            FeeCash: fee,
            RoundingResidual: shareDust,
            PriceBefore: priceBefore,
            PriceAfter: priceAfter,
            AvgPrice: avg,
            CashReserveAfter: rPrime,
            ShareReserveAfter: tPrime,
            KAfter: rPrime * tPrime);
    }

    public static AmmQuote Sell(decimal cashReserve, decimal shareReserve, decimal sharesIn, decimal feeRate)
    {
        if (sharesIn <= 0)
            throw new MarketException(ErrorCodes.OrderTooSmall, "Shares in must be positive.");
        if (cashReserve <= 0 || shareReserve <= 0)
            throw new MarketException(ErrorCodes.PoolEmpty, "Pool reserves must be positive.", 500);

        var q = MoneyRounding.RoundSharesOut(sharesIn);
        if (q <= 0)
            throw new MarketException(ErrorCodes.OrderTooSmall, "Order is too small to sell any shares.");

        var k = K(cashReserve, shareReserve);
        var tPrime = shareReserve + q;
        var rPrime = k / tPrime;
        if (rPrime <= 0)
            throw new MarketException(ErrorCodes.PoolEmpty, "Trade would empty the cash reserve.");

        var grossRaw = cashReserve - rPrime;
        var gross = MoneyRounding.RoundCashCredit(grossRaw);
        var fee = MoneyRounding.RoundCashCredit(gross * feeRate);
        var net = gross - fee;
        if (net <= 0)
            throw new MarketException(ErrorCodes.OrderTooSmall, "Sale proceeds are below the minimum after fees.");

        // Cash left in the pool after paying the user and taking the fee.
        // grossRaw - gross is the cent dust that stays in the pool (user was rounded down).
        var cashDust = grossRaw - gross;
        rPrime = cashReserve - gross;
        if (rPrime <= 0)
            throw new MarketException(ErrorCodes.PoolEmpty, "Trade would empty the cash reserve.");

        var priceBefore = Price(cashReserve, shareReserve);
        var priceAfter = Price(rPrime, tPrime);

        return new AmmQuote(
            Side: TradeSide.Sell,
            CashIn: 0m,
            CashGross: gross,
            CashNet: net,
            Shares: q,
            FeeCash: fee,
            RoundingResidual: cashDust,
            PriceBefore: priceBefore,
            PriceAfter: priceAfter,
            AvgPrice: gross / q,
            CashReserveAfter: rPrime,
            ShareReserveAfter: tPrime,
            KAfter: rPrime * tPrime);
    }

    /// <summary>
    /// Inverse: how much cash must be spent (before fee) to buy approximately <paramref name="shares"/>.
    /// Used when the client quotes by share count on the buy side.
    /// </summary>
    public static decimal CashInForBuyShares(decimal cashReserve, decimal shareReserve, decimal shares, decimal feeRate)
    {
        var q = MoneyRounding.RoundSharesOut(shares);
        if (q <= 0 || q >= shareReserve)
            throw new MarketException(ErrorCodes.OrderTooSmall, "Requested share amount is not fillable.");

        var k = K(cashReserve, shareReserve);
        var tPrime = shareReserve - q;
        var rPrime = k / tPrime;
        var cNet = rPrime - cashReserve;
        if (feeRate >= 1)
            throw new MarketException(ErrorCodes.OrderTooSmall, "Invalid fee rate.");
        var cashIn = cNet / (1m - feeRate);
        return MoneyRounding.RoundCashDebit(cashIn);
    }
}
