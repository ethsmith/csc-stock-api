using Csx.Api.Auth;
using Csx.Api.Contracts;
using Csx.Domain.Errors;
using Csx.Infrastructure.Data;
using Csx.Infrastructure.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Csx.Api.Endpoints;

public static class PortfolioEndpoints
{
    public static IEndpointRouteBuilder MapPortfolioEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1").WithTags("Portfolio").RequireAuthorization();

        g.MapGet("/portfolio", async (HttpContext ctx, CsxDbContext db, LedgerService ledger, CancellationToken ct) =>
        {
            var userId = TokenService.UserId(ctx.User) ?? throw new MarketException(ErrorCodes.TradingRestricted, "Unauthorized", 401);
            return Results.Ok(await BuildPortfolio(db, ledger, userId, includeCash: true, ct));
        });

        g.MapGet("/portfolio/history", async (HttpContext ctx, string? window, CsxDbContext db, CancellationToken ct) =>
        {
            var userId = TokenService.UserId(ctx.User) ?? throw new MarketException(ErrorCodes.TradingRestricted, "Unauthorized", 401);
            var days = WindowDays(window);
            var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days));
            var rows = await db.PortfolioSnapshots
                .Where(s => s.UserId == userId && s.Day >= from)
                .OrderBy(s => s.Day)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => new EquityPoint(r.Day, MoneyFormat.Cash(r.TotalValue))));
        });

        g.MapGet("/leaderboard", async (HttpContext ctx, string? window, long? cursor, CsxDbContext db, LedgerService ledger, CancellationToken ct) =>
        {
            var callerId = TokenService.UserId(ctx.User);
            var days = WindowDays(window);
            var snapDay = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days));
            var priorRows = await db.PortfolioSnapshots.Where(s => s.Day <= snapDay).ToListAsync(ct);
            var prior = priorRows
                .GroupBy(s => s.UserId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Day).First().TotalValue);

            var users = await db.Users.AsNoTracking().ToListAsync(ct);
            var priced = new List<(User User, decimal Value, PortfolioResponse Portfolio)>();
            foreach (var u in users)
            {
                var p = await BuildPortfolio(db, ledger, u.Id, includeCash: true, ct);
                var total = decimal.Parse(p.TotalValue, System.Globalization.CultureInfo.InvariantCulture);
                priced.Add((u, total, p));
            }
            var ranked = priced.OrderByDescending(x => x.Value).ThenBy(x => x.User.Id).ToList();
            var callerRank = callerId is { } cid
                ? ranked.FindIndex(x => x.User.Id == cid) + 1
                : (int?)null;
            if (callerRank == 0) callerRank = null;

            string? gapAbove = null;
            if (callerRank is > 1)
            {
                var above = ranked[callerRank.Value - 2].Value;
                var self = ranked[callerRank.Value - 1].Value;
                gapAbove = MoneyFormat.Cash(above - self);
            }

            var start = cursor is { } c ? (int)c : 0;
            var page = ranked.Skip(start).Take(50)
                .Select((x, i) =>
                {
                    string? change = null;
                    if (prior.TryGetValue(x.User.Id, out var then) && then != 0m)
                        change = MoneyFormat.Pct((x.Value - then) / then);
                    var top = x.Portfolio.Holdings.OrderByDescending(h => decimal.Parse(h.Mark, System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();
                    return new LeaderboardRow(
                        start + i + 1,
                        x.User.Id,
                        x.User.DisplayName,
                        MoneyFormat.Cash(x.Value),
                        change,
                        top?.Ticker,
                        top?.Name,
                        callerId == x.User.Id);
                })
                .ToList();
            return Results.Ok(new LeaderboardResponse(callerRank, gapAbove, page));
        });

        g.MapGet("/users/{id:long}/portfolio", async (long id, CsxDbContext db, LedgerService ledger, CancellationToken ct) =>
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();
            var p = await BuildPortfolio(db, ledger, id, includeCash: false, ct);
            return Results.Ok(new PublicPortfolioResponse(user.Id, user.DisplayName, p.Holdings));
        }).AllowAnonymous();

        return app;
    }

    public static async Task<PortfolioResponse> BuildPortfolio(
        CsxDbContext db, LedgerService ledger, long userId, bool includeCash, CancellationToken ct)
    {
        var cash = includeCash ? await ledger.GetUserCashAsync(userId, ct) : 0m;
        var holdings = await db.Holdings
            .Include(h => h.Franchise).ThenInclude(f => f.Pool)
            .Where(h => h.UserId == userId && h.Shares > 0)
            .ToListAsync(ct);

        var hv = holdings.Sum(h => (h.Franchise.Pool?.Price ?? 0m) * h.Shares);
        var total = cash + hv;
        var weightBase = includeCash ? total : hv;

        var fees = await db.Trades
            .Where(t => t.Order.UserId == userId)
            .SumAsync(t => (decimal?)t.Fee, ct) ?? 0m;

        var buyCash = await db.Trades
            .Where(t => t.Order.UserId == userId && t.Side == "buy")
            .SumAsync(t => (decimal?)t.Cash, ct) ?? 0m;
        var sellCash = await db.Trades
            .Where(t => t.Order.UserId == userId && t.Side == "sell")
            .SumAsync(t => (decimal?)t.Cash, ct) ?? 0m;
        var remainingBasis = holdings.Sum(h => h.CostBasis);
        var realized = sellCash - (buyCash - remainingBasis);

        var dtos = holdings.Select(h => ContractMapper.ToHolding(h, weightBase)).ToList();
        return new PortfolioResponse(
            MoneyFormat.Cash(cash),
            MoneyFormat.Cash(hv),
            MoneyFormat.Cash(total),
            MoneyFormat.Cash(fees),
            MoneyFormat.Cash(realized),
            dtos);
    }

    private static int WindowDays(string? window) => window switch
    {
        "week" => 7,
        "month" => 30,
        "season" => 120,
        _ => 120
    };
}
