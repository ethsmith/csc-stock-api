using System.Net;
using System.Text;
using Csx.Api.Auth;
using Csx.Api.Background;
using Csx.Api.Endpoints;
using Csx.Api.Hubs;
using Csx.Domain.Config;
using Csx.Domain.Errors;
using Csx.Domain.Ledger;
using Csx.Infrastructure;
using Csx.Infrastructure.Data;
using Csx.Infrastructure.Ledger;
using Csx.Infrastructure.Market;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Prometheus;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
    builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

    builder.Services.AddCsxInfrastructure(builder.Configuration);
    builder.Services.AddMemoryCache();
    builder.Services.AddHttpClient<DiscordAuthService>();
    builder.Services.AddSingleton<TokenService>();
    builder.Services.AddSingleton<MarketBroadcaster>();
    builder.Services.Replace(ServiceDescriptor.Singleton<IMarketRealtime>(sp => sp.GetRequiredService<MarketBroadcaster>()));
    builder.Services.AddSingleton<DiscordDigest>();
    builder.Services.AddSignalR();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor
            | ForwardedHeaders.XForwardedProto
            | ForwardedHeaders.XForwardedHost;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        o.KnownProxies.Add(IPAddress.Loopback);
        o.KnownProxies.Add(IPAddress.IPv6Loopback);
        o.ForwardLimit = 1;
    });

    var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey))
            };
            o.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    var access = ctx.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(access) && ctx.HttpContext.Request.Path.StartsWithSegments("/hub"))
                        ctx.Token = access;
                    return Task.CompletedTask;
                }
            };
        });
    builder.Services.AddAuthorization(o =>
    {
        o.AddPolicy("admin", p => p.RequireRole(UserRoles.Admin));
        o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });

    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = 429;
        o.AddFixedWindowLimiter("orders", w =>
        {
            w.PermitLimit = 30;
            w.Window = TimeSpan.FromMinutes(1);
        });
        o.AddFixedWindowLimiter("quotes", w =>
        {
            w.PermitLimit = 120;
            w.Window = TimeSpan.FromMinutes(1);
        });
        o.OnRejected = async (ctx, ct) =>
        {
            ctx.HttpContext.Response.ContentType = "application/problem+json";
            await ctx.HttpContext.Response.WriteAsJsonAsync(new
            {
                type = "https://csx.internal/errors/rate-limited",
                title = "Too many requests",
                status = 429,
                code = ErrorCodes.RateLimited
            }, ct);
        };
    });

    if (!builder.Environment.IsEnvironment("Testing"))
    {
        builder.Services.AddHostedService<SettlementWorker>();
        builder.Services.AddHostedService<MatchIngestHostedService>();
        builder.Services.AddHostedService<HaltSchedulerService>();
        builder.Services.AddHostedService<DecayTickHostedService>();
        builder.Services.AddHostedService<CandleAggregatorService>();
        builder.Services.AddHostedService<DailyJobsService>();
    }

    var cors = builder.Configuration.GetSection(CorsSettings.SectionName).Get<CorsSettings>() ?? new CorsSettings();
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        p.WithOrigins(cors.Origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));

    var app = builder.Build();

    app.UseForwardedHeaders();
    app.UseSerilogRequestLogging();
    app.UseExceptionHandler(err => err.Run(async ctx =>
    {
        var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
        ctx.Response.ContentType = "application/problem+json";
        if (ex is MarketException mx)
        {
            ctx.Response.StatusCode = mx.Status;
            await ctx.Response.WriteAsJsonAsync(new
            {
                type = $"https://csx.internal/errors/{mx.Code.Replace('_', '-')}",
                title = mx.Message,
                status = mx.Status,
                code = mx.Code,
                detail = mx.Message,
                meta = mx.Meta
            });
            return;
        }
        if (ex is InvariantViolationException)
        {
            Log.Error(ex, "Invariant violation");
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new
            {
                type = "https://csx.internal/errors/invariant-violation",
                title = "Ledger invariant failed; trade rolled back",
                status = 500,
                code = "invariant_violation"
            });
            return;
        }
        Log.Error(ex, "Unhandled error");
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new
        {
            type = "https://csx.internal/errors/internal",
            title = "Internal error",
            status = 500,
            code = "internal"
        });
    }));

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseHttpMetrics();

    app.MapAuthEndpoints();
    app.MapMarketEndpoints();
    app.MapMatchEndpoints();
    app.MapTradingEndpoints();
    app.MapPortfolioEndpoints();
    app.MapAdminEndpoints();
    app.MapHub<MarketHub>("/hub/market").AllowAnonymous();
    app.MapMetrics().AllowAnonymous();
    app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

    if (!app.Environment.IsEnvironment("Testing"))
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CsxDbContext>();
        await db.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<LedgerService>().EnsureSystemAccountsAsync(CancellationToken.None);
    }

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated");
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program;
