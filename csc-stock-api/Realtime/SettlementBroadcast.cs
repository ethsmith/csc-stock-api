using Csx.Api.Contracts;
using Csx.Api.Hubs;
using Csx.Infrastructure.Data;
using Csx.Infrastructure.Settlement;
using Microsoft.EntityFrameworkCore;

namespace Csx.Api.Realtime;

public static class SettlementBroadcast
{
    public static async Task<MatchSettledEvent> BuildAndSendAsync(
        CsxDbContext db,
        MarketBroadcaster realtime,
        SettlementBatchResult result,
        CancellationToken ct,
        bool broadcast = true)
    {
        var match = await db.Matches
            .Include(m => m.TeamA)
            .Include(m => m.TeamB)
            .SingleAsync(m => m.Id == result.MatchId, ct);

        var sides = new List<MatchSettledSide>();
        foreach (var s in result.Settlements)
        {
            var franchise = s.FranchiseId == match.FranchiseA ? match.TeamA : match.TeamB;
            var seq = await db.Pools.AsNoTracking()
                .Where(p => p.FranchiseId == s.FranchiseId)
                .Select(p => p.Seq)
                .SingleAsync(ct);
            sides.Add(new MatchSettledSide(
                s.FranchiseId,
                franchise.Ticker,
                franchise.Name,
                s.ExpectedMargin.ToString("0.0000"),
                s.ActualMargin,
                s.Surprise.ToString("0.0000"),
                s.ShockApplied.ToString("0.000000"),
                MoneyFormat.Price(s.PriceBefore),
                MoneyFormat.Price(s.PriceAfter),
                seq));
            if (broadcast)
                await realtime.PriceUpdated(s.FranchiseId, s.PriceAfter, s.PriceBefore, seq, s.CreatedAt);
        }

        var payload = new MatchSettledEvent(match.Id, match.Map, match.RoundsA, match.RoundsB, sides);
        if (broadcast)
            await realtime.MatchSettled(payload);
        return payload;
    }
}
