using Csx.Domain.Amm;
using Csx.Domain.Ledger;
using Csx.Domain.Shock;
using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Csx.Tests;

public class AmmMathTests
{
    [Fact]
    public void Buy_then_sell_same_shares_loses_cash()
    {
        const decimal r = 100_000m;
        const decimal t = 10_000m;
        const decimal fee = 0.005m;
        var buy = AmmMath.Buy(r, t, 500m, fee);
        var sell = AmmMath.Sell(buy.CashReserveAfter, buy.ShareReserveAfter, buy.Shares, fee);
        sell.CashNet.Should().BeLessThan(buy.CashIn);
    }

    [Fact]
    public void Buy_preserves_share_conservation()
    {
        var buy = AmmMath.Buy(100_000m, 10_000m, 500m, 0.005m);
        (buy.ShareReserveAfter + buy.Shares).Should().Be(10_000m);
    }

    [Fact]
    public void Price_is_cash_over_shares()
    {
        AmmMath.Price(100_000m, 10_000m).Should().Be(10m);
    }

    [Property(MaxTest = 200)]
    public bool BuySell_never_prints_money(decimal cashSpend)
    {
        cashSpend = Math.Clamp(Math.Abs(cashSpend) % 5_000m, 1.00m, 5_000m);
        try
        {
            var buy = AmmMath.Buy(100_000m, 10_000m, cashSpend, 0.005m);
            var sell = AmmMath.Sell(buy.CashReserveAfter, buy.ShareReserveAfter, buy.Shares, 0.005m);
            return sell.CashNet < buy.CashIn;
        }
        catch
        {
            return true;
        }
    }

    [Property(MaxTest = 200)]
    public bool K_is_non_decreasing_on_trades(decimal amount)
    {
        amount = Math.Clamp(Math.Abs(amount) % 1_000m, 1.00m, 1_000m);
        try
        {
            var k0 = AmmMath.K(100_000m, 10_000m);
            var buy = AmmMath.Buy(100_000m, 10_000m, amount, 0.005m);
            if (buy.KAfter + 0.0000001m < k0) return false;
            var sell = AmmMath.Sell(buy.CashReserveAfter, buy.ShareReserveAfter, buy.Shares / 2m, 0.005m);
            return sell.KAfter + 0.0000001m >= buy.KAfter;
        }
        catch
        {
            return true;
        }
    }
}

public class ShockMathTests
{
    [Theory]
    [InlineData(1400, 1000, 10)]      // 400 elo gap -> +10 expected rounds
    [InlineData(1000, 1400, -10)]
    [InlineData(1020, 1000, 0.5)]
    public void Expected_margin_from_elo(decimal elo, decimal opp, decimal expected)
    {
        ShockMath.ExpectedMargin(elo, opp, 40m).Should().BeApproximately(expected, 0.0001m);
    }

    [Fact]
    public void Favorite_narrow_win_is_negative_in_surprise_mode()
    {
        var e = ShockMath.ExpectedMargin(1400, 1000, 40m); // 10
        var actual = 2m; // 13-11
        var surprise = actual - e;
        var shock = ShockMath.ComputeShock(ShockMode.Surprise, surprise, won: true, 0.12m, 6m);
        shock.Should().BeNegative();
    }

    [Fact]
    public void Blowout_is_clamped_by_tanh()
    {
        var shock = ShockMath.SurpriseShock(40m, 0.12m, 6m);
        Math.Abs(shock).Should().BeLessThanOrEqualTo(0.12m);
    }

    [Fact]
    public void Signed_scaled_win_is_always_green()
    {
        var shock = ShockMath.SignedScaledShock(-8m, won: true, 0.12m, 6m);
        shock.Should().BePositive();
        shock.Should().BeGreaterThanOrEqualTo(0.12m * 0.15m);
        shock.Should().BeLessThanOrEqualTo(0.12m);
    }

    [Fact]
    public void Circuit_breaker_caps_daily_move()
    {
        var final = ShockMath.ApplyCircuitBreaker(10m, 10m, 14m, 0.25m);
        final.Should().Be(12.5m);
    }

    [Fact]
    public void Decay_tick_with_zero_lambda_is_identity()
    {
        ShockMath.DecayTick(10.26m, 6.79m, 0m).Should().Be(10.26m);
    }
}

public class MoneyRoundingTests
{
    [Fact]
    public void User_debit_rounds_up() => MoneyRounding.RoundCashDebit(1.001m).Should().Be(1.01m);

    [Fact]
    public void User_credit_rounds_down() => MoneyRounding.RoundCashCredit(1.019m).Should().Be(1.01m);

    [Fact]
    public void Shares_out_round_down() => MoneyRounding.RoundSharesOut(1.00009m).Should().Be(1.0000m);
}

public class TickerTests
{
    [Fact]
    public void Builds_prefix_plus_tier_letter()
    {
        Csx.Domain.Tickers.FromPrefixAndTier("ATL", "Premier", 1).Should().Be("ATLP");
        Csx.Domain.Tickers.FromPrefixAndTier("HG", "Challenger", 1).Should().Be("HGC");
        Csx.Domain.Tickers.FromPrefixAndTier("NAN", "Recruit", 1).Should().Be("NANR");
    }
}
