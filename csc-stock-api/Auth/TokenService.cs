using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Csx.Domain.Config;
using Csx.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Csx.Api.Auth;

public sealed class TokenService
{
    public const string RefreshCookie = "csx_refresh";
    private readonly JwtOptions _jwt;
    private readonly FrontendOptions _frontend;

    public TokenService(IOptions<JwtOptions> jwt, IOptions<FrontendOptions> frontend)
    {
        _jwt = jwt.Value;
        _frontend = frontend.Value;
    }

    public string IssueAccessToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim("discord_id", user.DiscordId),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("can_trade", user.CanTrade ? "true" : "false")
        };
        var token = new JwtSecurityToken(
            _jwt.Issuer,
            _jwt.Audience,
            claims,
            expires: DateTime.UtcNow.AddMinutes(_jwt.AccessTokenMinutes),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<string> IssueRefreshTokenAsync(CsxDbContext db, long userId, CancellationToken ct)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            TokenHash = Hash(raw),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(_jwt.RefreshTokenDays),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return raw;
    }

    public async Task<User?> RotateRefreshAsync(CsxDbContext db, string raw, CancellationToken ct)
    {
        var hash = Hash(raw);
        var row = await db.RefreshTokens.Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row is null || row.RevokedAt is not null || row.ExpiresAt < DateTimeOffset.UtcNow)
            return null;
        row.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return row.User;
    }

    public async Task RevokeAsync(CsxDbContext db, string raw, CancellationToken ct)
    {
        var hash = Hash(raw);
        var row = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row is not null)
        {
            row.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public CookieOptions RefreshCookieOptions(HttpContext ctx)
    {
        var crossSite = IsCrossSite(ctx.Request);
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = ctx.Request.IsHttps || crossSite,
            SameSite = crossSite ? SameSiteMode.None : SameSiteMode.Lax,
            Path = "/api/v1/auth",
            Expires = DateTimeOffset.UtcNow.AddDays(_jwt.RefreshTokenDays)
        };
    }

    private bool IsCrossSite(HttpRequest req)
    {
        if (string.IsNullOrWhiteSpace(_frontend.Origin)) return false;
        if (!Uri.TryCreate(_frontend.Origin, UriKind.Absolute, out var origin)) return false;
        return !string.Equals(origin.Host, req.Host.Host, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(origin.Scheme, req.Scheme, StringComparison.OrdinalIgnoreCase);
    }

    public static long? UserId(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(sub, out var id) ? id : null;
    }

    private static string Hash(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
