using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using Csx.Api.Contracts;
using Csx.Domain.Amm;
using Csx.Infrastructure;
using Csx.Infrastructure.Data;
using Csx.Infrastructure.Ledger;
using Csx.Infrastructure.Market;
using Csx.Infrastructure.Trading;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Csx.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer? Container { get; }

    public bool Available => Container is not null;

    public string ConnectionString => Container?.GetConnectionString()
        ?? throw new InvalidOperationException("Docker is not available");

    public PostgresFixture()
    {
        if (!DockerReachable())
            return;
        Container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("csx_test")
            .WithUsername("csx")
            .WithPassword("csx")
            .Build();
    }

    public async Task InitializeAsync()
    {
        if (Container is not null)
            await Container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (Container is not null)
            await Container.DisposeAsync();
    }

    public static bool DockerReachable()
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint("/var/run/docker.sock"));
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class CsxApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public CsxApiFactory(PostgresFixture pg) => _pg = pg;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Csx", _pg.ConnectionString);
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-must-be-32-bytes!!");
    }

    public async Task InitializeAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CsxDbContext>();
        await db.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<LedgerService>().EnsureSystemAccountsAsync(CancellationToken.None);
    }

    public new async Task DisposeAsync() => await base.DisposeAsync();
}

[CollectionDefinition("pg")]
public sealed class PgCollection : ICollectionFixture<PostgresFixture>;

public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!PostgresFixture.DockerReachable())
            Skip = "Docker socket is not accessible";
    }
}

[Collection("pg")]
public class LedgerAndMarketTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CsxApiFactory _factory = null!;
    private HttpClient _client = null!;

    public LedgerAndMarketTests(PostgresFixture pg) => _pg = pg;

    public async Task InitializeAsync()
    {
        if (!_pg.Available)
            return;
        _factory = new CsxApiFactory(_pg);
        await _factory.InitializeAsync();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    [DockerFact]
    public async Task Signup_grant_and_buy_conserves_shares_and_balances()
    {
        var token = await DevLogin("111");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CsxDbContext>();
        var ops = scope.ServiceProvider.GetRequiredService<MarketOpsService>();
        var franchise = new Franchise
        {
            Ticker = "TESTP",
            Name = "Test Premier",
            ExternalTeamId = 999001,
            IsActive = true,
            Elo = 1000,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Franchises.Add(franchise);
        await db.SaveChangesAsync();
        await ops.SeedPoolAsync(franchise, CancellationToken.None);

        var quote = await _client.PostAsJsonAsync("/api/v1/quotes", new QuoteRequest(franchise.Id, "buy", "50.00", null));
        quote.StatusCode.Should().Be(HttpStatusCode.OK);
        var q = await quote.Content.ReadFromJsonAsync<QuoteResponse>();
        q.Should().NotBeNull();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new OrderRequest(q!.QuoteId, 500))
        };
        req.Headers.Add("Idempotency-Key", "k-1");
        var fill = await _client.SendAsync(req);
        fill.StatusCode.Should().Be(HttpStatusCode.Created);

        var pool = await db.Pools.AsNoTracking().SingleAsync(p => p.FranchiseId == franchise.Id);
        var userShares = await db.Holdings.Where(h => h.FranchiseId == franchise.Id).SumAsync(h => h.Shares);
        (pool.ShareReserve + userShares).Should().Be(pool.TotalSupply);

        var entries = await db.Entries.Include(e => e.Postings).ToListAsync();
        foreach (var e in entries)
        {
            e.Postings.GroupBy(p => (p.AssetType, p.AssetId))
                .All(g => g.Sum(p => p.Amount) == 0m)
                .Should().BeTrue($"entry {e.Id} {e.Kind} unbalanced");
        }
    }

    [DockerFact]
    public async Task Idempotent_order_replays_without_second_fill()
    {
        var token = await DevLogin("222");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CsxDbContext>();
        var ops = scope.ServiceProvider.GetRequiredService<MarketOpsService>();
        var franchise = new Franchise
        {
            Ticker = "IDEMX",
            Name = "Idempotent",
            ExternalTeamId = 999002,
            IsActive = true,
            Elo = 1000,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Franchises.Add(franchise);
        await db.SaveChangesAsync();
        await ops.SeedPoolAsync(franchise, CancellationToken.None);

        var quote = await (await _client.PostAsJsonAsync("/api/v1/quotes", new QuoteRequest(franchise.Id, "buy", "25.00", null)))
            .Content.ReadFromJsonAsync<QuoteResponse>();

        async Task<HttpResponseMessage> Send()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
            {
                Content = JsonContent.Create(new OrderRequest(quote!.QuoteId, 500))
            };
            req.Headers.Add("Idempotency-Key", "same-key");
            return await _client.SendAsync(req);
        }

        var a = await Send();
        var b = await Send();
        a.StatusCode.Should().Be(HttpStatusCode.Created);
        b.StatusCode.Should().Be(HttpStatusCode.Created);
        var bodyA = await a.Content.ReadAsStringAsync();
        var bodyB = await b.Content.ReadAsStringAsync();
        JsonDocument.Parse(bodyA).RootElement.GetProperty("orderId").GetInt64()
            .Should().Be(JsonDocument.Parse(bodyB).RootElement.GetProperty("orderId").GetInt64());
        (await db.Trades.CountAsync(t => t.FranchiseId == franchise.Id)).Should().Be(1);
    }

    [DockerFact]
    public async Task Parallel_buys_match_sequential_replay()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CsxDbContext>();
        var ops = scope.ServiceProvider.GetRequiredService<MarketOpsService>();
        var franchise = new Franchise
        {
            Ticker = "PARX",
            Name = "Parallel",
            ExternalTeamId = 999003,
            IsActive = true,
            Elo = 1000,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Franchises.Add(franchise);
        await db.SaveChangesAsync();
        await ops.SeedPoolAsync(franchise, CancellationToken.None);

        var users = new List<User>();
        for (var i = 0; i < 40; i++)
        {
            var u = new User
            {
                DiscordId = $"par-{i}",
                DisplayName = $"p{i}",
                Role = UserRoles.Member,
                CanTrade = true,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Users.Add(u);
            users.Add(u);
        }
        await db.SaveChangesAsync();
        foreach (var u in users)
            await ops.GrantSignupCashAsync(u.Id, CancellationToken.None);

        await Parallel.ForEachAsync(users, new ParallelOptions { MaxDegreeOfParallelism = 20 }, async (u, ct) =>
        {
            await using var inner = _factory.Services.CreateAsyncScope();
            var trading = inner.ServiceProvider.GetRequiredService<TradingService>();
            var q = await trading.QuoteAsync(u.Id, franchise.Id, TradeSide.Buy, 10m, null, ct);
            await trading.FillAsync(u.Id, q.Id, 10_000, $"par-{u.Id}", "h", ct);
        });

        var pool = await db.Pools.AsNoTracking().SingleAsync(p => p.FranchiseId == franchise.Id);
        var userShares = await db.Holdings.Where(h => h.FranchiseId == franchise.Id).SumAsync(h => h.Shares);
        (pool.ShareReserve + userShares).Should().Be(pool.TotalSupply);

        // sequential replay of 40 $10 buys against the original pool should land on the same R,T
        decimal r = 10.00m * 10_000m, t = 10_000m;
        for (var i = 0; i < 40; i++)
        {
            var fill = AmmMath.Buy(r, t, 10m, 0.005m);
            r = fill.CashReserveAfter;
            t = fill.ShareReserveAfter;
        }
        pool.CashReserve.Should().Be(r);
        pool.ShareReserve.Should().Be(t);
    }

    private async Task<string> DevLogin(string discordId)
    {
        var res = await _client.PostAsJsonAsync("/api/v1/auth/dev", new { discordId, displayName = discordId });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<AuthTokenResponse>();
        return body!.AccessToken;
    }
}
