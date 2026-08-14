using Microsoft.EntityFrameworkCore;

namespace Csx.Infrastructure.Data;

public sealed class CsxDbContext : DbContext
{
    public CsxDbContext(DbContextOptions<CsxDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Org> Orgs => Set<Org>();
    public DbSet<Franchise> Franchises => Set<Franchise>();
    public DbSet<Pool> Pools => Set<Pool>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Balance> Balances => Set<Balance>();
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<Posting> Postings => Set<Posting>();
    public DbSet<Holding> Holdings => Set<Holding>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<PriceTick> PriceTicks => Set<PriceTick>();
    public DbSet<Candle> Candles => Set<Candle>();
    public DbSet<LeagueMatch> Matches => Set<LeagueMatch>();
    public DbSet<Settlement> Settlements => Set<Settlement>();
    public DbSet<EloSnapshot> EloSnapshots => Set<EloSnapshot>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<PortfolioSnapshot> PortfolioSnapshots => Set<PortfolioSnapshot>();
    public DbSet<MarketEvent> MarketEvents => Set<MarketEvent>();
    public DbSet<RosterSeat> RosterSeats => Set<RosterSeat>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<QuoteRecord> Quotes => Set<QuoteRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DiscordId).IsUnique();
            e.Property(x => x.DiscordId).IsRequired();
            e.Property(x => x.DisplayName).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<Org>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ExternalId).IsUnique();
        });

        b.Entity<Franchise>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Ticker).IsUnique();
            e.HasIndex(x => x.ExternalTeamId).IsUnique();
            e.HasOne(x => x.Org).WithMany(o => o.Franchises).HasForeignKey(x => x.OrgId);
            e.Property(x => x.Elo).HasColumnType("numeric(10,2)");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<Pool>(e =>
        {
            e.HasKey(x => x.FranchiseId);
            e.HasOne(x => x.Franchise).WithOne(f => f.Pool).HasForeignKey<Pool>(x => x.FranchiseId);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.ToTable(t => t.HasCheckConstraint("pools_reserves_positive", "\"CashReserve\" > 0 AND \"ShareReserve\" > 0"));
        });

        b.Entity<Account>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OwnerType, x.OwnerId, x.AssetType, x.AssetId }).IsUnique();
        });

        b.Entity<Balance>(e =>
        {
            e.HasKey(x => x.AccountId);
            e.HasOne(x => x.Account).WithOne(a => a.Balance).HasForeignKey<Balance>(x => x.AccountId);
        });

        b.Entity<Entry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<Posting>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Entry).WithMany(en => en.Postings).HasForeignKey(x => x.EntryId);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId);
            e.HasIndex(x => new { x.AccountId, x.Id }).IsDescending(false, true);
            e.HasIndex(x => x.EntryId);
        });

        b.Entity<Holding>(e =>
        {
            e.HasKey(x => new { x.UserId, x.FranchiseId });
            e.HasOne(x => x.User).WithMany(u => u.Holdings).HasForeignKey(x => x.UserId);
            e.HasOne(x => x.Franchise).WithMany().HasForeignKey(x => x.FranchiseId);
            e.ToTable(t => t.HasCheckConstraint("holdings_shares_nonneg", "\"Shares\" >= 0"));
        });

        b.Entity<Order>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasOne(x => x.Franchise).WithMany().HasForeignKey(x => x.FranchiseId);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<Trade>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Order).WithOne(o => o.Trade).HasForeignKey<Trade>(x => x.OrderId);
            e.HasOne(x => x.Entry).WithMany().HasForeignKey(x => x.EntryId);
            e.HasOne(x => x.Franchise).WithMany().HasForeignKey(x => x.FranchiseId);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<PriceTick>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Franchise).WithMany().HasForeignKey(x => x.FranchiseId);
            e.HasIndex(x => new { x.FranchiseId, x.At }).IsDescending(false, true);
            e.Property(x => x.At).HasDefaultValueSql("now()");
        });

        b.Entity<Candle>(e =>
        {
            e.HasKey(x => new { x.FranchiseId, x.Timeframe, x.Bucket });
            e.HasOne<Franchise>().WithMany().HasForeignKey(x => x.FranchiseId);
        });

        b.Entity<LeagueMatch>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ExternalId).IsUnique();
            e.HasOne(x => x.TeamA).WithMany().HasForeignKey(x => x.FranchiseA).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.TeamB).WithMany().HasForeignKey(x => x.FranchiseB).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ScheduledAt);
            e.HasIndex(x => x.Status);
        });

        b.Entity<Settlement>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Match).WithMany(m => m.Settlements).HasForeignKey(x => x.MatchId);
            e.HasOne(x => x.Franchise).WithMany().HasForeignKey(x => x.FranchiseId);
            e.HasIndex(x => new { x.MatchId, x.FranchiseId, x.IsCorrection }).IsUnique();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<EloSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Franchise).WithMany().HasForeignKey(x => x.FranchiseId);
            e.HasIndex(x => new { x.FranchiseId, x.At });
        });

        b.Entity<IdempotencyKey>(e =>
        {
            e.HasKey(x => new { x.UserId, x.Key });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            e.Property(x => x.ResponseBody).HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<PortfolioSnapshot>(e =>
        {
            e.HasKey(x => new { x.UserId, x.Day });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        });

        b.Entity<MarketEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Franchise).WithMany().HasForeignKey(x => x.FranchiseId);
            e.HasIndex(x => new { x.FranchiseId, x.Id }).IsDescending(false, true);
            e.Property(x => x.At).HasDefaultValueSql("now()");
        });

        b.Entity<RosterSeat>(e =>
        {
            e.HasKey(x => new { x.FranchiseId, x.DiscordId });
            e.HasOne(x => x.Franchise).WithMany(f => f.Roster).HasForeignKey(x => x.FranchiseId);
            e.HasIndex(x => x.DiscordId);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => x.TokenHash).IsUnique();
        });

        b.Entity<QuoteRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ExpiresAt);
        });
    }

    public Task<Pool> LockPoolAsync(long franchiseId, CancellationToken ct) =>
        Pools
            .FromSql($"SELECT * FROM pools WHERE \"FranchiseId\" = {franchiseId} FOR UPDATE")
            .SingleAsync(ct);
}
