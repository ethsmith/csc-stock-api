using System.Text.Json;
using Csx.Api.Auth;
using Csx.Api.Contracts;
using Csx.Api.Hubs;
using Csx.Domain.Amm;
using Csx.Domain.Errors;
using Csx.Infrastructure.Data;
using Csx.Infrastructure.Ledger;
using Csx.Infrastructure.Trading;
using Microsoft.EntityFrameworkCore;

namespace Csx.Api.Endpoints;

public static class TradingEndpoints
{
    public static IEndpointRouteBuilder MapTradingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1").WithTags("Trading").RequireAuthorization();

        g.MapPost("/quotes", async (
            QuoteRequest body,
            HttpContext ctx,
            TradingService trading,
        CancellationToken ct) =>
        {
            var userId = TokenService.UserId(ctx.User) ?? throw new MarketException(ErrorCodes.TradingRestricted, "Unauthorized", 401);
            if (!Enum.TryParse<TradeSide>(body.Side, true, out var side))
                return Results.BadRequest("side must be buy or sell");
            decimal? cash = ParseDec(body.CashIn);
            decimal? shares = ParseDec(body.Shares);
            var quote = await trading.QuoteAsync(userId, body.FranchiseId, side, cash, shares, ct);
            var impact = quote.PriceBefore == 0m ? 0m : (quote.PriceAfter - quote.PriceBefore) / quote.PriceBefore;
            return Results.Ok(new QuoteResponse(
                quote.Id, quote.FranchiseId, quote.Side,
                MoneyFormat.Cash(quote.CashIn),
                MoneyFormat.Shares(quote.SharesOut),
                MoneyFormat.Price(quote.AvgPrice),
                MoneyFormat.Price(quote.PriceBefore),
                MoneyFormat.Price(quote.PriceAfter),
                MoneyFormat.Pct(impact),
                MoneyFormat.Cash(quote.FeeCash),
                quote.ExpiresAt));
        }).RequireRateLimiting("quotes");

        g.MapPost("/orders", async (
            OrderRequest body,
            HttpContext ctx,
            TradingService trading,
            LedgerService ledger,
            CsxDbContext db,
            MarketBroadcaster realtime,
            CancellationToken ct) =>
        {
            var userId = TokenService.UserId(ctx.User) ?? throw new MarketException(ErrorCodes.TradingRestricted, "Unauthorized", 401);
            if (!ctx.Request.Headers.TryGetValue("Idempotency-Key", out var key) || string.IsNullOrWhiteSpace(key))
                return Results.BadRequest("Idempotency-Key header is required");

            ctx.Request.EnableBuffering();
            var hash = TradingService.HashRequest(JsonSerializer.Serialize(body));
            var order = await trading.FillAsync(userId, body.QuoteId, body.MaxSlippageBps, key.ToString(), hash, ct);
            var trade = order.Trade ?? await db.Trades.SingleAsync(t => t.OrderId == order.Id, ct);

            var seq = await db.Pools.Where(p => p.FranchiseId == order.FranchiseId).Select(p => p.Seq).SingleAsync(ct);
            var ticker = await db.Franchises.Where(f => f.Id == order.FranchiseId).Select(f => f.Ticker).SingleAsync(ct);
            await realtime.PriceUpdated(order.FranchiseId, trade.PriceAfter, trade.PriceBefore, seq, trade.CreatedAt);
            await realtime.TradeFilled(userId, new
            {
                orderId = order.Id,
                franchiseId = order.FranchiseId,
                ticker,
                side = order.Side,
                shares = MoneyFormat.Shares(trade.Shares),
                cash = MoneyFormat.Cash(trade.Cash),
                price = MoneyFormat.Price(trade.PriceAfter)
            });
            var portfolio = await PortfolioEndpoints.BuildPortfolio(db, ledger, userId, includeCash: true, ct);
            await realtime.PortfolioUpdated(userId, portfolio);

            return Results.Created($"/api/v1/orders/{order.Id}", ToDto(order, trade, ticker));
        }).RequireRateLimiting("orders");

        g.MapGet("/orders", async (HttpContext ctx, long? cursor, CsxDbContext db, CancellationToken ct) =>
        {
            var userId = TokenService.UserId(ctx.User) ?? throw new MarketException(ErrorCodes.TradingRestricted, "Unauthorized", 401);
            var q = db.Orders.Include(o => o.Trade).Include(o => o.Franchise).Where(o => o.UserId == userId);
            if (cursor is { } c) q = q.Where(o => o.Id < c);
            var rows = await q.OrderByDescending(o => o.Id).Take(50).ToListAsync(ct);
            return Results.Ok(rows.Select(o => ToDto(o, o.Trade, o.Franchise.Ticker)));
        });

        return app;
    }

    private static OrderResponse ToDto(Order order, Trade? trade, string ticker) => new(
        order.Id,
        order.FranchiseId,
        ticker,
        order.Side,
        order.Status,
        order.RejectCode,
        trade is null ? null : MoneyFormat.Shares(trade.Shares),
        trade is null ? null : MoneyFormat.Cash(trade.Cash),
        trade is null ? null : MoneyFormat.Cash(trade.Fee),
        trade is null ? null : MoneyFormat.Price(trade.PriceBefore),
        trade is null ? null : MoneyFormat.Price(trade.PriceAfter),
        order.CreatedAt);

    private static decimal? ParseDec(string? s) =>
        decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}
