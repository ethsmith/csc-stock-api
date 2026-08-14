using System.Text.Json;
using Csx.Infrastructure.Data;

namespace Csx.Api.Contracts;

public static class ContractMapper
{
    public static SettlementDto ToSettlement(Settlement s, string ticker) => new(
        s.Id,
        s.MatchId,
        s.FranchiseId,
        ticker,
        s.EloBefore.ToString("0.00"),
        s.OppEloBefore.ToString("0.00"),
        s.ExpectedMargin.ToString("0.0000"),
        s.ActualMargin,
        s.Surprise.ToString("0.0000"),
        s.ShockRaw.ToString("0.000000"),
        s.ShockApplied.ToString("0.000000"),
        s.ShockClamped,
        MoneyFormat.Price(s.PriceBefore),
        MoneyFormat.Price(s.PriceAfter),
        s.IsCorrection,
        s.CreatedAt);

    public static string SettlementPayload(Settlement s, string ticker) =>
        JsonSerializer.Serialize(ToSettlement(s, ticker));

    public static SettlementDto? TryParseSettlement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<SettlementDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static HoldingDto ToHolding(Holding h, decimal portfolioValue)
    {
        var price = h.Franchise.Pool?.Price ?? 0m;
        var value = price * h.Shares;
        var pnl = value - h.CostBasis;
        var avg = h.Shares == 0 ? 0m : h.CostBasis / h.Shares;
        var pnlPct = h.CostBasis == 0m ? 0m : pnl / h.CostBasis;
        var weight = portfolioValue == 0m ? 0m : value / portfolioValue;
        return new HoldingDto(
            h.FranchiseId,
            h.Franchise.Ticker,
            h.Franchise.Name,
            MoneyFormat.Shares(h.Shares),
            MoneyFormat.Cash(h.CostBasis),
            MoneyFormat.Price(avg),
            MoneyFormat.Cash(value),
            MoneyFormat.Cash(pnl),
            MoneyFormat.Pct(pnlPct),
            MoneyFormat.Pct(weight));
    }

    public static MatchDto ToMatch(LeagueMatch m, Franchise home, Franchise away, IEnumerable<Settlement> settlements, int preMatchMinutes)
    {
        DateTimeOffset? lockout = m.ScheduledAt is { } at ? at.AddMinutes(-preMatchMinutes) : null;
        var ticks = new Dictionary<long, string> { [home.Id] = home.Ticker, [away.Id] = away.Ticker };
        return new MatchDto(
            m.Id,
            m.ExternalId,
            m.Status,
            m.IsBo3,
            m.Map,
            m.ScheduledAt,
            lockout,
            m.FinishedAt,
            new MatchTeamDto(home.Id, home.Ticker, home.Name, home.Division),
            new MatchTeamDto(away.Id, away.Ticker, away.Name, away.Division),
            m.RoundsA,
            m.RoundsB,
            settlements.Select(s => ToSettlement(s, ticks.GetValueOrDefault(s.FranchiseId, "?"))).ToList());
    }
}
