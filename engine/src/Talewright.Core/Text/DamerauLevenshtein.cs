namespace Talewright.Core.Text;

/// <summary>Calculates edit distance with insertions, deletions, substitutions and adjacent transpositions.</summary>
public static class DamerauLevenshtein
{
    public static int Distance(string left, string right)
    {
        var distances = new int[left.Length + 1, right.Length + 1];
        for (var index = 0; index <= left.Length; index++) distances[index, 0] = index;
        for (var index = 0; index <= right.Length; index++) distances[0, index] = index;

        for (var row = 1; row <= left.Length; row++)
        {
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = left[row - 1] == right[column - 1] ? 0 : 1;
                distances[row, column] = Math.Min(
                    Math.Min(distances[row - 1, column] + 1, distances[row, column - 1] + 1),
                    distances[row - 1, column - 1] + substitution);
                if (row > 1 && column > 1 && left[row - 1] == right[column - 2] &&
                    left[row - 2] == right[column - 1])
                    distances[row, column] = Math.Min(distances[row, column], distances[row - 2, column - 2] + 1);
            }
        }

        return distances[left.Length, right.Length];
    }
}
