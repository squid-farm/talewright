namespace Talewright.Rules;

internal static class Arithmetic
{
    public static int Saturate(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
}
