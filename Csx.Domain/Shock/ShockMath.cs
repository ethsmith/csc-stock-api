namespace Csx.Domain.Shock;

public enum ShockMode
{
    Surprise,
    SignedScaled
}

public sealed record ShockComputation(
    decimal Elo,
    decimal OppElo,
    decimal ExpectedMargin,
    decimal ActualMargin,
    decimal Surprise,
    decimal ShockRaw,
    decimal ShockApplied,
    bool ShockClamped,
    decimal PriceBefore,
    decimal PriceAfter,
    decimal DeltaCash);

public static class ShockMath
{
    public static decimal ExpectedMargin(decimal elo, decimal oppElo, decimal eloDivisor)
    {
        if (eloDivisor == 0) return 0m;
        var raw = (elo - oppElo) / eloDivisor;
        return Clamp(raw, -10m, 10m);
    }

    public static decimal SurpriseShock(decimal surprise, decimal alpha, decimal beta)
    {
        if (beta == 0) return 0m;
        return alpha * Tanh(surprise / beta);
    }

    public static decimal SignedScaledShock(decimal surprise, bool won, decimal alpha, decimal beta)
    {
        var magnitude = alpha * (0.15m + 0.85m * Abs(Tanh(surprise / beta)));
        return won ? magnitude : -magnitude;
    }

    public static decimal ComputeShock(
        ShockMode mode,
        decimal surprise,
        bool won,
        decimal alpha,
        decimal beta) =>
        mode == ShockMode.SignedScaled
            ? SignedScaledShock(surprise, won, alpha, beta)
            : SurpriseShock(surprise, alpha, beta);

    public static decimal ApplyCircuitBreaker(
        decimal price,
        decimal price24hAgo,
        decimal targetPrice,
        decimal dailyMovePct)
    {
        if (price24hAgo <= 0) return targetPrice;
        var capHigh = price24hAgo * (1m + dailyMovePct);
        var capLow = price24hAgo * (1m - dailyMovePct);
        return Clamp(targetPrice, capLow, capHigh);
    }

    public static decimal Fundamental(decimal initialPrice, decimal elo, decimal leagueMeanElo, decimal kappa, decimal floor, decimal ceiling)
    {
        var f = initialPrice * (1m + kappa * (elo - leagueMeanElo) / 400m);
        return Clamp(f, floor, ceiling);
    }

    public static decimal DecayTick(decimal price, decimal fundamental, decimal lambda) =>
        price + lambda * (fundamental - price);

    public static decimal Tanh(decimal x)
    {
        var d = (double)x;
        return (decimal)Math.Tanh(d);
    }

    public static decimal Abs(decimal x) => x < 0 ? -x : x;

    public static decimal Clamp(decimal value, decimal min, decimal max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    public static int EstimateMapCount(bool isBo3, int roundsA, int roundsB)
    {
        if (!isBo3) return 1;
        var total = roundsA + roundsB;
        if (total <= 0) return 1;
        return Math.Clamp((int)Math.Round(total / 22.0), 2, 3);
    }

    public static (decimal EloA, decimal EloB) EloAfter(
        decimal eloA, decimal eloB, bool aWon, bool draw, decimal k = 16m)
    {
        var ea = 1m / (1m + (decimal)Math.Pow(10, (double)((eloB - eloA) / 400m)));
        var eb = 1m - ea;
        var sa = draw ? 0.5m : aWon ? 1m : 0m;
        var sb = 1m - sa;
        return (eloA + k * (sa - ea), eloB + k * (sb - eb));
    }
}
