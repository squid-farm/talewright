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
        Before {image("photo.jpeg", "A camp")} after. {var:greeting}

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
            "game:\n  id: image-test\n  title: Folder images\n  start: start\n  undo: true\n");
        Directory.CreateDirectory(Path.Combine(Root, "characters"));
        File.WriteAllText(Path.Combine(Root, "characters", "player.talewright.yaml"),
            "characters:\n  player: {first-name: Tester, inventory: [camera]}\n");
        Directory.CreateDirectory(Path.Combine(Root, "world"));
        File.WriteAllText(Path.Combine(Root, "world", "equipment.talewright.yaml"),
            "items: {camera: {name: Camera}}\nvars: {greeting: \"Hello from supporting configuration.\"}\n");
        File.WriteAllText(TalePath, Tale);
        using var photo = Assembly.GetExecutingAssembly().GetManifestResourceStream("stories/LostRelic/images/camp-art.jpeg")
            ?? throw new InvalidOperationException("Missing real image test resource.");
        using (var target = File.Create(ImagePath)) photo.CopyTo(target);
        File.WriteAllBytes(Path.Combine(Root, "images", "pixel.png"), Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        File.WriteAllBytes(Path.Combine(Root, "images", "pixel.webp"), Convert.FromBase64String(
            "UklGRh4AAABXRUJQVlA4TBEAAAAvAAAAAAfQ//73v/+BiOh/AAA="));
        File.Copy(Path.Combine(Root, "images", "pixel.png"), Path.Combine(Root, "images", "unvisited.png"));
    }

    public byte[] Zip(string prefix = "")
    {
        var source = StorySource.FromDirectory(Root);
        using var buffer = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create,
            leaveOpen: true))
        {
            foreach (var path in source.Files.Keys.Concat(source.ImagePaths))
            {
                using var entry = archive.CreateEntry(prefix + path).Open();
                using var input = File.OpenRead(Path.Combine(Root, path));
                input.CopyTo(entry);
            }
        }
        return buffer.ToArray();
    }

    public void AddSourceAboveFormerFolderLimits()
    {
        var comment = "// " + new string('x', 5 * 1024 * 1024) + "\n";
        for (var index = 0; index < 10; index++)
            File.WriteAllText(Path.Combine(Root, $"large-{index}.tale"), comment);
        for (var index = 0; index < 1_000; index++)
            File.WriteAllText(Path.Combine(Root, $"empty-{index}.tale"), "");
    }

    public void EnlargeIllustrationAboveFormerArchiveLimit()
    {
        // JPEG decoders ignore bytes after the end marker; the ZIP still expands the entire member.
        using var image = File.OpenWrite(ImagePath);
        image.SetLength(51L * 1024 * 1024);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
