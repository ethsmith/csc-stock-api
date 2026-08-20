using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Csx.Infrastructure.Data;

public static class OwnerTypes
{
    public const string User = "user";
    public const string Pool = "pool";
    public const string Mint = "mint";
    public const string Fees = "fees";
    public const string Supply = "supply";
}

public static class AssetTypes
{
    public const string Cash = "cash";
    public const string Share = "share";
}

public static class EntryKinds
{
    public const string SignupGrant = "signup_grant";
    public const string PoolSeed = "pool_seed";
    public const string TradeBuy = "trade_buy";
    public const string TradeSell = "trade_sell";
    public const string Revalue = "revalue";
    public const string ForcedLiquidation = "forced_liquidation";
    public const string DelistRedeem = "delist_redeem";
    public const string AdminHalt = "admin_halt";
}

public static class OrderStatuses
{
    public const string Pending = "pending";
    public const string Filled = "filled";
    public const string Rejected = "rejected";
}

public static class MatchStatuses
{
    public const string Scheduled = "scheduled";
    public const string Live = "live";
    public const string Final = "final";
    public const string Settled = "settled";
}

public static class UserRoles
{
    public const string Member = "member";
    public const string Admin = "admin";
}

public static class TickSources
{
    public const string Trade = "trade";
    public const string Settlement = "settlement";
    public const string Decay = "decay";
    public const string Admin = "admin";
    public const string ImpliedOpen = "implied_open";
    public const string ImpliedOpenRestore = "implied_open_restore";
}

public static class EventKinds
{
    public const string Settlement = "settlement";
    public const string Halt = "halt";
    public const string Resume = "resume";
    public const string RosterMove = "roster_move";
    public const string Correction = "correction";
    public const string ImpliedOpen = "implied_open";
    public const string Delist = "delist";
}

[Table("users")]
public sealed class User
{
    public long Id { get; set; }
    public string DiscordId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public string Role { get; set; } = UserRoles.Member;
    public bool CanTrade { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public List<Holding> Holdings { get; set; } = [];
}

[Table("orgs")]
public sealed class Org
{
    public long Id { get; set; }
    public long ExternalId { get; set; }
    public string Name { get; set; } = "";
    public string? Prefix { get; set; }
    public string? LogoUrl { get; set; }
    public List<Franchise> Franchises { get; set; } = [];
}

[Table("franchises")]
public sealed class Franchise
{
    public long Id { get; set; }
    public string Ticker { get; set; } = "";
    public string Name { get; set; } = "";
    public long? OrgId { get; set; }
    public Org? Org { get; set; }
    public long ExternalTeamId { get; set; }
    public string? Division { get; set; }
    public bool IsActive { get; set; } = true;
    public decimal Elo { get; set; } = 1000m;
    public DateTimeOffset CreatedAt { get; set; }
    public Pool? Pool { get; set; }
    public List<RosterSeat> Roster { get; set; } = [];
}

[Table("pools")]
public sealed class Pool
{
    public long FranchiseId { get; set; }
    public Franchise Franchise { get; set; } = null!;
    [Column(TypeName = "numeric(28,10)")]
    public decimal CashReserve { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal ShareReserve { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal TotalSupply { get; set; }
    public long Seq { get; set; }
    public bool IsHalted { get; set; }
    public string? HaltReason { get; set; }
    public DateTimeOffset? ResumesAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [NotMapped]
    public decimal Price => ShareReserve == 0 ? 0 : CashReserve / ShareReserve;
}

[Table("accounts")]
public sealed class Account
{
    public long Id { get; set; }
    public string OwnerType { get; set; } = "";
    public long? OwnerId { get; set; }
    public string AssetType { get; set; } = "";
    public long? AssetId { get; set; }
    public Balance? Balance { get; set; }
}

[Table("balances")]
public sealed class Balance
{
    public long AccountId { get; set; }
    public Account Account { get; set; } = null!;
    [Column(TypeName = "numeric(28,10)")]
    public decimal Amount { get; set; }
}

[Table("entries")]
public sealed class Entry
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public string? RefType { get; set; }
    public long? RefId { get; set; }
    public long? ActorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<Posting> Postings { get; set; } = [];
}

[Table("postings")]
public sealed class Posting
{
    public long Id { get; set; }
    public long EntryId { get; set; }
    public Entry Entry { get; set; } = null!;
    public long AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public string AssetType { get; set; } = "";
    public long? AssetId { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal Amount { get; set; }
}

[Table("holdings")]
public sealed class Holding
{
    public long UserId { get; set; }
    public User User { get; set; } = null!;
    public long FranchiseId { get; set; }
    public Franchise Franchise { get; set; } = null!;
    [Column(TypeName = "numeric(28,10)")]
    public decimal Shares { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal CostBasis { get; set; }
}

[Table("orders")]
public sealed class Order
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public User User { get; set; } = null!;
    public long FranchiseId { get; set; }
    public Franchise Franchise { get; set; } = null!;
    public string Side { get; set; } = "";
    public string? QuoteId { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal? CashIn { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal? SharesIn { get; set; }
    public int MaxSlippageBps { get; set; } = 100;
    public string Status { get; set; } = OrderStatuses.Pending;
    public string? RejectCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Trade? Trade { get; set; }
}

[Table("trades")]
public sealed class Trade
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public long EntryId { get; set; }
    public Entry Entry { get; set; } = null!;
    public long FranchiseId { get; set; }
    public Franchise Franchise { get; set; } = null!;
    public string Side { get; set; } = "";
    [Column(TypeName = "numeric(28,10)")]
    public decimal Shares { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal Cash { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal Fee { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal PriceBefore { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal PriceAfter { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

[Table("price_ticks")]
public sealed class PriceTick
{
    public long Id { get; set; }
    public long FranchiseId { get; set; }
    public Franchise Franchise { get; set; } = null!;
    [Column(TypeName = "numeric(28,10)")]
    public decimal Price { get; set; }
    public long Seq { get; set; }
    public string Source { get; set; } = "";
    public long? RefId { get; set; }
    public DateTimeOffset At { get; set; }
}

[Table("candles")]
public sealed class Candle
{
    public long FranchiseId { get; set; }
    public string Timeframe { get; set; } = "";
    public DateTimeOffset Bucket { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal Open { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal High { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal Low { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal Close { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal VolumeCash { get; set; }
}

[Table("matches")]
public sealed class LeagueMatch
{
    public long Id { get; set; }
    public string? ExternalId { get; set; }
    public long FranchiseA { get; set; }
    public Franchise TeamA { get; set; } = null!;
    public long FranchiseB { get; set; }
    public Franchise TeamB { get; set; } = null!;
    public string? Map { get; set; }
    public int? RoundsA { get; set; }
    public int? RoundsB { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = MatchStatuses.Scheduled;
    public bool IsBo3 { get; set; }
    public List<Settlement> Settlements { get; set; } = [];
}

[Table("settlements")]
public sealed class Settlement
{
    public long Id { get; set; }
    public long MatchId { get; set; }
    public LeagueMatch Match { get; set; } = null!;
    public long FranchiseId { get; set; }
    public Franchise Franchise { get; set; } = null!;
    [Column(TypeName = "numeric(10,2)")]
    public decimal EloBefore { get; set; }
    [Column(TypeName = "numeric(10,2)")]
    public decimal OppEloBefore { get; set; }
    [Column(TypeName = "numeric(10,4)")]
    public decimal ExpectedMargin { get; set; }
    public int ActualMargin { get; set; }
    [Column(TypeName = "numeric(10,4)")]
    public decimal Surprise { get; set; }
    [Column(TypeName = "numeric(10,6)")]
    public decimal ShockRaw { get; set; }
    [Column(TypeName = "numeric(10,6)")]
    public decimal ShockApplied { get; set; }
    public bool ShockClamped { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal PriceBefore { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal PriceAfter { get; set; }
    public bool IsCorrection { get; set; }
    public long? CorrectsId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

[Table("elo_snapshots")]
public sealed class EloSnapshot
{
    public long Id { get; set; }
    public long FranchiseId { get; set; }
    public Franchise Franchise { get; set; } = null!;
    [Column(TypeName = "numeric(10,2)")]
    public decimal Elo { get; set; }
    public long? MatchId { get; set; }
    public DateTimeOffset At { get; set; }
}

[Table("idempotency_keys")]
public sealed class IdempotencyKey
{
    public long UserId { get; set; }
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public int? ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

[Table("portfolio_snapshots")]
public sealed class PortfolioSnapshot
{
    public long UserId { get; set; }
    public DateOnly Day { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal Cash { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal HoldingsValue { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal TotalValue { get; set; }
}

[Table("market_events")]
public sealed class MarketEvent
{
    public long Id { get; set; }
    public long FranchiseId { get; set; }
    public Franchise Franchise { get; set; } = null!;
    public string Kind { get; set; } = "";
    public string Headline { get; set; } = "";
    public string? PayloadJson { get; set; }
    public DateTimeOffset At { get; set; }
}

[Table("roster_seats")]
public sealed class RosterSeat
{
    public long FranchiseId { get; set; }
    public Franchise Franchise { get; set; } = null!;
    public string DiscordId { get; set; } = "";
    public long ExternalPlayerId { get; set; }
    public string PlayerName { get; set; } = "";
    public string PlayerType { get; set; } = "";
}

[Table("refresh_tokens")]
public sealed class RefreshToken
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public User User { get; set; } = null!;
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

[Table("quotes")]
public sealed class QuoteRecord
{
    [Key]
    public string Id { get; set; } = "";
    public long UserId { get; set; }
    public long FranchiseId { get; set; }
    public string Side { get; set; } = "";
    [Column(TypeName = "numeric(28,10)")]
    public decimal CashIn { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal SharesOut { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal AvgPrice { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal PriceBefore { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal PriceAfter { get; set; }
    [Column(TypeName = "numeric(28,10)")]
    public decimal FeeCash { get; set; }
    public long PoolSeq { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
