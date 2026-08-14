using Csx.Api.Hubs;
using Csx.Api.Realtime;
using Csx.Domain.Config;
using Csx.Infrastructure.Data;
using Csx.Infrastructure.Integrity;
using Csx.Infrastructure.Ledger;
using Csx.Infrastructure.Market;
using Csx.Infrastructure.Settlement;
using Csx.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Csx.Api.Background;

public sealed class SettlementWorker : BackgroundService
{
    private readonly SettlementQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SettlementWorker> _log;

    public SettlementWorker(SettlementQueue queue, IServiceScopeFactory scopes, ILogger<SettlementWorker> log)
    {
        _queue = queue;
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var matchId in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var settlement = scope.ServiceProvider.GetRequiredService<SettlementService>();
                var db = scope.ServiceProvider.GetRequiredService<CsxDbContext>();
                var realtime = scope.ServiceProvider.GetRequiredService<MarketBroadcaster>();
                var digest = scope.ServiceProvider.GetRequiredService<DiscordDigest>();
                await settlement.HaltMatchPoolsAsync(matchId, "Settlement in progress", null, stoppingToken);
                var result = await settlement.SettleMatchAsync(matchId, stoppingToken);
                if (result.AlreadySettled) continue;
                await digest.PostSettlementAsync(db, matchId, stoppingToken);
                await SettlementBroadcast.BuildAndSendAsync(db, realtime, result, stoppingToken);
                _log.LogInformation("Auto-settled match {MatchId}", matchId);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Settlement failed for match {MatchId}", matchId);
            }
        }
    }
}

public sealed class MatchIngestHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MatchIngestHostedService> _log;

    public MatchIngestHostedService(IServiceScopeFactory scopes, ILogger<MatchIngestHostedService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PollOnce(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await PollOnce(stoppingToken);
    }

    private async Task PollOnce(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MatchIngestService>().PollAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Match ingest poll failed");
        }
    }
}

public sealed class HaltSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly HaltOptions _halt;
    private readonly ILogger<HaltSchedulerService> _log;

    public HaltSchedulerService(IServiceScopeFactory scopes, IOptions<HaltOptions> halt, ILogger<HaltSchedulerService> log)
    {
        _scopes = scopes;
        _halt = halt.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<CsxDbContext>();
                var settlement = scope.ServiceProvider.GetRequiredService<SettlementService>();
                var realtime = scope.ServiceProvider.GetRequiredService<MarketBroadcaster>();
                var window = DateTimeOffset.UtcNow.AddMinutes(_halt.PreMatchMinutes);
                var due = await db.Matches
                    .Where(m => (m.Status == MatchStatuses.Scheduled || m.Status == MatchStatuses.Live)
                                && m.ScheduledAt != null
                                && m.ScheduledAt <= window)
                    .ToListAsync(stoppingToken);
                foreach (var match in due)
                {
                    var resumes = match.ScheduledAt;
                    await settlement.HaltMatchPoolsAsync(match.Id, "Pre-match lockout", resumes, stoppingToken);
                    await realtime.MarketHalted(match.FranchiseA, true, "Pre-match lockout", resumes);
                    await realtime.MarketHalted(match.FranchiseB, true, "Pre-match lockout", resumes);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Halt scheduler failed");
            }
        }
    }
}

public sealed class DecayTickHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DecayTickHostedService> _log;

    public DecayTickHostedService(IServiceScopeFactory scopes, ILogger<DecayTickHostedService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MarketOpsService>().DecayTickAsync(stoppingToken);
                _log.LogInformation("Decay tick applied");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Decay tick failed");
            }
        }
    }
}

public sealed class CandleAggregatorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CandleAggregatorService> _log;

    public CandleAggregatorService(IServiceScopeFactory scopes, ILogger<CandleAggregatorService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await AggregateAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await AggregateAsync(stoppingToken);
    }

    private async Task AggregateAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CsxDbContext>();
            var since = DateTimeOffset.UtcNow.AddHours(-24);
            var ticks = await db.PriceTicks.Where(t => t.At >= since).ToListAsync(stoppingToken);
            foreach (var group in ticks.GroupBy(t => t.FranchiseId))
            {
                var list = group.ToList();
                foreach (var bucket in DistinctFloors(list, TimeSpan.FromMinutes(5)))
                    Upsert(db, group.Key, "5m", bucket, list);
                foreach (var bucket in DistinctFloors(list, TimeSpan.FromHours(1)))
                    Upsert(db, group.Key, "1h", bucket, list);
                foreach (var bucket in DistinctFloors(list, TimeSpan.FromDays(1)))
                    Upsert(db, group.Key, "1d", bucket, list);
            }
            await db.SaveChangesAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Candle aggregation failed");
        }
    }

    private static IEnumerable<DateTimeOffset> DistinctFloors(IReadOnlyList<PriceTick> ticks, TimeSpan size) =>
        ticks.Select(t => Floor(t.At, size)).Distinct();

    private static DateTimeOffset Floor(DateTimeOffset at, TimeSpan size)
    {
        var utc = at.ToUniversalTime();
        var ticks = utc.UtcTicks - utc.UtcTicks % size.Ticks;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static void Upsert(CsxDbContext db, long franchiseId, string tf, DateTimeOffset bucket, IEnumerable<PriceTick> ticks)
    {
        var inBucket = ticks.Where(t =>
        {
            var size = tf switch { "1h" => TimeSpan.FromHours(1), "1d" => TimeSpan.FromDays(1), _ => TimeSpan.FromMinutes(5) };
            return t.At >= bucket && t.At < bucket + size;
        }).OrderBy(t => t.At).ToList();
        if (inBucket.Count == 0) return;

        var existing = db.Candles.Local.FirstOrDefault(c =>
            c.FranchiseId == franchiseId && c.Timeframe == tf && c.Bucket == bucket);
        existing ??= db.Candles.Find(franchiseId, tf, bucket);
        var open = existing?.Open ?? inBucket[0].Price;
        var close = inBucket[^1].Price;
        var high = Math.Max(existing?.High ?? inBucket[0].Price, inBucket.Max(t => t.Price));
        var low = Math.Min(existing?.Low ?? inBucket[0].Price, inBucket.Min(t => t.Price));
        if (existing is null)
        {
            db.Candles.Add(new Candle
            {
                FranchiseId = franchiseId,
                Timeframe = tf,
                Bucket = bucket,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                VolumeCash = 0
            });
        }
        else
        {
            existing.High = high;
            existing.Low = low;
            existing.Close = close;
        }
    }
}

public sealed class DailyJobsService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DailyJobsService> _log;

    public DailyJobsService(IServiceScopeFactory scopes, ILogger<DailyJobsService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        var lastSnap = DateOnly.MinValue;
        var lastSweep = DateOnly.MinValue;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTimeOffset.UtcNow;
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            if (now.Hour == 4 && now.Minute is >= 0 and < 2 && lastSnap != today)
            {
                lastSnap = today;
                await SnapshotAsync(stoppingToken);
            }
            if (now.Hour == 4 && now.Minute is >= 30 and < 32 && lastSweep != today)
            {
                lastSweep = today;
                await SweepAsync(stoppingToken);
            }
        }
    }

    private async Task SnapshotAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CsxDbContext>();
            var ledger = scope.ServiceProvider.GetRequiredService<LedgerService>();
            var day = DateOnly.FromDateTime(DateTime.UtcNow);
            var users = await db.Users.Select(u => u.Id).ToListAsync(ct);
            foreach (var userId in users)
            {
                var cash = await ledger.GetUserCashAsync(userId, ct);
                var holdings = await db.Holdings.Include(h => h.Franchise).ThenInclude(f => f.Pool)
                    .Where(h => h.UserId == userId)
                    .ToListAsync(ct);
                var hv = holdings.Sum(h => (h.Franchise.Pool?.Price ?? 0m) * h.Shares);
                var existing = await db.PortfolioSnapshots.FindAsync([userId, day], ct);
                if (existing is null)
                {
                    db.PortfolioSnapshots.Add(new PortfolioSnapshot
                    {
                        UserId = userId,
                        Day = day,
                        Cash = cash,
                        HoldingsValue = hv,
                        TotalValue = cash + hv
                    });
                }
                else
                {
                    existing.Cash = cash;
                    existing.HoldingsValue = hv;
                    existing.TotalValue = cash + hv;
                }
            }
            await db.SaveChangesAsync(ct);
            _log.LogInformation("Portfolio snapshots written for {Day}", day);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Portfolio snapshot failed");
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var sweep = scope.ServiceProvider.GetRequiredService<IntegritySweep>();
            var v = await sweep.RunAsync(ct);
            if (v.Count > 0)
                _log.LogError("Integrity sweep found {Count} violations: {Detail}", v.Count, string.Join(" | ", v.Select(x => x.Detail)));
            else
                _log.LogInformation("Integrity sweep clean");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Integrity sweep failed");
        }
    }
}
