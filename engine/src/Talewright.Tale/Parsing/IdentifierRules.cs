using System.Text.RegularExpressions;

namespace Talewright.Tale.Parsing;

internal static partial class IdentifierRules
{
    public const string PatternText = @"^[A-Za-z_][A-Za-z0-9_]*(?:[.-][A-Za-z0-9_]+)*$";

    [GeneratedRegex(PatternText, RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string value) => Pattern().IsMatch(value);

    public static bool IsStart(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    public static bool IsPart(char value) => IsStart(value) || value is >= '0' and <= '9' or '.' or '-';
}
