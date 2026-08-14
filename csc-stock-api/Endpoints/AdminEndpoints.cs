using Csx.Api.Contracts;
using Csx.Api.Hubs;
using Csx.Api.Realtime;
using Csx.Domain;
using Csx.Infrastructure.Data;
using Csx.Infrastructure.Integrity;
using Csx.Infrastructure.Market;
using Csx.Infrastructure.Settlement;
using Csx.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace Csx.Api.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/admin").WithTags("Admin").RequireAuthorization("admin");

        g.MapPost("/matches/{id:long}/settle", async (
            long id,
            SettlementService settlement,
            CsxDbContext db,
            MarketBroadcaster realtime,
            CancellationToken ct) =>
        {
            await settlement.HaltMatchPoolsAsync(id, "Settlement in progress", null, ct);
            var result = await settlement.SettleMatchAsync(id, ct);
            if (result.AlreadySettled)
                return Results.Ok(await SettlementBroadcast.BuildAndSendAsync(db, realtime, result, ct, broadcast: false));
            var payload = await SettlementBroadcast.BuildAndSendAsync(db, realtime, result, ct);
            return Results.Ok(payload);
        });

        g.MapPost("/matches/{id:long}/correct", async (
            long id,
            SettlementService settlement,
            CsxDbContext db,
            MarketBroadcaster realtime,
            CancellationToken ct) =>
        {
            var original = await db.Settlements.Where(s => s.MatchId == id && !s.IsCorrection).ToListAsync(ct);
            var correctsId = original.FirstOrDefault()?.Id;
            await settlement.HaltMatchPoolsAsync(id, "Correction in progress", null, ct);
            var result = await settlement.SettleMatchAsync(id, ct, isCorrection: true, correctsId: correctsId);
            var payload = await SettlementBroadcast.BuildAndSendAsync(db, realtime, result, ct);
            return Results.Ok(payload);
        });

        g.MapPost("/franchises", async (CreateFranchiseRequest body, CsxDbContext db, MarketOpsService ops, CancellationToken ct) =>
        {
            var franchise = new Franchise
            {
                Ticker = body.Ticker.ToUpperInvariant(),
                Name = body.Name,
                Division = body.Division,
                ExternalTeamId = body.ExternalTeamId ?? 0,
                IsActive = true,
                Elo = 1000m,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Franchises.Add(franchise);
            await db.SaveChangesAsync(ct);
            await ops.SeedPoolAsync(franchise, ct);
            return Results.Created($"/api/v1/franchises/{franchise.Id}", new { franchise.Id, franchise.Ticker });
        });

        g.MapPost("/franchises/{id:long}/halt", async (
            long id,
            HaltRequest body,
            SettlementService settlement,
            MarketBroadcaster realtime,
            CsxDbContext db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var actor = TokenServiceUserId(ctx);
            if (body.Halted)
                await settlement.HaltFranchiseAsync(id, body.Reason, body.ResumesAt, ct);
            else
                await settlement.ResumeFranchiseAsync(id, ct);
            db.Entries.Add(new Entry
            {
                Kind = EntryKinds.AdminHalt,
                RefType = "franchise",
                RefId = id,
                ActorUserId = actor,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
            await realtime.MarketHalted(id, body.Halted, body.Reason, body.ResumesAt);
            return Results.NoContent();
        });

        g.MapPost("/users/{id:long}/restrict", async (long id, RestrictUserRequest body, CsxDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();
            user.CanTrade = body.CanTrade;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapGet("/integrity", async (IntegritySweep sweep, CancellationToken ct) =>
        {
            var v = await sweep.RunAsync(ct);
            return Results.Ok(new IntegrityResponse(v.Count == 0, v.Select(x => new IntegrityItem(x.Code, x.Detail)).ToList()));
        });

        g.MapPost("/sync", async (FranchiseSyncService sync, CancellationToken ct) =>
        {
            await sync.SyncAsync(ct);
            return Results.Accepted();
        });

        g.MapPost("/implied-open", async (bool? force, ImpliedOpenService implied, CancellationToken ct) =>
        {
            var r = await implied.EnsureAppliedAsync(ct, force == true);
            return Results.Ok(new ImpliedOpenResponse(
                r.Applied,
                r.Skipped,
                r.Reason,
                r.FromSeason,
                r.ThroughSeason,
                r.MatchesUsed,
                r.MeanBeforeRescale.ToString("0.0000"),
                r.MeanAfterRescale.ToString("0.0000"),
                r.Lines.Select(l => new ImpliedOpenLineDto(l.FranchiseId, l.Ticker, l.Key, l.Price, l.Matches)).ToList()));
        });

        g.MapPost("/ingest", async (MatchIngestService ingest, CancellationToken ct) =>
        {
            await ingest.PollAsync(ct);
            return Results.Accepted();
        });

        return app;
    }

    private static long? TokenServiceUserId(HttpContext ctx) => Auth.TokenService.UserId(ctx.User);
}
