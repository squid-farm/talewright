using System.Globalization;
using System.Numerics;

namespace Talewright.Core;

/// <summary>
/// A reduced rational number backed by arbitrary-precision integers.
/// It is used for exact gameplay arithmetic; callers that require version-1 int32 semantics remain responsible
/// for saturation and truncating division at each operation.
/// </summary>
public readonly struct ExactNumber : IEquatable<ExactNumber>, IComparable<ExactNumber>
{
    private ExactNumber(BigInteger numerator, BigInteger denominator)
    {
        if (denominator < BigInteger.Zero)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        if (denominator == BigInteger.Zero)
            throw new DivideByZeroException();

        if (numerator == BigInteger.Zero)
        {
            Numerator = BigInteger.Zero;
            Denominator = BigInteger.One;
            return;
        }

        var divisor = BigInteger.GreatestCommonDivisor(
            BigInteger.Abs(numerator),
            denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    public BigInteger Numerator { get; }

    public BigInteger Denominator { get; }

    public static ExactNumber Zero => FromInteger(BigInteger.Zero);

    public static ExactNumber One => FromInteger(BigInteger.One);

    public static ExactNumber FromInteger(long value) => new(value, 1);

    public static ExactNumber FromInteger(BigInteger value) => new(value, BigInteger.One);

    public static ExactNumber FromFraction(long numerator, long denominator) =>
        denominator == 0 ? Zero : new(numerator, denominator);

    public static ExactNumber FromFraction(BigInteger numerator, BigInteger denominator) =>
        denominator == BigInteger.Zero ? Zero : new(numerator, denominator);

    public static ExactNumber Parse(string value)
    {
        if (!TryParse(value, out var number))
            throw new FormatException($"'{value}' is not a valid exact number.");
        return number;
    }

    public static bool TryParse(string? value, out ExactNumber number)
    {
        number = Zero;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();
        var slash = value.IndexOf('/');
        if (slash >= 0)
        {
            if (slash == 0 || slash != value.LastIndexOf('/') || slash == value.Length - 1)
                return false;

            if (!BigInteger.TryParse(value[..slash], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                    out var numerator)
                || !BigInteger.TryParse(value[(slash + 1)..], NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var denominator)
                || denominator == BigInteger.Zero)
                return false;

            number = new(numerator, denominator);
            return true;
        }

        var sign = 1;
        var index = 0;
        if (value[index] is '+' or '-')
        {
            if (value[index] == '-')
                sign = -1;
            index++;
        }

        if (index == value.Length)
            return false;

        var dot = value.IndexOf('.');
        var wholeText = dot < 0 ? value[index..] : value[index..dot];
        var fractionText = dot < 0 ? string.Empty : value[(dot + 1)..];

        if (wholeText.Length == 0 && fractionText.Length == 0)
            return false;
        if (wholeText.Length > 0 && !ContainsOnlyDigits(wholeText))
            return false;
        if (fractionText.Length == 0)
        {
            if (dot >= 0)
                return false;
        }
        else if (!ContainsOnlyDigits(fractionText))
        {
            return false;
        }

        var whole = wholeText.Length == 0
            ? BigInteger.Zero
            : BigInteger.Parse(wholeText, NumberStyles.None, CultureInfo.InvariantCulture);
        var fraction = fractionText.Length == 0
            ? BigInteger.Zero
            : BigInteger.Parse(fractionText, NumberStyles.None, CultureInfo.InvariantCulture);
        var scale = BigInteger.Pow(new BigInteger(10), fractionText.Length);
        number = new(sign * (whole * scale + fraction), scale);
        return true;
    }

    public static ExactNumber operator +(ExactNumber left, ExactNumber right) =>
        new(left.Numerator * right.Denominator + right.Numerator * left.Denominator,
            left.Denominator * right.Denominator);

    public static ExactNumber operator -(ExactNumber left, ExactNumber right) =>
        new(left.Numerator * right.Denominator - right.Numerator * left.Denominator,
            left.Denominator * right.Denominator);

    public static ExactNumber operator -(ExactNumber value) =>
        new(-value.Numerator, value.Denominator);

    public static ExactNumber operator *(ExactNumber left, ExactNumber right) =>
        new(left.Numerator * right.Numerator, left.Denominator * right.Denominator);

    public static ExactNumber operator /(ExactNumber left, ExactNumber right) =>
        right.Numerator == BigInteger.Zero
            ? Zero
            : new(left.Numerator * right.Denominator, left.Denominator * right.Numerator);

    public static bool operator ==(ExactNumber left, ExactNumber right) =>
        left.Numerator == right.Numerator && left.Denominator == right.Denominator;

    public static bool operator !=(ExactNumber left, ExactNumber right) => !(left == right);

    public static bool operator <(ExactNumber left, ExactNumber right) => left.CompareTo(right) < 0;

    public static bool operator <=(ExactNumber left, ExactNumber right) => left.CompareTo(right) <= 0;

    public static bool operator >(ExactNumber left, ExactNumber right) => left.CompareTo(right) > 0;

    public static bool operator >=(ExactNumber left, ExactNumber right) => left.CompareTo(right) >= 0;

    public static implicit operator ExactNumber(int value) => FromInteger(value);

    public static implicit operator ExactNumber(long value) => FromInteger(value);

    public int CompareTo(ExactNumber other) =>
        (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);

    public bool Equals(ExactNumber other) => this == other;

    public override bool Equals(object? obj) => obj is ExactNumber other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);

    public string ToCanonicalString() =>
        Denominator == BigInteger.One
            ? Numerator.ToString(CultureInfo.InvariantCulture)
            : $"{Numerator.ToString(CultureInfo.InvariantCulture)}/{Denominator.ToString(CultureInfo.InvariantCulture)}";

    public override string ToString() => ToCanonicalString();

    public int ToInt32RoundedAwayFromZero()
    {
        var quotient = BigInteger.DivRem(Numerator, Denominator, out var remainder);
        if (remainder == BigInteger.Zero)
            return BigIntegerToInt32Saturating(quotient);

        if (BigInteger.Abs(remainder) * 2 >= Denominator)
            quotient += Numerator.Sign;

        return BigIntegerToInt32Saturating(quotient);
    }

    private static int BigIntegerToInt32Saturating(BigInteger value)
    {
        if (value > int.MaxValue)
            return int.MaxValue;
        if (value < int.MinValue)
            return int.MinValue;
        return (int)value;
    }

    private static bool ContainsOnlyDigits(string value)
    {
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
                return false;
        }
        return value.Length > 0;
    }
}
