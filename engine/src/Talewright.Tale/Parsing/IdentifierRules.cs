using System.Text.RegularExpressions;

namespace Talewright.Tale.Parsing;

internal static partial class IdentifierRules
{
    /// <summary>Unanchored identifier grammar shared with the manifest schema and editor grammar.</summary>
    public const string PatternBody = @"[A-Za-z_][A-Za-z0-9_]*(?:[.-][A-Za-z0-9_]+)*";

    // .NET '$' also matches before a final '\n'; '\z' matches the schema's ECMAScript '$'.
    private const string PatternText = "^" + PatternBody + @"\z";

    [GeneratedRegex(PatternText, RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string value) => Pattern().IsMatch(value);

    public static bool IsStart(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    public static bool IsPart(char value) => IsStart(value) || value is >= '0' and <= '9' or '.' or '-';
}
