using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Csx.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerType = table.Column<string>(type: "text", nullable: false),
                    OwnerId = table.Column<long>(type: "bigint", nullable: true),
                    AssetType = table.Column<string>(type: "text", nullable: false),
                    AssetId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    RefType = table.Column<string>(type: "text", nullable: true),
                    RefId = table.Column<long>(type: "bigint", nullable: true),
                    ActorUserId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "orgs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExternalId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Prefix = table.Column<string>(type: "text", nullable: true),
                    LogoUrl = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orgs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "quotes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    FranchiseId = table.Column<long>(type: "bigint", nullable: false),
                    Side = table.Column<string>(type: "text", nullable: false),
                    CashIn = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    SharesOut = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    AvgPrice = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    PriceBefore = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    PriceAfter = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    FeeCash = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    PoolSeq = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DiscordId = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    AvatarUrl = table.Column<string>(type: "text", nullable: true),
                    Role = table.Column<string>(type: "text", nullable: false),
                    CanTrade = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "balances",
                columns: table => new
                {
                    AccountId = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(28,10)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_balances", x => x.AccountId);
                    table.ForeignKey(
                        name: "FK_balances_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "postings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EntryId = table.Column<long>(type: "bigint", nullable: false),
                    AccountId = table.Column<long>(type: "bigint", nullable: false),
                    AssetType = table.Column<string>(type: "text", nullable: false),
                    AssetId = table.Column<long>(type: "bigint", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(28,10)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_postings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_postings_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_postings_entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "franchises",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Ticker = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    OrgId = table.Column<long>(type: "bigint", nullable: true),
                    ExternalTeamId = table.Column<long>(type: "bigint", nullable: false),
                    Division = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Elo = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_franchises", x => x.Id);
                    table.ForeignKey(
                        name: "FK_franchises_orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "orgs",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    RequestHash = table.Column<string>(type: "text", nullable: false),
                    ResponseStatus = table.Column<int>(type: "integer", nullable: true),
                    ResponseBody = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_keys", x => new { x.UserId, x.Key });
                    table.ForeignKey(
                        name: "FK_idempotency_keys_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "portfolio_snapshots",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Cash = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    HoldingsValue = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    TotalValue = table.Column<decimal>(type: "numeric(28,10)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portfolio_snapshots", x => new { x.UserId, x.Day });
                    table.ForeignKey(
                        name: "FK_portfolio_snapshots_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "candles",
                columns: table => new
                {
                    FranchiseId = table.Column<long>(type: "bigint", nullable: false),
                    Timeframe = table.Column<string>(type: "text", nullable: false),
                    Bucket = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Open = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    High = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    Low = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    Close = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    VolumeCash = table.Column<decimal>(type: "numeric(28,10)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candles", x => new { x.FranchiseId, x.Timeframe, x.Bucket });
                    table.ForeignKey(
                        name: "FK_candles_franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "elo_snapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FranchiseId = table.Column<long>(type: "bigint", nullable: false),
                    Elo = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    MatchId = table.Column<long>(type: "bigint", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_elo_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_elo_snapshots_franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "holdings",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    FranchiseId = table.Column<long>(type: "bigint", nullable: false),
                    Shares = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    CostBasis = table.Column<decimal>(type: "numeric(28,10)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holdings", x => new { x.UserId, x.FranchiseId });
                    table.CheckConstraint("holdings_shares_nonneg", "\"Shares\" >= 0");
                    table.ForeignKey(
                        name: "FK_holdings_franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_holdings_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "market_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FranchiseId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Headline = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_market_events_franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "matches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExternalId = table.Column<string>(type: "text", nullable: true),
                    FranchiseA = table.Column<long>(type: "bigint", nullable: false),
                    FranchiseB = table.Column<long>(type: "bigint", nullable: false),
                    Map = table.Column<string>(type: "text", nullable: true),
                    RoundsA = table.Column<int>(type: "integer", nullable: true),
                    RoundsB = table.Column<int>(type: "integer", nullable: true),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    IsBo3 = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_matches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_matches_franchises_FranchiseA",
                        column: x => x.FranchiseA,
                        principalTable: "franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_matches_franchises_FranchiseB",
                        column: x => x.FranchiseB,
                        principalTable: "franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    FranchiseId = table.Column<long>(type: "bigint", nullable: false),
                    Side = table.Column<string>(type: "text", nullable: false),
                    QuoteId = table.Column<string>(type: "text", nullable: true),
                    CashIn = table.Column<decimal>(type: "numeric(28,10)", nullable: true),
                    SharesIn = table.Column<decimal>(type: "numeric(28,10)", nullable: true),
                    MaxSlippageBps = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RejectCode = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_orders_franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_orders_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pools",
                columns: table => new
                {
                    FranchiseId = table.Column<long>(type: "bigint", nullable: false),
                    CashReserve = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    ShareReserve = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    TotalSupply = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    IsHalted = table.Column<bool>(type: "boolean", nullable: false),
                    HaltReason = table.Column<string>(type: "text", nullable: true),
                    ResumesAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pools", x => x.FranchiseId);
                    table.CheckConstraint("pools_reserves_positive", "\"CashReserve\" > 0 AND \"ShareReserve\" > 0");
                    table.ForeignKey(
                        name: "FK_pools_franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "price_ticks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FranchiseId = table.Column<long>(type: "bigint", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    RefId = table.Column<long>(type: "bigint", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_ticks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_price_ticks_franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "roster_seats",
                columns: table => new
                {
                    FranchiseId = table.Column<long>(type: "bigint", nullable: false),
                    DiscordId = table.Column<string>(type: "text", nullable: false),
                    ExternalPlayerId = table.Column<long>(type: "bigint", nullable: false),
                    PlayerName = table.Column<string>(type: "text", nullable: false),
                    PlayerType = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roster_seats", x => new { x.FranchiseId, x.DiscordId });
                    table.ForeignKey(
                        name: "FK_roster_seats_franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "settlements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MatchId = table.Column<long>(type: "bigint", nullable: false),
                    FranchiseId = table.Column<long>(type: "bigint", nullable: false),
                    EloBefore = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    OppEloBefore = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    ExpectedMargin = table.Column<decimal>(type: "numeric(10,4)", nullable: false),
                    ActualMargin = table.Column<int>(type: "integer", nullable: false),
                    Surprise = table.Column<decimal>(type: "numeric(10,4)", nullable: false),
                    ShockRaw = table.Column<decimal>(type: "numeric(10,6)", nullable: false),
                    ShockApplied = table.Column<decimal>(type: "numeric(10,6)", nullable: false),
                    ShockClamped = table.Column<bool>(type: "boolean", nullable: false),
                    PriceBefore = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    PriceAfter = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    IsCorrection = table.Column<bool>(type: "boolean", nullable: false),
                    CorrectsId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_settlements_franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_settlements_matches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "matches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "trades",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    EntryId = table.Column<long>(type: "bigint", nullable: false),
                    FranchiseId = table.Column<long>(type: "bigint", nullable: false),
                    Side = table.Column<string>(type: "text", nullable: false),
                    Shares = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    Cash = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    Fee = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    PriceBefore = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    PriceAfter = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trades", x => x.Id);
                    table.ForeignKey(
                        name: "FK_trades_entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_trades_franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_trades_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_OwnerType_OwnerId_AssetType_AssetId",
                table: "accounts",
                columns: new[] { "OwnerType", "OwnerId", "AssetType", "AssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_elo_snapshots_FranchiseId_At",
                table: "elo_snapshots",
                columns: new[] { "FranchiseId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_franchises_ExternalTeamId",
                table: "franchises",
                column: "ExternalTeamId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_franchises_OrgId",
                table: "franchises",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_franchises_Ticker",
                table: "franchises",
                column: "Ticker",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_holdings_FranchiseId",
                table: "holdings",
                column: "FranchiseId");

            migrationBuilder.CreateIndex(
                name: "IX_market_events_FranchiseId_Id",
                table: "market_events",
                columns: new[] { "FranchiseId", "Id" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_matches_ExternalId",
                table: "matches",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_matches_FranchiseA",
                table: "matches",
                column: "FranchiseA");

            migrationBuilder.CreateIndex(
                name: "IX_matches_FranchiseB",
                table: "matches",
                column: "FranchiseB");

            migrationBuilder.CreateIndex(
                name: "IX_matches_ScheduledAt",
                table: "matches",
                column: "ScheduledAt");

            migrationBuilder.CreateIndex(
                name: "IX_matches_Status",
                table: "matches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_orders_FranchiseId",
                table: "orders",
                column: "FranchiseId");

            migrationBuilder.CreateIndex(
                name: "IX_orders_UserId_CreatedAt",
                table: "orders",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_orgs_ExternalId",
                table: "orgs",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_postings_AccountId_Id",
                table: "postings",
                columns: new[] { "AccountId", "Id" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_postings_EntryId",
                table: "postings",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_price_ticks_FranchiseId_At",
                table: "price_ticks",
                columns: new[] { "FranchiseId", "At" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_quotes_ExpiresAt",
                table: "quotes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_TokenHash",
                table: "refresh_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_UserId",
                table: "refresh_tokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_roster_seats_DiscordId",
                table: "roster_seats",
                column: "DiscordId");

            migrationBuilder.CreateIndex(
                name: "IX_settlements_FranchiseId",
                table: "settlements",
                column: "FranchiseId");

            migrationBuilder.CreateIndex(
                name: "IX_settlements_MatchId_FranchiseId_IsCorrection",
                table: "settlements",
                columns: new[] { "MatchId", "FranchiseId", "IsCorrection" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_trades_EntryId",
                table: "trades",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_trades_FranchiseId",
                table: "trades",
                column: "FranchiseId");

            migrationBuilder.CreateIndex(
                name: "IX_trades_OrderId",
                table: "trades",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_DiscordId",
                table: "users",
                column: "DiscordId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "balances");

            migrationBuilder.DropTable(
                name: "candles");

            migrationBuilder.DropTable(
                name: "elo_snapshots");

            migrationBuilder.DropTable(
                name: "holdings");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "market_events");

            migrationBuilder.DropTable(
                name: "pools");

            migrationBuilder.DropTable(
                name: "portfolio_snapshots");

            migrationBuilder.DropTable(
                name: "postings");

            migrationBuilder.DropTable(
                name: "price_ticks");

            migrationBuilder.DropTable(
                name: "quotes");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "roster_seats");

            migrationBuilder.DropTable(
                name: "settlements");

            migrationBuilder.DropTable(
                name: "trades");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "matches");

            migrationBuilder.DropTable(
                name: "entries");

            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "franchises");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "orgs");
        }
    }
}
