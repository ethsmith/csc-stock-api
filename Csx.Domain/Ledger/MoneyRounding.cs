namespace Csx.Domain.Ledger;

/// <summary>
/// Internal math stays at numeric(28,10). Round only at cash/share boundaries.
/// Residuals must be posted to system:fees so entries still sum to zero.
/// </summary>
public static class MoneyRounding
{
    public const int CashDecimals = 2;
    public const int ShareDecimals = 4;
    public const int InternalDecimals = 10;

    public static decimal Scale(int decimals) => decimals switch
    {
        2 => 100m,
        4 => 10_000m,
        10 => 10_000_000_000m,
        _ => (decimal)Math.Pow(10, decimals)
    };

    /// <summary>Cash the user pays: round up to the cent.</summary>
    public static decimal RoundCashDebit(decimal amount)
    {
        if (amount <= 0) return 0m;
        return Math.Ceiling(amount * 100m) / 100m;
    }

    /// <summary>Cash the user receives: round down to the cent.</summary>
    public static decimal RoundCashCredit(decimal amount)
    {
        if (amount <= 0) return 0m;
        return Math.Floor(amount * 100m) / 100m;
    }

    /// <summary>Shares leaving the pool: 4 dp, round down.</summary>
    public static decimal RoundSharesOut(decimal amount)
    {
        if (amount <= 0) return 0m;
        return Math.Floor(amount * 10_000m) / 10_000m;
    }

    public static decimal Quantize(decimal amount, int decimals) =>
        decimal.Round(amount, decimals, MidpointRounding.ToZero);
}
