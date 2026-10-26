using System.Reflection;
using Talewright.Talemark.Compilation;

namespace Talewright.Player.E2E.Tests;

internal sealed class ImageStoryFiles : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "talewright-images-" + Guid.NewGuid().ToString("N"));
    public string TalePath => Path.Combine(Root, "main.tale");
    public string ImagePath => Path.Combine(Root, "images", "photo.jpeg");
    public const string Tale = """
        === start
        Before {image("photo.jpeg", "A camp")} after.

        > player: Dialogue {image("pixel.png", "A pixel")}.

        **{image("photo.jpeg", "Strong camp")}** *{image("photo.jpeg", "Emphasized camp")}*

        `{image("pixel.webp", "Code pixel")}`

        {if true}{image("photo.jpeg", "Conditional camp")}{/if}

        {image("photo.jpeg", "Repeated camp")}{image("photo.jpeg", "Repeated camp")}

        * [Continue {image("photo.jpeg", "Choice camp")}] -> next
        === next
        Next passage.
        {image("photo.jpeg", "Next camp")}
        * [Finish] -> END
        """;

    public ImageStoryFiles()
    {
        Directory.CreateDirectory(Path.Combine(Root, "images"));
        File.WriteAllText(Path.Combine(Root, StorySource.ManifestFileName),
            "game:\n  id: image-test\n  title: Folder images\n  start: start\n  undo: true\ncharacters:\n  player: {first-name: Tester}\n");
        File.WriteAllText(TalePath, Tale);
        using var photo = Assembly.GetExecutingAssembly().GetManifestResourceStream("stories/LostRelic/images/camp-art.jpeg")
            ?? throw new InvalidOperationException("Missing real image test resource.");
        using (var target = File.Create(ImagePath)) photo.CopyTo(target);
        File.WriteAllBytes(Path.Combine(Root, "images", "pixel.png"), Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        File.WriteAllBytes(Path.Combine(Root, "images", "pixel.webp"), Convert.FromBase64String(
            "UklGRh4AAABXRUJQVlA4TBEAAAAvAAAAAAfQ//73v/+BiOh/AAA="));
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
