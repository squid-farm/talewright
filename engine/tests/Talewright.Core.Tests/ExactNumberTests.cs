using System.Numerics;
using Talewright.Core;

namespace Talewright.Core.Tests;

public class ExactNumberTests
{
    [Fact]
    public void Addition_is_exact_for_decimal_values()
    {
        var result = ExactNumber.Parse("0.1") + ExactNumber.Parse("0.2");

        Assert.Equal(ExactNumber.Parse("0.3"), result);
        Assert.Equal("3/10", result.ToCanonicalString());
    }

    [Fact]
    public void Division_and_multiplication_preserve_exact_values()
    {
        var result = ExactNumber.One / ExactNumber.FromInteger(3);

        Assert.Equal("1/3", result.ToCanonicalString());
        Assert.Equal(ExactNumber.One, result * 3);
    }

    [Fact]
    public void Negative_and_equivalent_fractions_normalize_canonically()
    {
        Assert.Equal("-1/4", ExactNumber.Parse("-0.25").ToCanonicalString());
        Assert.Equal("-1/2", ExactNumber.Parse("-.5").ToCanonicalString());
        Assert.Equal("-1/2", ExactNumber.FromFraction(1, -2).ToCanonicalString());
        Assert.Equal(ExactNumber.Parse("2/3"), ExactNumber.Parse("4/6"));
        Assert.Equal("3", ExactNumber.FromFraction(15, 5).ToCanonicalString());
    }

    [Fact]
    public void Decimal_presentation_terminates_when_possible_and_falls_back_to_fraction()
    {
        Assert.Equal("0.25", ExactNumber.Parse("1/4").ToDecimalString());
        Assert.Equal("3.5", ExactNumber.Parse("7/2").ToDecimalString());
        Assert.Equal("-0.125", ExactNumber.Parse("-1/8").ToDecimalString());
        Assert.Equal("1/3", ExactNumber.Parse("1/3").ToDecimalString());
    }

    [Theory]
    [InlineData("1.234", "1.23")]
    [InlineData("1.235", "1.24")]
    [InlineData("-1.235", "-1.24")]
    [InlineData("1/2", "0.5")]
    [InlineData("1/3", "0.33")]
    [InlineData("2/3", "0.67")]
    [InlineData("-1/3", "-0.33")]
    [InlineData("0.004", "0")]
    [InlineData("-0.004", "0")]
    [InlineData("0.995", "1")]
    [InlineData("1.004", "1")]
    [InlineData("2.500", "2.5")]
    public void Reader_display_rounds_at_most_two_places_away_from_zero(string text, string expected)
    {
        Assert.Equal(expected, ExactNumber.Parse(text).ToDisplayString());
    }

    [Theory]
    [InlineData("0.004", "0")]
    [InlineData("-0.004", "0")]
    [InlineData("0.25", "+0.25")]
    [InlineData("-0.25", "−0.25")]
    public void Signed_reader_display_uses_displayed_magnitude(string text, string expected)
    {
        Assert.Equal(expected, ExactNumber.Parse(text).ToSignedDisplayString());
    }

    [Fact]
    public void Exponent_notation_is_an_exact_manifest_number()
    {
        Assert.Equal(ExactNumber.FromInteger(100), ExactNumber.Parse("1e2"));
        Assert.Equal(ExactNumber.FromFraction(1, 80), ExactNumber.Parse("1.25e-2"));
    }

    [Fact]
    public void Addition_and_comparison_work_for_negative_values()
    {
        var negativeHalf = ExactNumber.Parse("-1/2");
        var negativeThird = ExactNumber.Parse("-1/3");

        Assert.True(negativeHalf < negativeThird);
        Assert.Equal(ExactNumber.Parse("-5/6"), negativeHalf + negativeThird);
        Assert.Equal(ExactNumber.Parse("-1/6"), negativeHalf - negativeThird);
    }

    [Fact]
    public void Arithmetic_matches_fraction_cross_products_for_small_values()
    {
        for (var leftNumerator = -3; leftNumerator <= 3; leftNumerator++)
        for (var leftDenominator = 1; leftDenominator <= 4; leftDenominator++)
        for (var rightNumerator = -3; rightNumerator <= 3; rightNumerator++)
        for (var rightDenominator = 1; rightDenominator <= 4; rightDenominator++)
        {
            var left = ExactNumber.FromFraction(leftNumerator, leftDenominator);
            var right = ExactNumber.FromFraction(rightNumerator, rightDenominator);

            Assert.Equal(
                ExactNumber.FromFraction(
                    leftNumerator * rightDenominator + rightNumerator * leftDenominator,
                    leftDenominator * rightDenominator),
                left + right);
            Assert.Equal(
                ExactNumber.FromFraction(
                    leftNumerator * rightDenominator - rightNumerator * leftDenominator,
                    leftDenominator * rightDenominator),
                left - right);
            Assert.Equal(
                ExactNumber.FromFraction(leftNumerator * rightNumerator, leftDenominator * rightDenominator),
                left * right);
            Assert.Equal(
                rightNumerator == 0
                    ? ExactNumber.Zero
                    : ExactNumber.FromFraction(leftNumerator * rightDenominator, leftDenominator * rightNumerator),
                left / right);
        }
    }

    [Fact]
    public void Default_value_is_canonical_zero()
    {
        ExactNumber value = default;

        Assert.Equal(ExactNumber.Zero, value);
        Assert.Equal(ExactNumber.Zero.GetHashCode(), value.GetHashCode());
        Assert.Equal("0", value.ToCanonicalString());
        Assert.Equal(ExactNumber.Zero, value * ExactNumber.Parse("3/7"));
    }

    [Fact]
    public void Division_by_zero_returns_zero_but_invalid_fraction_construction_throws()
    {
        Assert.Equal(ExactNumber.Zero, ExactNumber.Parse("12") / ExactNumber.Zero);
        Assert.Throws<DivideByZeroException>(() => ExactNumber.FromFraction(BigInteger.One, BigInteger.Zero));
    }

    [Theory]
    [InlineData("1/2", 1)]
    [InlineData("-1/2", -1)]
    [InlineData("2/3", 1)]
    [InlineData("-2/3", -1)]
    [InlineData("1/3", 0)]
    [InlineData("-1/3", 0)]
    [InlineData("2147483648", int.MaxValue)]
    [InlineData("-2147483649", int.MinValue)]
    public void Whole_number_conversion_rounds_away_from_zero_and_saturates(string text, int expected)
    {
        Assert.Equal(expected, ExactNumber.Parse(text).ToInt32RoundedAwayFromZero());
    }

    [Theory]
    [InlineData("2.5", "3", "3", "2")]
    [InlineData("-2.5", "-3", "-2", "-3")]
    [InlineData("2.499", "2", "3", "2")]
    [InlineData("-2.499", "-2", "-2", "-3")]
    [InlineData("0.1", "0", "1", "0")]
    [InlineData("-0.1", "0", "0", "-1")]
    [InlineData("1/3", "0", "1", "0")]
    [InlineData("-1/3", "0", "0", "-1")]
    [InlineData("2", "2", "2", "2")]
    [InlineData("-2", "-2", "-2", "-2")]
    public void Exact_integer_rounding_handles_signs_ties_and_fractions(
        string text, string nearest, string ceiling, string floor)
    {
        var value = ExactNumber.Parse(text);

        Assert.Equal(ExactNumber.Parse(nearest), value.RoundToNearestInteger());
        Assert.Equal(ExactNumber.Parse(ceiling), value.Ceiling());
        Assert.Equal(ExactNumber.Parse(floor), value.Floor());
    }

    [Fact]
    public void Exact_integer_rounding_preserves_arbitrary_precision()
    {
        var digits = new string('9', 500);
        var integer = BigInteger.Parse(digits);
        var positive = ExactNumber.Parse($"{digits}.6");
        var negative = ExactNumber.Parse($"-{digits}.6");

        Assert.Equal(ExactNumber.FromInteger(integer + 1), positive.RoundToNearestInteger());
        Assert.Equal(ExactNumber.FromInteger(integer + 1), positive.Ceiling());
        Assert.Equal(ExactNumber.FromInteger(integer), positive.Floor());
        Assert.Equal(ExactNumber.FromInteger(-integer - 1), negative.RoundToNearestInteger());
        Assert.Equal(ExactNumber.FromInteger(-integer), negative.Ceiling());
        Assert.Equal(ExactNumber.FromInteger(-integer - 1), negative.Floor());
    }

    [Fact]
    public void Arbitrary_precision_values_are_not_limited()
    {
        var digits = new string('9', 500);
        var huge = BigInteger.Parse(digits);
        var value = ExactNumber.FromInteger(huge);
        var fraction = ExactNumber.FromFraction(huge, huge + BigInteger.One);

        Assert.True(ExactNumber.TryParse(digits, out var parsed));
        Assert.Equal(value, parsed);
        Assert.False(ExactNumber.TryParse(digits + "x", out _));
        Assert.Equal(huge, value.Numerator);
        Assert.Equal(BigInteger.One, value.Denominator);
        Assert.Equal(ExactNumber.FromInteger(huge), fraction * ExactNumber.FromInteger(huge + BigInteger.One));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("1.")]
    [InlineData("1/0")]
    [InlineData("1//2")]
    [InlineData("1.2.3")]
    [InlineData("1 / 2")]
    public void Malformed_values_are_rejected(string text)
    {
        Assert.False(ExactNumber.TryParse(text, out _));
        Assert.Throws<FormatException>(() => ExactNumber.Parse(text));
    }
}
