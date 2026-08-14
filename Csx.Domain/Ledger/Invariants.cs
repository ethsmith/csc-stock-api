namespace Csx.Domain.Ledger;

public sealed class InvariantViolationException : Exception
{
    public InvariantViolationException(string message) : base(message) { }
}

public static class Invariants
{
    public static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvariantViolationException(message);
    }

    public static void AssertZeroSum(IEnumerable<(string AssetType, long? AssetId, decimal Amount)> postings)
    {
        var groups = postings.GroupBy(p => (p.AssetType, p.AssetId));
        foreach (var g in groups)
        {
            var sum = g.Sum(p => p.Amount);
            Assert(sum == 0m, $"Entry does not sum to zero for {g.Key.AssetType}/{g.Key.AssetId}: {sum}");
        }
    }

    public static void AssertShareSupply(decimal poolShares, decimal userShares, decimal totalSupply)
    {
        Assert(
            poolShares + userShares == totalSupply,
            $"Share supply broken: pool {poolShares} + users {userShares} != {totalSupply}");
    }

    public static void AssertNonNegative(decimal amount, string label)
    {
        Assert(amount >= 0m, $"{label} went negative: {amount}");
    }
}
