using Csx.Domain;
using Csx.Infrastructure.CscCore;
using Csx.Infrastructure.Data;
using Csx.Infrastructure.Market;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Csx.Infrastructure.Sync;

public sealed record FranchiseSyncResult(int ActiveCount, IReadOnlyList<long> DelistedIds);

public sealed class FranchiseSyncService
{
    private readonly CsxDbContext _db;
    private readonly CscCoreClient _core;
    private readonly MarketOpsService _ops;
    private readonly IMarketRealtime _realtime;
    private readonly ILogger<FranchiseSyncService> _log;

    public FranchiseSyncService(
        CsxDbContext db,
        CscCoreClient core,
        MarketOpsService ops,
        IMarketRealtime realtime,
        ILogger<FranchiseSyncService> log)
    {
        _db = db;
        _core = core;
        _ops = ops;
        _realtime = realtime;
        _log = log;
    }

    public async Task<FranchiseSyncResult> SyncAsync(CancellationToken ct)
    {
        var franchises = await _core.GetActiveFranchisesAsync(ct);
        var seenTeamIds = new HashSet<long>();
        var seenLineKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var orgDto in franchises)
        {
            if (!long.TryParse(orgDto.Id, out var orgExtId)) continue;
            var org = await _db.Orgs.SingleOrDefaultAsync(o => o.ExternalId == orgExtId, ct);
            if (org is null)
            {
                org = new Org { ExternalId = orgExtId };
                _db.Orgs.Add(org);
            }
            org.Name = orgDto.Name;
            org.Prefix = orgDto.Prefix;
            org.LogoUrl = orgDto.Logo?.Url;
            await _db.SaveChangesAsync(ct);

            foreach (var team in orgDto.Teams.Where(t => t.Active))
            {
                if (!long.TryParse(team.Id, out var teamId)) continue;
                seenTeamIds.Add(teamId);
                var lineKey = Tickers.LineKey(orgDto.Prefix, team.Tier?.Name);
                if (lineKey.Length > 1)
                    seenLineKeys.Add(lineKey);

                var existing = await ResolveFranchiseAsync(org.Id, teamId, orgDto.Prefix, team.Tier?.Name, ct);
                var wasInactive = !existing.IsActive;

                existing.Name = $"{orgDto.Name} — {team.Name}";
                existing.OrgId = org.Id;
                existing.Division = team.Tier?.Name;
                existing.ExternalTeamId = teamId;
                existing.Elo = ComputeTeamElo(team);
                if (wasInactive)
                    await _ops.ReactivateAsync(existing, ct);
                else
                    await _db.SaveChangesAsync(ct);
                await _ops.SeedPoolAsync(existing, ct);
                await ReplaceRosterAsync(existing, team, ct);

                if (wasInactive)
                    _log.LogInformation("Relisted {Ticker} onto Core team {TeamId}", existing.Ticker, teamId);
            }
        }

        var delisted = new List<long>();
        var stale = await _db.Franchises
            .Include(f => f.Org)
            .Include(f => f.Pool)
            .Where(f => f.IsActive)
            .ToListAsync(ct);
        foreach (var f in stale)
        {
            if (StillActive(f, seenTeamIds, seenLineKeys))
                continue;
            await _ops.DelistAsync(f, ct);
            delisted.Add(f.Id);
            await _realtime.MarketHalted(f.Id, true, MarketOpsService.DelistReason, null);
            _log.LogInformation("Delisted {Ticker}; Core no longer fields this line", f.Ticker);
        }

        _log.LogInformation("Synced {Count} CSC teams into the exchange; delisted {Delisted}", seenTeamIds.Count, delisted.Count);
        return new FranchiseSyncResult(seenTeamIds.Count, delisted);
    }

    private async Task<Franchise> ResolveFranchiseAsync(
        long orgId, long teamId, string? prefix, string? tierName, CancellationToken ct)
    {
        var byTeam = await _db.Franchises.SingleOrDefaultAsync(f => f.ExternalTeamId == teamId, ct);
        if (byTeam is not null)
            return byTeam;

        if (!string.IsNullOrWhiteSpace(tierName))
        {
            var byLine = await _db.Franchises
                .Where(f => f.OrgId == orgId && f.Division == tierName)
                .OrderByDescending(f => f.IsActive)
                .ThenBy(f => f.Id)
                .FirstOrDefaultAsync(ct);
            if (byLine is not null)
                return byLine;
        }

        var ticker = Tickers.FromPrefixAndTier(prefix, tierName, teamId);
        var byTicker = await _db.Franchises.SingleOrDefaultAsync(f => f.Ticker == ticker, ct);
        if (byTicker is not null)
            return byTicker;

        ticker = await UniquifyTickerAsync(ticker, ct);
        var created = new Franchise
        {
            ExternalTeamId = teamId,
            Ticker = ticker,
            CreatedAt = DateTimeOffset.UtcNow,
            IsActive = false
        };
        _db.Franchises.Add(created);
        await _db.SaveChangesAsync(ct);
        return created;
    }

    private static bool StillActive(Franchise f, HashSet<long> teamIds, HashSet<string> lineKeys)
    {
        if (teamIds.Contains(f.ExternalTeamId))
            return true;
        var key = Tickers.LineKey(f.Org?.Prefix, f.Division);
        return key.Length > 1 && lineKeys.Contains(key);
    }

    private async Task ReplaceRosterAsync(Franchise franchise, CscTeamDto team, CancellationToken ct)
    {
        var previous = await _db.RosterSeats.Where(r => r.FranchiseId == franchise.Id).ToListAsync(ct);
        var incoming = team.Players
            .Where(p => SignedPlayerTypes.IsRostered(p.Type) && !string.IsNullOrWhiteSpace(p.DiscordId))
            .Select(p => new { DiscordId = p.DiscordId!, p.Id, p.Name, p.Type })
            .DistinctBy(p => p.DiscordId)
            .ToList();

        var incomingIds = incoming.Select(p => p.DiscordId).ToHashSet(StringComparer.Ordinal);
        foreach (var seat in previous.Where(s => !incomingIds.Contains(s.DiscordId)))
        {
            var user = await _db.Users.SingleOrDefaultAsync(u => u.DiscordId == seat.DiscordId, ct);
            if (user is not null)
                await _ops.ForcedLiquidateAsync(user.Id, franchise.Id, ct);
            _db.RosterSeats.Remove(seat);
        }

        foreach (var p in incoming)
        {
            var seat = previous.FirstOrDefault(s => s.DiscordId == p.DiscordId);
            if (seat is null)
            {
                _db.RosterSeats.Add(new RosterSeat
                {
                    FranchiseId = franchise.Id,
                    DiscordId = p.DiscordId,
                    ExternalPlayerId = long.TryParse(p.Id, out var pid) ? pid : 0,
                    PlayerName = p.Name,
                    PlayerType = p.Type ?? ""
                });
            }
            else
            {
                seat.PlayerName = p.Name;
                seat.PlayerType = p.Type ?? "";
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<string> UniquifyTickerAsync(string ticker, CancellationToken ct)
    {
        var candidate = ticker;
        var n = 2;
        while (await _db.Franchises.AnyAsync(f => f.Ticker == candidate, ct))
        {
            var suffix = n.ToString();
            candidate = ticker.Length + suffix.Length <= 5
                ? ticker + suffix
                : ticker[..Math.Max(1, 5 - suffix.Length)] + suffix;
            n++;
        }
        return candidate;
    }

    private static decimal ComputeTeamElo(CscTeamDto team)
    {
        var mmrs = team.Players
            .Where(p => SignedPlayerTypes.IsRostered(p.Type) && p.Mmr is > 0)
            .Select(p => (decimal)p.Mmr!)
            .ToList();
        if (mmrs.Count > 0) return mmrs.Average();
        if (team.Tier?.MmrMin is int min && team.Tier.MmrMax is int max && max > 0)
            return (min + Math.Min(max, min + 400)) / 2m;
        return 1000m;
    }
}
