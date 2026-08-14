namespace Csx.Domain.Errors;

public static class ErrorCodes
{
    public const string InsufficientFunds = "insufficient_funds";
    public const string InsufficientShares = "insufficient_shares";
    public const string MarketHalted = "market_halted";
    public const string MarketDelisted = "market_delisted";
    public const string QuoteExpired = "quote_expired";
    public const string SlippageExceeded = "slippage_exceeded";
    public const string PositionCapExceeded = "position_cap_exceeded";
    public const string SelfDealingRestricted = "self_dealing_restricted";
    public const string TradingRestricted = "trading_restricted";
    public const string IdempotencyKeyReuse = "idempotency_key_reuse";
    public const string RateLimited = "rate_limited";
    public const string OrderTooSmall = "order_too_small";
    public const string QuoteNotFound = "quote_not_found";
    public const string FranchiseNotFound = "franchise_not_found";
    public const string MatchNotFound = "match_not_found";
    public const string PoolEmpty = "pool_empty";
}

public sealed class MarketException : Exception
{
    public string Code { get; }
    public int Status { get; }
    public IReadOnlyDictionary<string, object?> Meta { get; }

    public MarketException(
        string code,
        string message,
        int status = 409,
        IReadOnlyDictionary<string, object?>? meta = null)
        : base(message)
    {
        Code = code;
        Status = status;
        Meta = meta ?? new Dictionary<string, object?>();
    }

    public static MarketException Halted(long franchiseId, DateTimeOffset? resumesAt = null) =>
        new(
            ErrorCodes.MarketHalted,
            "This market is halted.",
            409,
            new Dictionary<string, object?>
            {
                ["franchiseId"] = franchiseId,
                ["resumesAt"] = resumesAt
            });

    public static MarketException Delisted(long franchiseId) =>
        new(
            ErrorCodes.MarketDelisted,
            "This franchise is no longer active. Positions were redeemed at the last mark.",
            409,
            new Dictionary<string, object?> { ["franchiseId"] = franchiseId });
}
