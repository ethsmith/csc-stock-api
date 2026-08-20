using Csx.Domain;
using Csx.Domain.Config;
using Csx.Domain.Ledger;
using Csx.Domain.Shock;
using Csx.Infrastructure.CscCore;
using Csx.Infrastructure.Data;
using Csx.Infrastructure.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Csx.Infrastructure.Market;

public sealed record ImpliedOpenApplyResult(
    bool Applied,
    bool Skipped,
    string Reason,
    int FromSeason,
    int ThroughSeason,
    int MatchesUsed,
    decimal MeanBeforeRescale,
    decimal MeanAfterRescale,
    IReadOnlyList<ImpliedOpenApplyLine> Lines);

public sealed record ImpliedOpenApplyLine(
    long FranchiseId,
    string Ticker,
    string Key,
    string Price,
    int Matches);

public sealed class ImpliedOpenService
{
    private readonly CsxDbContext _db;
    private readonly CscCoreClient _core;
    private readonly LedgerService _ledger;
    private readonly MarketOptions _market;
    private readonly ShockOptions _shock;
    private readonly DecayOptions _decay;
    private readonly ImpliedOpenOptions _options;
    private readonly ILogger<ImpliedOpenService> _log;

    public ImpliedOpenService(
        CsxDbContext db,
        CscCoreClient core,
        LedgerService ledger,
        IOptions<MarketOptions> market,
        IOptions<ShockOptions> shock,
        IOptions<DecayOptions> decay,
        IOptions<ImpliedOpenOptions> options,
        ILogger<ImpliedOpenService> log)
    {
        _db = db;
        _core = core;
        _ledger = ledger;
        _market = market.Value;
        _shock = shock.Value;
        _decay = decay.Value;
        _options = options.Value;
        _log = log;
    }

    public async Task<bool> HasAppliedAsync(CancellationToken ct) =>
        await _db.PriceTicks.AnyAsync(t => t.Source == TickSources.ImpliedOpen, ct);

    /// <summary>
    /// Replays implied open onto current pools once, to unwind decay. Does not debit user cash
    /// or change share holdings — only pool cash (the mark) moves.
    /// </summary>
    public async Task<ImpliedOpenApplyResult> RestoreOnceAfterDecayAsync(CancellationToken ct)
    {
        if (!_options.RestoreAfterDecay)
            return Skip("restore after decay disabled");
        if (await _db.PriceTicks.AnyAsync(t => t.Source == TickSources.ImpliedOpenRestore, ct))
            return Skip("decay restore already applied");
        if (!await _db.PriceTicks.AnyAsync(t => t.Source == TickSources.Decay, ct))
            return Skip("no decay ticks");

        _log.LogInformation(
            "Re-applying implied open to unwind decay; user cash and share holdings are unchanged");
        return await EnsureAppliedAsync(ct, force: true, tickSource: TickSources.ImpliedOpenRestore);
    }

    public async Task<ImpliedOpenApplyResult> EnsureAppliedAsync(
        CancellationToken ct,
        bool force = false,
        string? tickSource = null)
    {
        if (!_options.Auto && !force)
            return Skip("auto disabled");

        if (!force && await HasAppliedAsync(ct))
            return Skip("already applied");

        var traded = await _db.Trades.AnyAsync(ct);
        if (traded && !force)
            return Skip("trades already exist; pass force to revalue anyway");

        var season = await _core.GetActiveSeasonAsync(ct);
        var from = Math.Max(1, _options.FromSeason);
        if (from > season)
            return Skip($"fromSeason {from} is after active season {season}");

        var franchises = await _db.Franchises
            .Include(f => f.Org)
            .Include(f => f.Pool)
            .Where(f => f.IsActive)
            .ToListAsync(ct);
        if (franchises.Count == 0)
            return Skip("no active franchises");

        var byKey = new Dictionary<string, Franchise>(StringComparer.Ordinal);
        foreach (var f in franchises)
        {
            if (string.IsNullOrWhiteSpace(f.Org?.Prefix) || string.IsNullOrWhiteSpace(f.Division))
                continue;
            var key = Tickers.LineKey(f.Org.Prefix, f.Division);
            if (key.Length <= 1) continue;
            byKey.TryAdd(key, f);
        }

        var historical = new List<HistoricalMatch>();
        var fetched = 0;
        for (var s = from; s <= season; s++)
        {
            var rows = await _core.GetCompletedMatchesAsync(s, ct);
            fetched += rows.Count;
            foreach (var dto in rows)
            {
                var mapped = ToHistorical(dto);
                if (mapped is not null)
                    historical.Add(mapped);
            }
        }

        _log.LogInformation(
            "Implied open loaded {Mapped}/{Fetched} completed matches from S{From}-S{To} onto {Lines} tickers",
            historical.Count, fetched, from, season, byKey.Count);

        var mode = _shock.Mode.Equals("SignedScaled", StringComparison.OrdinalIgnoreCase)
            ? ShockMode.SignedScaled
            : ShockMode.Surprise;

        var replay = ImpliedOpenMath.Replay(
            byKey.Keys.ToList(),
            historical,
            _market.InitialPrice,
            _decay.PriceFloor,
            _decay.PriceCeiling,
            _shock.Alpha,
            _shock.Beta,
            _shock.EloDivisor,
            mode,
            _options.RescaleToInitial);

        if (replay.MatchesUsed == 0)
            return Skip("no mapped historical matches");

        var lines = new List<ImpliedOpenApplyLine>();
        foreach (var line in replay.Lines)
        {
            if (!byKey.TryGetValue(line.Key, out var franchise) || franchise.Pool is null)
                continue;

            await ApplyPriceAsync(
                franchise,
                line.Price,
                line.Matches,
                tickSource ?? TickSources.ImpliedOpen,
                ct);
            lines.Add(new ImpliedOpenApplyLine(
                franchise.Id,
                franchise.Ticker,
                line.Key,
                line.Price.ToString("0.0000"),
                line.Matches));
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation(
            "Implied open applied from S{From}-S{To}: {Matches} matches, mean {Before:0.00} -> {After:0.00}, {Lines} tickers",
            from, season, replay.MatchesUsed, replay.MeanBeforeRescale, replay.MeanAfterRescale, lines.Count);

        return new ImpliedOpenApplyResult(
            true,
            false,
            "applied",
            from,
            season,
            replay.MatchesUsed,
            replay.MeanBeforeRescale,
            replay.MeanAfterRescale,
            lines);
    }

    private static ImpliedOpenApplyResult Skip(string reason) =>
        new(false, true, reason, 0, 0, 0, 0m, 0m, []);

    private static HistoricalMatch? ToHistorical(CscMatchDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Home?.Franchise?.Prefix) ||
            string.IsNullOrWhiteSpace(dto.Away?.Franchise?.Prefix))
            return null;

        var homeKey = Tickers.LineKey(dto.Home?.Franchise?.Prefix, dto.Home?.Tier?.Name);
        var awayKey = Tickers.LineKey(dto.Away?.Franchise?.Prefix, dto.Away?.Tier?.Name);
        if (homeKey.Length <= 1 || awayKey.Length <= 1) return null;

        var played = dto.Stats.Where(s => s.HomeScore + s.AwayScore > 0 || s.IsForfeit).ToList();
        if (played.Count == 0) return null;

        var at = dto.CompletedAt ?? dto.ScheduledDate ?? DateTimeOffset.UnixEpoch;
        return new HistoricalMatch(
            homeKey,
            awayKey,
            played.Sum(s => s.HomeScore),
            played.Sum(s => s.AwayScore),
            dto.IsBo3,
            at);
    }

    private async Task ApplyPriceAsync(
        Franchise franchise,
        decimal price,
        int matches,
        string tickSource,
        CancellationToken ct)
    {
        var pool = await _db.LockPoolAsync(franchise.Id, ct);
        var target = ShockMath.Clamp(price, _decay.PriceFloor, _decay.PriceCeiling);
        var delta = (target * pool.ShareReserve) - pool.CashReserve;
        if (delta != 0m)
        {
            await _ledger.PostAsync(
                EntryKinds.Revalue,
                tickSource,
                franchise.Id,
                [
                    new PostingDraft(OwnerTypes.Mint, null, AssetTypes.Cash, null, -delta),
                    new PostingDraft(OwnerTypes.Pool, franchise.Id, AssetTypes.Cash, null, delta)
                ],
                ct);
            pool.CashReserve += delta;
        }

        pool.Seq += 1;
        pool.UpdatedAt = DateTimeOffset.UtcNow;
        _db.PriceTicks.Add(new PriceTick
        {
            FranchiseId = franchise.Id,
            Price = pool.ShareReserve == 0 ? 0 : pool.CashReserve / pool.ShareReserve,
            Seq = pool.Seq,
            Source = tickSource,
            At = DateTimeOffset.UtcNow
        });
        var restore = tickSource == TickSources.ImpliedOpenRestore;
        _db.MarketEvents.Add(new MarketEvent
        {
            FranchiseId = franchise.Id,
            Kind = EventKinds.ImpliedOpen,
            Headline = restore
                ? matches == 0
                    ? $"{franchise.Ticker} restored to ${target:0.00} implied open (holdings unchanged)"
                    : $"{franchise.Ticker} restored to ${target:0.00} implied open from {matches} historical matches (holdings unchanged)"
                : matches == 0
                    ? $"{franchise.Ticker} opened at ${target:0.00} (no mapped history)"
                    : $"{franchise.Ticker} opened at ${target:0.00} from {matches} historical matches",
            At = DateTimeOffset.UtcNow
        });
    }
}
