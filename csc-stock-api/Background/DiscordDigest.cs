using System.Text;
using System.Text.Json;
using Csx.Domain.Config;
using Csx.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Csx.Api.Background;

public sealed class DiscordDigest
{
    private readonly IHttpClientFactory _http;
    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordDigest> _log;

    public DiscordDigest(IHttpClientFactory http, IOptions<DiscordOptions> options, ILogger<DiscordDigest> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public async Task PostSettlementAsync(CsxDbContext db, long matchId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.DigestWebhookUrl))
            return;

        var settlements = await db.Settlements
            .Include(s => s.Franchise)
            .Where(s => s.MatchId == matchId)
            .OrderByDescending(s => s.ShockApplied)
            .ToListAsync(ct);
        if (settlements.Count == 0) return;

        var lines = settlements.Select(s =>
        {
            var pct = s.ShockApplied * 100m;
            var arrow = pct >= 0 ? "▲" : "▼";
            return $"{arrow} `{s.Franchise.Ticker}` {s.Franchise.Name}: {pct:+0.00;-0.00}% (surprise {s.Surprise:+0.0;-0.0})";
        });
        var payload = JsonSerializer.Serialize(new
        {
            content = $"**Market movers — match {matchId}**\n" + string.Join("\n", lines)
        });
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var res = await _http.CreateClient().PostAsync(_options.DigestWebhookUrl, content, ct);
            if (!res.IsSuccessStatusCode)
                _log.LogWarning("Discord digest webhook returned {Status}", (int)res.StatusCode);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Discord digest failed");
        }
    }
}
