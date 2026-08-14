using System.Net.Http.Headers;
using System.Text.Json;
using Csx.Domain.Config;
using Csx.Infrastructure.Data;
using Csx.Infrastructure.Market;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Csx.Api.Auth;

public sealed class DiscordUserInfo
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string? GlobalName { get; set; }
    public string? Avatar { get; set; }
}

public sealed class DiscordAuthService
{
    private readonly HttpClient _http;
    private readonly DiscordOptions _discord;
    private readonly JwtOptions _jwt;

    public DiscordAuthService(HttpClient http, IOptions<DiscordOptions> discord, IOptions<JwtOptions> jwt)
    {
        _http = http;
        _discord = discord.Value;
        _jwt = jwt.Value;
    }

    public string BuildAuthorizeUrl(string state)
    {
        var qs = new QueryString()
            .Add("client_id", _discord.ClientId)
            .Add("redirect_uri", _discord.RedirectUri)
            .Add("response_type", "code")
            .Add("scope", "identify")
            .Add("state", state);
        return "https://discord.com/api/oauth2/authorize" + qs.ToUriComponent();
    }

    public async Task<DiscordUserInfo> ExchangeAsync(string code, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _discord.ClientId,
            ["client_secret"] = _discord.ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _discord.RedirectUri
        });
        using var tokenRes = await _http.PostAsync("https://discord.com/api/oauth2/token", form, ct);
        tokenRes.EnsureSuccessStatusCode();
        using var tokenDoc = JsonDocument.Parse(await tokenRes.Content.ReadAsStringAsync(ct));
        var access = tokenDoc.RootElement.GetProperty("access_token").GetString()
                     ?? throw new InvalidOperationException("Discord token missing");

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/users/@me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var userRes = await _http.SendAsync(req, ct);
        userRes.EnsureSuccessStatusCode();
        var info = JsonSerializer.Deserialize<DiscordUserInfo>(
                       await userRes.Content.ReadAsStringAsync(ct),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidOperationException("Discord profile missing");
        return info;
    }

    public async Task<User> UpsertUserAsync(
        CsxDbContext db,
        MarketOpsService ops,
        DiscordUserInfo info,
        CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.DiscordId == info.Id, ct);
        var avatar = string.IsNullOrEmpty(info.Avatar)
            ? null
            : $"https://cdn.discordapp.com/avatars/{info.Id}/{info.Avatar}.png";
        var display = string.IsNullOrWhiteSpace(info.GlobalName) ? info.Username : info.GlobalName;
        var isAdmin = _discord.AdminDiscordIds.Contains(info.Id);

        if (user is null)
        {
            user = new User
            {
                DiscordId = info.Id,
                DisplayName = display,
                AvatarUrl = avatar,
                Role = isAdmin ? UserRoles.Admin : UserRoles.Member,
                CanTrade = !isAdmin,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            await ops.GrantSignupCashAsync(user.Id, ct);
        }
        else
        {
            user.DisplayName = display;
            user.AvatarUrl = avatar;
            if (isAdmin) user.Role = UserRoles.Admin;
            await db.SaveChangesAsync(ct);
        }

        return user;
    }
}
