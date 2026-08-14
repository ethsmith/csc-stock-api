namespace Csx.Infrastructure.Market;

public interface IMarketRealtime
{
    Task MarketHalted(long franchiseId, bool halted, string? reason, DateTimeOffset? resumesAt);
}

public sealed class NoopMarketRealtime : IMarketRealtime
{
    public Task MarketHalted(long franchiseId, bool halted, string? reason, DateTimeOffset? resumesAt) =>
        Task.CompletedTask;
}
