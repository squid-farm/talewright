using System.Globalization;
using System.Numerics;

namespace Talewright.Core;

/// <summary>
/// A reduced rational number backed by arbitrary-precision integers.
/// Version-1 gameplay arithmetic continues to use its existing int32 operations.
/// </summary>
public readonly struct ExactNumber : IEquatable<ExactNumber>, IComparable<ExactNumber>
{
    private readonly BigInteger _numerator;
    private readonly BigInteger _denominator;

    private ExactNumber(BigInteger numerator, BigInteger denominator)
        : this(numerator, denominator, alreadyReduced: false)
    {
    }

    private ExactNumber(BigInteger numerator, BigInteger denominator, bool alreadyReduced)
    {
        if (denominator.IsZero)
            throw new DivideByZeroException("An exact number cannot have a zero denominator.");

        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        if (numerator.IsZero)
        {
            _numerator = BigInteger.Zero;
            _denominator = BigInteger.One;
            return;
        }

        if (alreadyReduced)
        {
            _numerator = numerator;
            _denominator = denominator;
            return;
        }

        var divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        _numerator = numerator / divisor;
        _denominator = denominator / divisor;
    }

    public BigInteger Numerator => _numerator;

    public BigInteger Denominator => _denominator.IsZero ? BigInteger.One : _denominator;

    public static ExactNumber Zero => new(BigInteger.Zero, BigInteger.One);

    public static ExactNumber One => new(BigInteger.One, BigInteger.One);

    public static ExactNumber FromInteger(long value) => new(value, BigInteger.One);

    public static ExactNumber FromInteger(BigInteger value) => new(value, BigInteger.One);

    public static ExactNumber FromFraction(long numerator, long denominator) =>
        new(numerator, denominator);

    public static ExactNumber FromFraction(BigInteger numerator, BigInteger denominator) =>
        new(numerator, denominator);

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

            if (!BigInteger.TryParse(value[..slash], NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var numerator)
                || !BigInteger.TryParse(value[(slash + 1)..], NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var denominator)
                || denominator.IsZero)
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
        if (dot >= 0 && dot != value.LastIndexOf('.'))
            return false;

        var wholeText = dot < 0 ? value[index..] : value[index..dot];
        var fractionText = dot < 0 ? string.Empty : value[(dot + 1)..];
        if (wholeText.Length == 0 && fractionText.Length == 0)
            return false;
        if (wholeText.Length > 0 && !ContainsOnlyDigits(wholeText))
            return false;
        if (dot >= 0 && fractionText.Length == 0)
            return false;
        if (fractionText.Length > 0 && !ContainsOnlyDigits(fractionText))
            return false;

        var whole = wholeText.Length == 0
            ? BigInteger.Zero
            : BigInteger.Parse(wholeText, NumberStyles.None, CultureInfo.InvariantCulture);
        var fraction = fractionText.Length == 0
            ? BigInteger.Zero
            : BigInteger.Parse(fractionText, NumberStyles.None, CultureInfo.InvariantCulture);
        var scale = BigInteger.Pow(new BigInteger(10), fractionText.Length);
        var parsedNumerator = whole * scale + fraction;
        if (sign < 0)
            parsedNumerator = -parsedNumerator;

        number = new(parsedNumerator, scale);
        return true;
    }

    public static ExactNumber operator +(ExactNumber left, ExactNumber right) =>
        AddOrSubtract(left, right, subtract: false);

    public static ExactNumber operator -(ExactNumber left, ExactNumber right) =>
        AddOrSubtract(left, right, subtract: true);

    public static ExactNumber operator -(ExactNumber value) =>
        CreateReduced(-value.Numerator, value.Denominator);

    public static ExactNumber operator *(ExactNumber left, ExactNumber right)
    {
        var firstDivisor = BigInteger.GreatestCommonDivisor(
            BigInteger.Abs(left.Numerator), right.Denominator);
        var secondDivisor = BigInteger.GreatestCommonDivisor(
            BigInteger.Abs(right.Numerator), left.Denominator);
        return CreateReduced(
            (left.Numerator / firstDivisor) * (right.Numerator / secondDivisor),
            (left.Denominator / secondDivisor) * (right.Denominator / firstDivisor));
    }

    public static ExactNumber operator /(ExactNumber left, ExactNumber right)
    {
        if (right.Numerator.IsZero)
            return Zero;

        var numeratorDivisor = BigInteger.GreatestCommonDivisor(
            BigInteger.Abs(left.Numerator), BigInteger.Abs(right.Numerator));
        var denominatorDivisor = BigInteger.GreatestCommonDivisor(left.Denominator, right.Denominator);
        return CreateReduced(
            (left.Numerator / numeratorDivisor) * (right.Denominator / denominatorDivisor),
            (left.Denominator / denominatorDivisor) * (right.Numerator / numeratorDivisor));
    }

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
        if (!remainder.IsZero && BigInteger.Abs(remainder) * 2 >= Denominator)
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

    private static ExactNumber CreateReduced(BigInteger numerator, BigInteger denominator) =>
        new(numerator, denominator, alreadyReduced: true);

    private static ExactNumber AddOrSubtract(ExactNumber left, ExactNumber right, bool subtract)
    {
        var commonDivisor = BigInteger.GreatestCommonDivisor(left.Denominator, right.Denominator);
        var leftFactor = right.Denominator / commonDivisor;
        var rightFactor = left.Denominator / commonDivisor;
        var rightNumerator = right.Numerator * rightFactor;
        var numerator = left.Numerator * leftFactor + (subtract ? -rightNumerator : rightNumerator);

        // Reduced operands guarantee that any remaining common factor divides the shared denominator factor.
        var reduction = commonDivisor.IsOne
            ? BigInteger.One
            : BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), commonDivisor);
        return CreateReduced(numerator / reduction, left.Denominator * leftFactor / reduction);
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
