using Csx.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Csx.Infrastructure.Integrity;

public sealed record IntegrityViolation(string Code, string Detail);

public sealed class IntegritySweep
{
    private readonly CsxDbContext _db;

    public IntegritySweep(CsxDbContext db) => _db = db;

    public async Task<IReadOnlyList<IntegrityViolation>> RunAsync(CancellationToken ct)
    {
        var violations = new List<IntegrityViolation>();

        var unbalanced = await _db.Postings
            .GroupBy(p => new { p.EntryId, p.AssetType, p.AssetId })
            .Where(g => g.Sum(p => p.Amount) != 0)
            .Select(g => new { g.Key.EntryId, g.Key.AssetType, g.Key.AssetId, Sum = g.Sum(p => p.Amount) })
            .ToListAsync(ct);
        foreach (var row in unbalanced)
            violations.Add(new("entry_unbalanced", $"entry {row.EntryId} {row.AssetType}/{row.AssetId} sum={row.Sum}"));

        var rebuilt = await _db.Postings
            .GroupBy(p => p.AccountId)
            .Select(g => new { AccountId = g.Key, Sum = g.Sum(p => p.Amount) })
            .ToListAsync(ct);
        var balances = await _db.Balances.ToDictionaryAsync(b => b.AccountId, b => b.Amount, ct);
        foreach (var row in rebuilt)
        {
            balances.TryGetValue(row.AccountId, out var cached);
            if (cached != row.Sum)
                violations.Add(new("balance_drift", $"account {row.AccountId} cache={cached} ledger={row.Sum}"));
        }

        var pools = await _db.Pools.ToListAsync(ct);
        foreach (var pool in pools)
        {
            var userShares = await _db.Holdings
                .Where(h => h.FranchiseId == pool.FranchiseId)
                .SumAsync(h => (decimal?)h.Shares, ct) ?? 0m;
            if (pool.ShareReserve + userShares != pool.TotalSupply)
            {
                violations.Add(new(
                    "share_supply",
                    $"franchise {pool.FranchiseId}: pool {pool.ShareReserve} + users {userShares} != {pool.TotalSupply}"));
            }
            if (pool.CashReserve <= 0 || pool.ShareReserve <= 0)
                violations.Add(new("empty_pool", $"franchise {pool.FranchiseId} reserves {pool.CashReserve}/{pool.ShareReserve}"));
        }

        var negatives = await _db.Balances.Where(b => b.Amount < 0 && b.Account.OwnerType == OwnerTypes.User)
            .Include(b => b.Account)
            .ToListAsync(ct);
        foreach (var n in negatives)
            violations.Add(new("negative_user_balance", $"account {n.AccountId} amount={n.Amount}"));

        return violations;
    }
}
