using Csx.Domain.Config;

namespace Csx.Api.Contracts;

public static class MoneyFormat
{
    public static string Cash(decimal v) => v.ToString("0.00");
    public static string Shares(decimal v) => v.ToString("0.0000");
    public static string Price(decimal v) => v.ToString("0.0000");
    public static string Pct(decimal v) => v.ToString("0.000000");
}

public sealed record AuthTokenResponse(string AccessToken, int ExpiresIn, string TokenType = "Bearer");

public sealed record MeResponse(
    long Id,
    string DiscordId,
    string DisplayName,
    string? AvatarUrl,
    string Role,
    bool CanTrade,
    IReadOnlyList<long> RestrictedFranchiseIds);

public sealed record FranchiseListItem(
    long Id,
    string Ticker,
    string Name,
    string? Org,
    string? Division,
    bool IsActive,
    bool IsHalted,
    string? HaltReason,
    DateTimeOffset? ResumesAt,
    string Price,
    string Change24h,
    string MarketCap,
    long Seq,
    IReadOnlyList<string> Spark);

public sealed record RosterPlayer(string Name, string DiscordId, string Type);

public sealed record FranchiseDetail(
    long Id,
    string Ticker,
    string Name,
    string? Org,
    string? LogoUrl,
    string? Division,
    bool IsActive,
    bool IsHalted,
    string? HaltReason,
    DateTimeOffset? ResumesAt,
    string Price,
    string CashReserve,
    string ShareReserve,
    string TotalSupply,
    string Fundamental,
    string Elo,
    long Seq,
    FranchiseMatchSummary? NextMatch,
    IReadOnlyList<RosterPlayer> Roster);

public sealed record FranchiseMatchSummary(
    long MatchId,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset? LockoutAt,
    string OpponentTicker,
    string OpponentName);

public sealed record CandleDto(
    DateTimeOffset Bucket,
    string Open,
    string High,
    string Low,
    string Close,
    string VolumeCash);

public sealed record SettlementDto(
    long Id,
    long MatchId,
    long FranchiseId,
    string Ticker,
    string EloBefore,
    string OppEloBefore,
    string ExpectedMargin,
    int ActualMargin,
    string Surprise,
    string ShockRaw,
    string ShockApplied,
    bool ShockClamped,
    string PriceBefore,
    string PriceAfter,
    bool IsCorrection,
    DateTimeOffset At);

public sealed record MarketEventDto(
    long Id,
    string Kind,
    string Headline,
    DateTimeOffset At,
    SettlementDto? Settlement);

public sealed record MoverDto(long FranchiseId, string Ticker, string Name, string Price, string Change);

public sealed record LiveMatchDto(
    long Id,
    string Status,
    DateTimeOffset? ScheduledAt,
    string HomeTicker,
    string AwayTicker,
    int? RoundsHome,
    int? RoundsAway);

public sealed record MarketStatusDto(
    bool GlobalHalted,
    int HaltedMarkets,
    int ActiveMarkets,
    DateTimeOffset? NextLockoutAt,
    IReadOnlyList<LiveMatchDto> LiveMatches);

public sealed record MatchTeamDto(long Id, string Ticker, string Name, string? Division);

public sealed record MatchDto(
    long Id,
    string? ExternalId,
    string Status,
    bool IsBo3,
    string? Map,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset? LockoutAt,
    DateTimeOffset? FinishedAt,
    MatchTeamDto Home,
    MatchTeamDto Away,
    int? RoundsHome,
    int? RoundsAway,
    IReadOnlyList<SettlementDto> Settlements);

public sealed record QuoteRequest(long FranchiseId, string Side, string? CashIn, string? Shares);

public sealed record QuoteResponse(
    string QuoteId,
    long FranchiseId,
    string Side,
    string CashIn,
    string SharesOut,
    string AvgPrice,
    string PriceBefore,
    string PriceAfter,
    string Impact,
    string FeeCash,
    DateTimeOffset ExpiresAt);

public sealed record OrderRequest(string QuoteId, int MaxSlippageBps = 100);

public sealed record OrderResponse(
    long OrderId,
    long FranchiseId,
    string Ticker,
    string Side,
    string Status,
    string? RejectCode,
    string? Shares,
    string? Cash,
    string? Fee,
    string? PriceBefore,
    string? PriceAfter,
    DateTimeOffset CreatedAt);

public sealed record HoldingDto(
    long FranchiseId,
    string Ticker,
    string Name,
    string Shares,
    string CostBasis,
    string AvgCost,
    string Mark,
    string UnrealizedPnl,
    string UnrealizedPnlPct,
    string Weight);

public sealed record PortfolioResponse(
    string Cash,
    string HoldingsValue,
    string TotalValue,
    string FeesPaid,
    string RealizedPnl,
    IReadOnlyList<HoldingDto> Holdings);

public sealed record EquityPoint(DateOnly Day, string TotalValue);

public sealed record LeaderboardRow(
    int Rank,
    long UserId,
    string DisplayName,
    string TotalValue,
    string? Change,
    string? TopHoldingTicker,
    string? TopHoldingName,
    bool IsCaller);

public sealed record LeaderboardResponse(int? CallerRank, string? GapAbove, IReadOnlyList<LeaderboardRow> Rows);

public sealed record PublicPortfolioResponse(
    long UserId,
    string DisplayName,
    IReadOnlyList<HoldingDto> Holdings);

public sealed record TierDto(string Name, string Letter);

public sealed record MarketConfigDto(
    string StartingCash,
    string InitialPrice,
    string TotalSupply,
    int FeeBps,
    string MinOrderCash,
    string PositionCapPct,
    string ShockMode,
    string ShockAlpha,
    string ShockBeta,
    string EloDivisor,
    string BreakerDailyMovePct,
    string DecayLambda,
    string FundamentalFloor,
    string FundamentalCeiling,
    int HaltPreMatchMinutes,
    int QuoteTtlSeconds,
    int? Season,
    IReadOnlyList<TierDto> Tiers);

public sealed record ImpliedOpenLineDto(
    long FranchiseId,
    string Ticker,
    string Key,
    string Price,
    int Matches);

public sealed record ImpliedOpenResponse(
    bool Applied,
    bool Skipped,
    string Reason,
    int FromSeason,
    int ThroughSeason,
    int MatchesUsed,
    string MeanBeforeRescale,
    string MeanAfterRescale,
    IReadOnlyList<ImpliedOpenLineDto> Lines);

public sealed record HaltRequest(bool Halted, string Reason, DateTimeOffset? ResumesAt);

public sealed record CreateFranchiseRequest(
    string Ticker,
    string Name,
    string? Division,
    long? ExternalTeamId);

public sealed record RestrictUserRequest(bool CanTrade);

public sealed record IntegrityResponse(bool Ok, IReadOnlyList<IntegrityItem> Violations);

public sealed record IntegrityItem(string Code, string Detail);

public sealed record MatchSettledSide(
    long FranchiseId,
    string Ticker,
    string Name,
    string ExpectedMargin,
    int ActualMargin,
    string Surprise,
    string Shock,
    string PriceBefore,
    string PriceAfter,
    long Seq);

public sealed record MatchSettledEvent(
    long MatchId,
    string? Map,
    int? RoundsHome,
    int? RoundsAway,
    IReadOnlyList<MatchSettledSide> Franchises);
