namespace Csx.Domain.Shock;

public sealed record HistoricalMatch(
    string HomeKey,
    string AwayKey,
    int RoundsHome,
    int RoundsAway,
    bool IsBo3,
    DateTimeOffset At);

public sealed record ImpliedOpenLine(
    string Key,
    decimal Price,
    int Matches,
    decimal Elo);

public sealed record ImpliedOpenResult(
    IReadOnlyList<ImpliedOpenLine> Lines,
    decimal MeanBeforeRescale,
    decimal MeanAfterRescale,
    int MatchesUsed);

public static class ImpliedOpenMath
{
    public static ImpliedOpenResult Replay(
        IReadOnlyList<string> bookKeys,
        IReadOnlyList<HistoricalMatch> matches,
        decimal initialPrice,
        decimal floor,
        decimal ceiling,
        decimal alpha,
        decimal beta,
        decimal eloDivisor,
        ShockMode mode,
        bool rescaleToInitial)
    {
        var state = new Dictionary<string, (decimal Elo, decimal Price, int Matches)>(StringComparer.Ordinal);
        foreach (var key in bookKeys.Where(k => k.Length > 1))
            state[key] = (1000m, initialPrice, 0);

        var ordered = matches
            .Where(m => !string.IsNullOrWhiteSpace(m.HomeKey) && !string.IsNullOrWhiteSpace(m.AwayKey))
            .Where(m => m.HomeKey != m.AwayKey)
            .Where(m => m.RoundsHome != 0 || m.RoundsAway != 0)
            .OrderBy(m => m.At)
            .ThenBy(m => m.HomeKey, StringComparer.Ordinal)
            .ToList();

        foreach (var match in ordered)
        {
            Ensure(state, match.HomeKey, initialPrice);
            Ensure(state, match.AwayKey, initialPrice);
            ApplyMatch(state, match, floor, ceiling, alpha, beta, eloDivisor, mode);
        }

        var book = bookKeys
            .Where(state.ContainsKey)
            .Select(k =>
            {
                var s = state[k];
                return new ImpliedOpenLine(k, s.Price, s.Matches, s.Elo);
            })
            .ToList();

        var meanBefore = book.Count == 0 ? initialPrice : book.Average(l => l.Price);
        if (rescaleToInitial && meanBefore > 0m && book.Count > 0)
        {
            var factor = initialPrice / meanBefore;
            book = book.Select(l => l with
            {
                Price = ShockMath.Clamp(l.Price * factor, floor, ceiling)
            }).ToList();
        }

        var meanAfter = book.Count == 0 ? initialPrice : book.Average(l => l.Price);
        return new ImpliedOpenResult(book, meanBefore, meanAfter, ordered.Count);
    }

    private static void Ensure(
        Dictionary<string, (decimal Elo, decimal Price, int Matches)> state,
        string key,
        decimal initialPrice)
    {
        if (!state.ContainsKey(key))
            state[key] = (1000m, initialPrice, 0);
    }

    private static void ApplyMatch(
        Dictionary<string, (decimal Elo, decimal Price, int Matches)> state,
        HistoricalMatch match,
        decimal floor,
        decimal ceiling,
        decimal alpha,
        decimal beta,
        decimal eloDivisor,
        ShockMode mode)
    {
        var home = state[match.HomeKey];
        var away = state[match.AwayKey];
        var maps = ShockMath.EstimateMapCount(match.IsBo3, match.RoundsHome, match.RoundsAway);
        var actualHome = match.RoundsHome - match.RoundsAway;
        var actualAway = -actualHome;

        var priceHome = Shock(home.Price, home.Elo, away.Elo, actualHome, maps, floor, ceiling, alpha, beta, eloDivisor, mode);
        var priceAway = Shock(away.Price, away.Elo, home.Elo, actualAway, maps, floor, ceiling, alpha, beta, eloDivisor, mode);
        var (eloHome, eloAway) = ShockMath.EloAfter(home.Elo, away.Elo, actualHome > 0, actualHome == 0);

        state[match.HomeKey] = (eloHome, priceHome, home.Matches + 1);
        state[match.AwayKey] = (eloAway, priceAway, away.Matches + 1);
    }

    private static decimal Shock(
        decimal price,
        decimal elo,
        decimal oppElo,
        int actualMargin,
        int maps,
        decimal floor,
        decimal ceiling,
        decimal alpha,
        decimal beta,
        decimal eloDivisor,
        ShockMode mode)
    {
        var expected = ShockMath.ExpectedMargin(elo, oppElo, eloDivisor) * maps;
        var surprise = actualMargin - expected;
        var shock = ShockMath.ComputeShock(mode, surprise, actualMargin > 0, alpha, beta);
        var next = price * (1m + shock);
        return ShockMath.Clamp(next, floor, ceiling);
    }
}
