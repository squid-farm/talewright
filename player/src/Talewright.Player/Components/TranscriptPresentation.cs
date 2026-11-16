using Talewright.Narrative.Rendering;
using Talewright.Runtime;

namespace Talewright.Player.Components;

internal static class TranscriptPresentation
{
    internal const int PassageWindow = 21;

    internal static IReadOnlyList<PassageGroup> Group(
        IReadOnlyList<HistoryFrame> passages, int window, int? includeFromIndex = null)
    {
        var groups = new List<List<PresentedPassage>>();
        var start = Math.Max(0, passages.Count - window);
        if (includeFromIndex is { } include && include < start)
            start = Math.Max(0, include);
        for (var index = start; index < passages.Count; index++)
        {
            var passage = new PresentedPassage(index, passages[index]);
            if (passage.Entry.Frame.ClearsPrevious)
                groups.Clear();

            if (passage.Entry.Frame.ContinuesPrevious && groups.Count > 0)
                groups[^1].Add(passage);
            else
                groups.Add([passage]);
        }

        var result = new PassageGroup[groups.Count];
        var laterImages = new HashSet<string>(StringComparer.Ordinal);
        for (var index = groups.Count - 1; index >= 0; index--)
        {
            var group = groups[index];
            result[index] = new PassageGroup(group[0].Index, group,
                new HashSet<string>(laterImages, StringComparer.Ordinal));
            foreach (var passage in group)
            {
                var inlines = passage.Entry.Frame.Document.Blocks.SelectMany(block => block switch
                {
                    RenderedParagraph paragraph => paragraph.Inlines,
                    RenderedSpeech speech => speech.Paragraphs.SelectMany(paragraph => paragraph),
                    _ => Enumerable.Empty<RenderedInline>(),
                }).Concat(passage.Entry.Chosen ?? []);
                foreach (var media in inlines.OfType<RenderedMedia>())
                    laterImages.Add(media.MediaId);
            }
        }
        return result;
    }
}

internal sealed record PresentedPassage(int Index, HistoryFrame Entry);

internal sealed record PassageGroup(
    int StartIndex,
    IReadOnlyList<PresentedPassage> Passages,
    IReadOnlySet<string> SupersededImages);
