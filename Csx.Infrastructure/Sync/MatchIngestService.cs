using System.Threading.Channels;
using Csx.Infrastructure.CscCore;
using Csx.Infrastructure.Data;
using Csx.Infrastructure.Market;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Csx.Infrastructure.Sync;

public sealed class SettlementQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(long matchId, CancellationToken ct) => _channel.Writer.WriteAsync(matchId, ct);

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public sealed class MatchIngestService
{
    private readonly CsxDbContext _db;
    private readonly CscCoreClient _core;
    private readonly FranchiseSyncService _franchises;
    private readonly ImpliedOpenService _impliedOpen;
    private readonly SettlementQueue _queue;
    private readonly ILogger<MatchIngestService> _log;

    public MatchIngestService(
        CsxDbContext db,
        CscCoreClient core,
        FranchiseSyncService franchises,
        ImpliedOpenService impliedOpen,
        SettlementQueue queue,
        ILogger<MatchIngestService> log)
    {
        _db = db;
        _core = core;
        _franchises = franchises;
        _impliedOpen = impliedOpen;
        _queue = queue;
        _log = log;
    }

    public async Task PollAsync(CancellationToken ct)
    {
        var season = await _core.GetActiveSeasonAsync(ct);
        await _franchises.SyncAsync(ct);
        var implied = await _impliedOpen.EnsureAppliedAsync(ct);

        if (implied.Applied)
        {
            var completed = await _core.GetMatchesAsync(season, "COMPLETED", 0, ct);
            foreach (var dto in completed)
            {
                var match = await UpsertMatchAsync(dto, enqueueIfFinal: false, ct);
                if (match is not null && match.Status == MatchStatuses.Final)
                    match.Status = MatchStatuses.Settled;
            }
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            var completed = await _core.GetMatchesAsync(season, "COMPLETED", 50, ct);
            foreach (var dto in completed)
                await UpsertMatchAsync(dto, enqueueIfFinal: true, ct);
        }

        var upcoming = await _core.GetUpcomingMatchesAsync(season, ct);
        foreach (var dto in upcoming)
            await UpsertMatchAsync(dto, enqueueIfFinal: false, ct);
    }

    public async Task<LeagueMatch?> UpsertMatchAsync(CscMatchDto dto, bool enqueueIfFinal, CancellationToken ct)
    {
        if (dto.Home is null || dto.Away is null) return null;
        if (!long.TryParse(dto.Home.Id, out var homeTeam) || !long.TryParse(dto.Away.Id, out var awayTeam))
            return null;

        var a = await _db.Franchises.SingleOrDefaultAsync(f => f.ExternalTeamId == homeTeam, ct);
        var b = await _db.Franchises.SingleOrDefaultAsync(f => f.ExternalTeamId == awayTeam, ct);
        if (a is null || b is null)
        {
            _log.LogWarning("Skipping Core match {Id}; missing franchise mapping", dto.Id);
            return null;
        }
        if (!a.IsActive || !b.IsActive)
            enqueueIfFinal = false;

        var played = dto.Stats.Where(s => s.HomeScore + s.AwayScore > 0 || s.IsForfeit).ToList();
        var roundsA = played.Sum(s => s.HomeScore);
        var roundsB = played.Sum(s => s.AwayScore);
        var mapName = string.Join(",", played.Select(s => s.MapName).Where(n => !string.IsNullOrWhiteSpace(n)));

        var match = await _db.Matches.SingleOrDefaultAsync(m => m.ExternalId == dto.Id, ct);
        var wasSettled = match?.Status == MatchStatuses.Settled;
        if (match is null)
        {
            match = new LeagueMatch { ExternalId = dto.Id };
            _db.Matches.Add(match);
        }

        match.FranchiseA = a.Id;
        match.FranchiseB = b.Id;
        match.Map = mapName.Length == 0 ? null : mapName;
        match.IsBo3 = dto.IsBo3;
        match.ScheduledAt = dto.ScheduledDate;
        match.FinishedAt = dto.CompletedAt;
        if (played.Count > 0)
        {
            match.RoundsA = roundsA;
            match.RoundsB = roundsB;
        }

        if (dto.CompletedAt is not null)
            match.Status = wasSettled ? MatchStatuses.Settled : MatchStatuses.Final;
        else if (dto.Stats.Count > 0)
            match.Status = MatchStatuses.Live;
        else
            match.Status = MatchStatuses.Scheduled;

        await _db.SaveChangesAsync(ct);

        if (enqueueIfFinal && match.Status == MatchStatuses.Final && !wasSettled)
            await _queue.EnqueueAsync(match.Id, ct);

        return match;
    }
}
