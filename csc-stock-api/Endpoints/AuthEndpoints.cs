using Csx.Api.Auth;
using Csx.Api.Contracts;
using Csx.Domain.Config;
using Csx.Infrastructure.Data;
using Csx.Infrastructure.Market;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Csx.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/auth").WithTags("Auth");

        g.MapGet("/discord", (DiscordAuthService discord, HttpContext ctx) =>
        {
            var state = Guid.NewGuid().ToString("N");
            ctx.Response.Cookies.Append("csx_oauth_state", state, new CookieOptions
            {
                HttpOnly = true,
                Secure = ctx.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddMinutes(10)
            });
            return Results.Redirect(discord.BuildAuthorizeUrl(state));
        }).AllowAnonymous();

        g.MapGet("/discord/callback", async (
            string code,
            string? state,
            HttpContext ctx,
            DiscordAuthService discord,
            TokenService tokens,
            CsxDbContext db,
            MarketOpsService ops,
            IOptions<JwtOptions> jwt,
            IOptions<FrontendOptions> frontend,
            CancellationToken ct) =>
        {
            if (!ctx.Request.Cookies.TryGetValue("csx_oauth_state", out var expected) || expected != state)
                return Results.BadRequest("Invalid OAuth state");
            ctx.Response.Cookies.Delete("csx_oauth_state");
            var info = await discord.ExchangeAsync(code, ct);
            var user = await discord.UpsertUserAsync(db, ops, info, ct);

            var origin = frontend.Value.Origin.TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(origin))
            {
                var refresh = await tokens.IssueRefreshTokenAsync(db, user.Id, ct);
                ctx.Response.Cookies.Append(TokenService.RefreshCookie, refresh, tokens.RefreshCookieOptions(ctx));
                var path = frontend.Value.PostLoginPath;
                if (!path.StartsWith('/')) path = "/" + path;
                return Results.Redirect(origin + path);
            }

            return await IssueAsync(ctx, tokens, db, user, jwt.Value, ct);
        }).AllowAnonymous();

        g.MapPost("/refresh", async (
            HttpContext ctx,
            TokenService tokens,
            CsxDbContext db,
            IOptions<JwtOptions> jwt,
            CancellationToken ct) =>
        {
            if (!ctx.Request.Cookies.TryGetValue(TokenService.RefreshCookie, out var raw))
                return Results.Unauthorized();
            var user = await tokens.RotateRefreshAsync(db, raw, ct);
            if (user is null) return Results.Unauthorized();
            return await IssueAsync(ctx, tokens, db, user, jwt.Value, ct);
        }).AllowAnonymous();

        g.MapPost("/logout", async (HttpContext ctx, TokenService tokens, CsxDbContext db, CancellationToken ct) =>
        {
            if (ctx.Request.Cookies.TryGetValue(TokenService.RefreshCookie, out var raw))
                await tokens.RevokeAsync(db, raw, ct);
            ctx.Response.Cookies.Delete(TokenService.RefreshCookie, tokens.RefreshCookieOptions(ctx));
            return Results.NoContent();
        }).AllowAnonymous();

        app.MapGet("/api/v1/me", async (HttpContext ctx, CsxDbContext db, CancellationToken ct) =>
        {
            var id = TokenService.UserId(ctx.User);
            if (id is null) return Results.Unauthorized();
            var user = await db.Users.SingleAsync(u => u.Id == id, ct);
            var restricted = await db.RosterSeats
                .Where(r => r.DiscordId == user.DiscordId)
                .Select(r => r.FranchiseId)
                .ToListAsync(ct);
            return Results.Ok(new MeResponse(
                user.Id, user.DiscordId, user.DisplayName, user.AvatarUrl, user.Role, user.CanTrade, restricted));
        }).WithTags("Auth").RequireAuthorization();

        app.MapPost("/api/v1/auth/dev", async (
            DevLoginRequest body,
            HttpContext ctx,
            IHostEnvironment env,
            CsxDbContext db,
            MarketOpsService ops,
            TokenService tokens,
            IOptions<JwtOptions> jwt,
            IOptions<DiscordOptions> discord,
            CancellationToken ct) =>
        {
            if (!env.IsDevelopment() && !env.IsEnvironment("Testing")) return Results.NotFound();
            var info = new DiscordUserInfo
            {
                Id = body.DiscordId,
                Username = body.DisplayName ?? "dev",
                GlobalName = body.DisplayName
            };
            var user = await new DiscordAuthService(new HttpClient(), discord, jwt)
                .UpsertUserAsync(db, ops, info, ct);
            if (body.Admin)
            {
                user.Role = UserRoles.Admin;
                user.CanTrade = false;
                await db.SaveChangesAsync(ct);
            }
            return await IssueAsync(ctx, tokens, db, user, jwt.Value, ct);
        }).AllowAnonymous().WithTags("Auth");

        return app;
    }

    private static async Task<IResult> IssueAsync(
        HttpContext ctx,
        TokenService tokens,
        CsxDbContext db,
        User user,
        JwtOptions jwt,
        CancellationToken ct)
    {
        var access = tokens.IssueAccessToken(user);
        var refresh = await tokens.IssueRefreshTokenAsync(db, user.Id, ct);
        ctx.Response.Cookies.Append(TokenService.RefreshCookie, refresh, tokens.RefreshCookieOptions(ctx));
        return Results.Ok(new AuthTokenResponse(access, jwt.AccessTokenMinutes * 60));
    }
}

public sealed record DevLoginRequest(string DiscordId, string? DisplayName, bool Admin = false);
