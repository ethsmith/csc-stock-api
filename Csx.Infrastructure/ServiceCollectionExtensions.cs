using Csx.Domain.Config;
using Csx.Infrastructure.CscCore;
using Csx.Infrastructure.Data;
using Csx.Infrastructure.Integrity;
using Csx.Infrastructure.Ledger;
using Csx.Infrastructure.Market;
using Csx.Infrastructure.Settlement;
using Csx.Infrastructure.Sync;
using Csx.Infrastructure.Trading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Csx.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCsxInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<MarketOptions>(config.GetSection(MarketOptions.SectionName));
        services.Configure<ShockOptions>(config.GetSection(ShockOptions.SectionName));
        services.Configure<BreakerOptions>(config.GetSection(BreakerOptions.SectionName));
        services.Configure<DecayOptions>(config.GetSection(DecayOptions.SectionName));
        services.Configure<HaltOptions>(config.GetSection(HaltOptions.SectionName));
        services.Configure<QuoteOptions>(config.GetSection(QuoteOptions.SectionName));
        services.Configure<CscCoreOptions>(config.GetSection(CscCoreOptions.SectionName));
        services.Configure<ImpliedOpenOptions>(config.GetSection(ImpliedOpenOptions.SectionName));
        services.Configure<DiscordOptions>(config.GetSection(DiscordOptions.SectionName));
        services.Configure<JwtOptions>(config.GetSection(JwtOptions.SectionName));
        services.Configure<FrontendOptions>(config.GetSection(FrontendOptions.SectionName));
        services.Configure<CorsSettings>(config.GetSection(CorsSettings.SectionName));

        var cs = config.GetConnectionString("Csx")
                 ?? "Host=localhost;Port=5433;Database=csx;Username=csx;Password=csx";
        services.AddDbContext<CsxDbContext>(o => o.UseNpgsql(cs));

        services.AddHttpClient<CscCoreClient>(c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddScoped<LedgerService>();
        services.AddScoped<TradingService>();
        services.AddScoped<SettlementService>();
        services.AddScoped<MarketOpsService>();
        services.AddScoped<ImpliedOpenService>();
        services.AddScoped<FranchiseSyncService>();
        services.AddScoped<MatchIngestService>();
        services.AddScoped<IntegritySweep>();
        services.AddSingleton<SettlementQueue>();
        services.TryAddSingleton<IMarketRealtime, NoopMarketRealtime>();
        return services;
    }
}
