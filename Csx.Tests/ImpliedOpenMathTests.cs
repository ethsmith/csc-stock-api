using Csx.Domain.Shock;
using FluentAssertions;

namespace Csx.Tests;

public class ImpliedOpenMathTests
{
    [Fact]
    public void New_line_with_no_matches_stays_at_initial_price()
    {
        var result = ImpliedOpenMath.Replay(
            ["ATL|Premier"],
            [],
            10m, 2m, 40m, 0.12m, 6m, 40m, ShockMode.Surprise, rescaleToInitial: false);

        result.Lines.Should().ContainSingle();
        result.Lines[0].Price.Should().Be(10m);
        result.Lines[0].Matches.Should().Be(0);
    }

    [Fact]
    public void Blowout_lifts_winner_and_drops_loser()
    {
        var at = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
        var result = ImpliedOpenMath.Replay(
            ["DOG|Premier", "FAV|Premier"],
            [new HistoricalMatch("DOG|Premier", "FAV|Premier", 13, 3, false, at)],
            10m, 2m, 40m, 0.12m, 6m, 40m, ShockMode.Surprise, rescaleToInitial: false);

        var dog = result.Lines.Single(l => l.Key == "DOG|Premier");
        var fav = result.Lines.Single(l => l.Key == "FAV|Premier");
        dog.Price.Should().BeGreaterThan(10m);
        fav.Price.Should().BeLessThan(10m);
        dog.Matches.Should().Be(1);
    }

    [Fact]
    public void Rescale_keeps_mean_near_initial_price()
    {
        var at = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
        var matches = new List<HistoricalMatch>
        {
            new("A|Premier", "B|Premier", 13, 5, false, at),
            new("A|Premier", "B|Premier", 13, 7, false, at.AddDays(1)),
            new("A|Premier", "B|Premier", 16, 14, false, at.AddDays(2))
        };
        var result = ImpliedOpenMath.Replay(
            ["A|Premier", "B|Premier"],
            matches,
            10m, 2m, 40m, 0.12m, 6m, 40m, ShockMode.Surprise, rescaleToInitial: true);

        result.MeanAfterRescale.Should().BeApproximately(10m, 0.15m);
        result.Lines.Should().OnlyContain(l => l.Price >= 2m && l.Price <= 40m);
        result.Lines.Select(l => l.Price).Should().Contain(p => p != 10m);
    }

    [Fact]
    public void Unmapped_opponent_still_moves_the_book_side()
    {
        var at = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
        var result = ImpliedOpenMath.Replay(
            ["ATL|Premier"],
            [new HistoricalMatch("ATL|Premier", "DEAD|Premier", 13, 8, false, at)],
            10m, 2m, 40m, 0.12m, 6m, 40m, ShockMode.Surprise, rescaleToInitial: false);

        result.Lines.Should().ContainSingle(l => l.Key == "ATL|Premier");
        result.Lines[0].Price.Should().NotBe(10m);
        result.Lines.Should().NotContain(l => l.Key == "DEAD|Premier");
    }
}
