using Csx.Domain.Ledger;
using Csx.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Csx.Infrastructure.Ledger;

public readonly record struct PostingDraft(
    string OwnerType,
    long? OwnerId,
    string AssetType,
    long? AssetId,
    decimal Amount);

public sealed class LedgerService
{
    private readonly CsxDbContext _db;

    public LedgerService(CsxDbContext db) => _db = db;

    public async Task EnsureSystemAccountsAsync(CancellationToken ct)
    {
        await GetOrCreateAccountAsync(OwnerTypes.Mint, null, AssetTypes.Cash, null, ct);
        await GetOrCreateAccountAsync(OwnerTypes.Fees, null, AssetTypes.Cash, null, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<Account> GetOrCreateAccountAsync(
        string ownerType,
        long? ownerId,
        string assetType,
        long? assetId,
        CancellationToken ct)
    {
        var existing = await _db.Accounts.SingleOrDefaultAsync(
            a => a.OwnerType == ownerType
                 && a.OwnerId == ownerId
                 && a.AssetType == assetType
                 && a.AssetId == assetId,
            ct);
        if (existing is not null) return existing;

        var account = new Account
        {
            OwnerType = ownerType,
            OwnerId = ownerId,
            AssetType = assetType,
            AssetId = assetId
        };
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync(ct);
        _db.Balances.Add(new Balance { AccountId = account.Id, Amount = 0m });
        await _db.SaveChangesAsync(ct);
        return account;
    }

    public async Task<decimal> GetBalanceAsync(Account account, CancellationToken ct)
    {
        var bal = await _db.Balances.FindAsync([account.Id], ct);
        return bal?.Amount ?? 0m;
    }

    public async Task<decimal> GetUserCashAsync(long userId, CancellationToken ct)
    {
        var acc = await FindAccountAsync(OwnerTypes.User, userId, AssetTypes.Cash, null, ct);
        if (acc is null) return 0m;
        return await GetBalanceAsync(acc, ct);
    }

    public async Task<Account?> FindAccountAsync(
        string ownerType, long? ownerId, string assetType, long? assetId, CancellationToken ct) =>
        await _db.Accounts.SingleOrDefaultAsync(
            a => a.OwnerType == ownerType
                 && a.OwnerId == ownerId
                 && a.AssetType == assetType
                 && a.AssetId == assetId,
            ct);

    public async Task<Entry> PostAsync(
        string kind,
        string? refType,
        long? refId,
        IReadOnlyList<PostingDraft> drafts,
        CancellationToken ct,
        long? actorUserId = null)
    {
        Invariants.Assert(drafts.Count > 0, "Entry has no postings");
        Invariants.AssertZeroSum(drafts.Select(d => (d.AssetType, d.AssetId, d.Amount)));

        var entry = new Entry
        {
            Kind = kind,
            RefType = refType,
            RefId = refId,
            ActorUserId = actorUserId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Entries.Add(entry);
        await _db.SaveChangesAsync(ct);

        foreach (var draft in drafts)
        {
            if (draft.Amount == 0m) continue;
            var account = await GetOrCreateAccountAsync(
                draft.OwnerType, draft.OwnerId, draft.AssetType, draft.AssetId, ct);

            _db.Postings.Add(new Posting
            {
                EntryId = entry.Id,
                AccountId = account.Id,
                AssetType = draft.AssetType,
                AssetId = draft.AssetId,
                Amount = draft.Amount
            });

            var bal = await _db.Balances.FindAsync([account.Id], ct);
            if (bal is null)
            {
                bal = new Balance { AccountId = account.Id, Amount = 0m };
                _db.Balances.Add(bal);
            }
            bal.Amount += draft.Amount;
        }

        await _db.SaveChangesAsync(ct);
        await _db.Entry(entry).Collection(e => e.Postings).LoadAsync(ct);
        return entry;
    }
}
