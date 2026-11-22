using System.Numerics;
using Talewright.Core;

namespace Talewright.Core.Tests;

public class ExactNumberTests
{
    [Fact]
    public void Addition_is_exact_for_decimal_literals()
    {
        var tenth = ExactNumber.Parse("0.1");
        var twoTenths = ExactNumber.Parse("0.2");

        Assert.Equal("3/10", (tenth + twoTenths).ToCanonicalString());
    }

    [Fact]
    public void Division_produces_an_exact_reduced_fraction()
    {
        var result = ExactNumber.Parse("1") / ExactNumber.Parse("3");

        Assert.Equal("1/3", result.ToCanonicalString());
        Assert.Equal(ExactNumber.One, result * 3);
    }

    [Fact]
    public void Negative_and_equivalent_fractions_normalize_canonically()
    {
        Assert.Equal("-1/4", ExactNumber.Parse("-0.25").ToCanonicalString());
        Assert.Equal("2/3", ExactNumber.Parse("2/3").ToCanonicalString());
        Assert.Equal(ExactNumber.Parse("2/3"), ExactNumber.Parse("4/6"));
        Assert.Equal(ExactNumber.Parse("-.5"), ExactNumber.Parse("-1/2"));
    }

    [Fact]
    public void Division_by_zero_returns_zero()
    {
        Assert.Equal(ExactNumber.Zero, ExactNumber.Parse("12") / ExactNumber.Zero);
        Assert.Equal(ExactNumber.Zero, ExactNumber.FromFraction(BigInteger.One, BigInteger.Zero));
    }

    [Theory]
    [InlineData("1/2", 1)]
    [InlineData("-1/2", -1)]
    [InlineData("2/3", 1)]
    [InlineData("-2/3", -1)]
    [InlineData("1/3", 0)]
    [InlineData("-1/3", 0)]
    public void Whole_boundary_rounding_uses_halves_away_from_zero(string text, int expected)
    {
        Assert.Equal(expected, ExactNumber.Parse(text).ToInt32RoundedAwayFromZero());
    }

    [Fact]
    public void Arbitrary_precision_values_are_not_limited()
    {
        var huge = BigInteger.Parse(new string('9', 500));
        var number = ExactNumber.FromInteger(huge);

        Assert.Equal(huge, number.Numerator);
        Assert.Equal(BigInteger.One, number.Denominator);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("1.")]
    [InlineData("1/0")]
    [InlineData("1//2")]
    [InlineData("1.2.3")]
    public void Malformed_values_are_rejected(string text)
    {
        Assert.False(ExactNumber.TryParse(text, out _));
    }
}
