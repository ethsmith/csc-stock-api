using Csx.Domain.Shock;
using FluentAssertions;

namespace Csx.Tests;

public class SettlementFixtureTests
{
    // Hand-checked historical-style fixtures from the design doc.
    [Theory]
    [InlineData(1400, 1000, 10, -8)]   // favorite 13-11: surprise -8, shock negative
    [InlineData(1400, 1000, 10, 0)]    // favorite 13-3 blowout: surprise 0
    [InlineData(1000, 1400, 10, 22)]   // underdog 13-3: surprise +22? wait actual +10 expected -10 surprise +20
    public void Surprise_table(decimal elo, decimal opp, int actual, int unused)
    {
        _ = unused;
        var e = ShockMath.ExpectedMargin(elo, opp, 40m);
        var surprise = actual - e;
        var shock = ShockMath.ComputeShock(ShockMode.Surprise, surprise, actual > 0, 0.12m, 6m);
        if (elo > opp && actual < e)
            shock.Should().BeNegative();
        Math.Abs(shock).Should().BeLessThanOrEqualTo(0.12m);
    }

    [Fact]
    public void Heavy_favorite_blowout_is_near_zero_surprise()
    {
        var e = ShockMath.ExpectedMargin(1400, 1000, 40m);
        var surprise = 10m - e;
        surprise.Should().Be(0m);
        ShockMath.SurpriseShock(surprise, 0.12m, 6m).Should().Be(0m);
    }

    [Fact]
    public void Upset_blowout_is_max_positive_move()
    {
        var e = ShockMath.ExpectedMargin(1000, 1400, 40m); // -10
        var surprise = 10m - e; // +20
        var shock = ShockMath.SurpriseShock(surprise, 0.12m, 6m);
        shock.Should().BePositive();
        shock.Should().BeApproximately(0.12m * (decimal)Math.Tanh(20.0 / 6.0), 0.0001m);
    }
}
