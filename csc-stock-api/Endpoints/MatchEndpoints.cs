using Csx.Api.Contracts;
using Csx.Domain.Config;
using Csx.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Csx.Api.Endpoints;

public static class MatchEndpoints
{
    public static IEndpointRouteBuilder MapMatchEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/matches").WithTags("Matches");

        g.MapGet("", async (
            string? status,
            long? cursor,
            int? limit,
            CsxDbContext db,
            IOptions<HaltOptions> halt,
            CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 100);
            var q = db.Matches.Include(m => m.TeamA).Include(m => m.TeamB).AsQueryable();
            if (!string.IsNullOrWhiteSpace(status))
                q = q.Where(m => m.Status == status.ToLowerInvariant());
            if (cursor is { } c)
                q = q.Where(m => m.Id < c);

            q = status == MatchStatuses.Settled || status == MatchStatuses.Final
                ? q.OrderByDescending(m => m.FinishedAt).ThenByDescending(m => m.Id)
                : q.OrderBy(m => m.ScheduledAt).ThenBy(m => m.Id);

            var rows = await q.Take(take).ToListAsync(ct);
            var ids = rows.Select(m => m.Id).ToList();
            var settlements = await db.Settlements.Where(s => ids.Contains(s.MatchId)).ToListAsync(ct);
            var byMatch = settlements.GroupBy(s => s.MatchId).ToDictionary(x => x.Key, x => x.ToList());

            return Results.Ok(rows.Select(m =>
            {
                byMatch.TryGetValue(m.Id, out var ss);
                return ContractMapper.ToMatch(m, m.TeamA, m.TeamB, ss ?? [], halt.Value.PreMatchMinutes);
            }));
        }).AllowAnonymous();

        g.MapGet("/{id:long}", async (
            long id,
            CsxDbContext db,
            IOptions<HaltOptions> halt,
            CancellationToken ct) =>
        {
            var m = await db.Matches.Include(x => x.TeamA).Include(x => x.TeamB)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (m is null) return Results.NotFound();
            var settlements = await db.Settlements.Where(s => s.MatchId == id).ToListAsync(ct);
            return Results.Ok(ContractMapper.ToMatch(m, m.TeamA, m.TeamB, settlements, halt.Value.PreMatchMinutes));
        }).AllowAnonymous();

        return app;
    }
}
