namespace Talewright.Core.Identity;

/// <summary>
/// The one identifier rule: an ASCII letter or '_', then letters, digits and '_',
/// with single '.' or '-' separators between word groups.
/// </summary>
public static class IdentifierRules
{
    public static bool IsValid(string value)
    {
        if (value.Length == 0 || !IsStart(value[0]))
        {
            return false;
        }

        var afterSeparator = false;
        foreach (var c in value.AsSpan(1))
        {
            if (c is '.' or '-')
            {
                if (afterSeparator)
                {
                    return false;
                }

                afterSeparator = true;
            }
            else if (IsWordChar(c))
            {
                afterSeparator = false;
            }
            else
            {
                return false;
            }
        }

        return !afterSeparator;
    }

    public static bool IsStart(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    public static bool IsPart(char value) => IsWordChar(value) || value is '.' or '-';

    private static bool IsWordChar(char value) => IsStart(value) || value is >= '0' and <= '9';
}
