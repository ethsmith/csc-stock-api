namespace Csx.Domain.Config;

public sealed class MarketOptions
{
    public const string SectionName = "Market";

    public decimal StartingCash { get; set; } = 500.00m;
    public decimal InitialPrice { get; set; } = 10.00m;
    public decimal TotalSupply { get; set; } = 10_000m;
    public int FeeBps { get; set; } = 50;
    public decimal MinOrderCash { get; set; } = 1.00m;
    public decimal PositionCapPct { get; set; } = 0.15m;

    public decimal FeeRate => FeeBps / 10_000m;
}

public sealed class ShockOptions
{
    public const string SectionName = "Shock";

    public decimal Alpha { get; set; } = 0.12m;
    public decimal Beta { get; set; } = 6m;
    public decimal EloDivisor { get; set; } = 40m;
    public string Mode { get; set; } = "Surprise";
}

public sealed class BreakerOptions
{
    public const string SectionName = "Breaker";

    public decimal DailyMovePct { get; set; } = 0.25m;
}

public sealed class DecayOptions
{
    public const string SectionName = "Decay";

    /// <summary>When false, prices never drift toward Elo fair value. Off by default — this is not how listed stocks work.</summary>
    public bool Enabled { get; set; }

    public decimal Lambda { get; set; } = 0.02m;
    public decimal Kappa { get; set; } = 1.2m;
    public decimal PriceFloor { get; set; } = 2.00m;
    public decimal PriceCeiling { get; set; } = 40.00m;

    public bool IsActive => Enabled && Lambda > 0m;
}

public sealed class HaltOptions
{
    public const string SectionName = "Halt";

    public int PreMatchMinutes { get; set; } = 15;
}

public sealed class QuoteOptions
{
    public const string SectionName = "Quote";

    public int TtlSeconds { get; set; } = 15;
}

public sealed class CscCoreOptions
{
    public const string SectionName = "CscCore";

    public string GraphQlUrl { get; set; } = "https://core.playcsc.com/graphql";
    public int? Season { get; set; }
    public string? BearerToken { get; set; }
}

public sealed class ImpliedOpenOptions
{
    public const string SectionName = "ImpliedOpen";

    /// <summary>Replay completed matches from this season through the active season onto current tickers.</summary>
    public int FromSeason { get; set; } = 11;

    /// <summary>Run once after the first franchise sync so launch prices are not a flat $10.</summary>
    public bool Auto { get; set; } = true;

    /// <summary>Rescale the book so the mean opening price stays at Market:InitialPrice.</summary>
    public bool RescaleToInitial { get; set; } = true;

    /// <summary>
    /// One-shot: if decay ticks exist and a restore has not run, re-apply implied open.
    /// Pool cash is revalued so marks match the replay; user cash and share holdings are not touched.
    /// No-ops after the restore tick is written, even if this stays true.
    /// </summary>
    public bool RestoreAfterDecay { get; set; } = true;
}

public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "http://localhost:5233/api/v1/auth/discord/callback";
    public string[] AdminDiscordIds { get; set; } = [];
    public string? DigestWebhookUrl { get; set; }
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string SigningKey { get; set; } = "dev-only-change-me-to-a-32-byte-secret!!";
    public string Issuer { get; set; } = "csx-api";
    public string Audience { get; set; } = "csx-client";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;
}

public sealed class FrontendOptions
{
    public const string SectionName = "Frontend";

    /// <summary>SPA origin, e.g. http://localhost:5173. Empty keeps the OAuth callback as JSON.</summary>
    public string Origin { get; set; } = "http://localhost:5173";
    public string PostLoginPath { get; set; } = "/login";
}

public sealed class CorsSettings
{
    public const string SectionName = "Cors";

    public string[] Origins { get; set; } = ["http://localhost:5173", "http://localhost:3000"];
}
