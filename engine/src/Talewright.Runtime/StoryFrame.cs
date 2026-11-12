using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Model;

namespace Talewright.Runtime;

/// <summary>A choice the player can make right now, with its label already rendered against current state.</summary>
/// <param name="Key">Opaque key to pass back to <see cref="IStorySession.Choose"/>.</param>
/// <param name="Label">Rendered label text.</param>
public sealed record ChoiceView(string Key, IReadOnlyList<RenderedInline> Label)
{
    internal ReadOnlyMemory<byte>? ReplayIdentity { get; init; }

    internal static IReadOnlyList<RenderedInline> ContinueLabel { get; } =
        Array.AsReadOnly<RenderedInline>([new RenderedText(ChoiceDefinition.ContinueLabelText, TextStyle.None)]);

    /// <summary>Plain-text label (for tests, logs, accessibility).</summary>
    public string Text => RenderedInline.ToPlainText(Label);
}

/// <summary>A numbered entry in the complete menu shown for the current frame.</summary>
/// <param name="Number">One-based position in the menu, including unavailable entries.</param>
/// <param name="Key">Opaque choice key; pass it to <see cref="IStorySession.Choose"/> only when available.</param>
/// <param name="Label">Rendered menu label.</param>
/// <param name="IsAvailable">Whether the player may select this entry now.</param>
public sealed record ChoiceMenuEntry(
    int Number,
    string Key,
    IReadOnlyList<RenderedInline> Label,
    bool IsAvailable)
{
    /// <summary>Plain-text label (for tests, logs, accessibility).</summary>
    public string Text => RenderedInline.ToPlainText(Label);
}

/// <summary>
/// One step of play, ready to present: what happened as a result of the last choice (notes), the scene's
/// rendered text, and what the player can do next. Presentation layers only ever consume frames, never the
/// story model, so a console, Blazor or game-engine UI can all sit on the same session.
/// </summary>
/// <param name="Scene">The scene now being shown, or null once the story has ended without a final scene.</param>
/// <param name="Title">The scene's <c>@title</c>, if any.</param>
/// <param name="Document">
/// Blocks to show, in order: notes for consequences of the previous choice (check results, stat/item/standing
/// changes), then the scene's prose, then notes for anything that happened on entering the scene.
/// </param>
/// <param name="Choices">Available choices only; unavailable menu entries are not selectable through this list.</param>
/// <param name="IsEnded">True when the story has reached <c>-&gt; END</c>.</param>
public sealed record StoryFrame(
    SceneId? Scene,
    string? Title,
    RenderedDocument Document,
    IReadOnlyList<ChoiceView> Choices,
    bool IsEnded)
{
    /// <summary>Whether this scene joins the preceding passage on screen.</summary>
    public bool ContinuesPrevious { get; init; }

    /// <summary>
    /// The complete visible menu, in display order. Numbers remain stable across available and unavailable entries.
    /// </summary>
    public IReadOnlyList<ChoiceMenuEntry> Menu { get; init; } =
        Array.AsReadOnly(Choices.Select((choice, index) =>
            new ChoiceMenuEntry(index + 1, choice.Key, choice.Label, IsAvailable: true)).ToArray());
}
