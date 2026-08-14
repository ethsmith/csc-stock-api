using Microsoft.AspNetCore.SignalR;
using Csx.Infrastructure.Market;

namespace Csx.Api.Hubs;

public sealed class MarketHub : Hub
{
    public const string MarketGroup = "market";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, MarketGroup);
        if (Context.User?.Identity?.IsAuthenticated == true)
        {
            var sub = Context.User.FindFirst("sub")?.Value
                      ?? Context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (sub is not null)
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{sub}");
        }
        await base.OnConnectedAsync();
    }

    public Task JoinFranchise(long franchiseId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"franchise:{franchiseId}");
}

public sealed class MarketBroadcaster : IMarketRealtime
{
    private readonly IHubContext<MarketHub> _hub;
    private readonly Dictionary<long, (DateTimeOffset Last, object Payload)> _throttle = new();
    private readonly object _gate = new();

    public MarketBroadcaster(IHubContext<MarketHub> hub) => _hub = hub;

    public Task PriceUpdated(long franchiseId, decimal price, decimal prevPrice, long seq, DateTimeOffset at)
    {
        var payload = new { franchiseId, price = price.ToString("0.0000"), prevPrice = prevPrice.ToString("0.0000"), seq, at };
        lock (_gate)
        {
            if (_throttle.TryGetValue(franchiseId, out var last) &&
                at - last.Last < TimeSpan.FromMilliseconds(250))
            {
                _throttle[franchiseId] = (last.Last, payload);
                return Task.CompletedTask;
            }
            _throttle[franchiseId] = (at, payload);
        }
        return _hub.Clients.Group(MarketHub.MarketGroup).SendAsync("price.updated", payload);
    }

    public Task MarketHalted(long franchiseId, bool halted, string? reason, DateTimeOffset? resumesAt) =>
        _hub.Clients.Group(MarketHub.MarketGroup).SendAsync("market.halted", new { franchiseId, halted, reason, resumesAt });

    public Task MatchSettled(object payload) =>
        _hub.Clients.Group(MarketHub.MarketGroup).SendAsync("match.settled", payload);

    public Task TradeFilled(long userId, object payload) =>
        _hub.Clients.Group($"user:{userId}").SendAsync("trade.filled", payload);

    public Task PortfolioUpdated(long userId, object payload) =>
        _hub.Clients.Group($"user:{userId}").SendAsync("portfolio.updated", payload);
}
