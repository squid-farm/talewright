using Talewright.Runtime;

namespace Talewright.Player.Components;

internal static class TranscriptPresentation
{
    internal const int PassageWindow = 21;

    internal static IReadOnlyList<PassageGroup> Group(IReadOnlyList<HistoryFrame> passages, int window)
    {
        var groups = new List<List<PresentedPassage>>();
        var start = Math.Max(0, passages.Count - window);
        for (var index = start; index < passages.Count; index++)
        {
            var passage = new PresentedPassage(index, passages[index]);
            if (passage.Entry.Frame.ContinuesPrevious && groups.Count > 0)
                groups[^1].Add(passage);
            else
                groups.Add([passage]);
        }

        return groups.Select(group => new PassageGroup(group[0].Index, group)).ToArray();
    }
}

internal sealed record PresentedPassage(int Index, HistoryFrame Entry);

internal sealed record PassageGroup(int StartIndex, IReadOnlyList<PresentedPassage> Passages);
