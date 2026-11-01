using System.Reflection;
using Talewright.Talemark.Compilation;
using Talewright.Testing;

namespace Talewright.Player.E2E.Tests;

internal sealed class ImageStoryFiles : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "talewright-images-" + Guid.NewGuid().ToString("N"));
    public string TalePath => Path.Combine(Root, "main.tale");
    public string ImagePath => Path.Combine(Root, "images", "photo.jpeg");
    public static readonly string Tale = BuildTale();

    private static string BuildTale()
    {
        var photo = ImageFaker.Call("photo.jpeg", "A camp");
        var pixel = ImageFaker.Call("pixel.png", "A pixel");
        var repeated = ImageFaker.Call("photo.jpeg", "Repeated camp");
        return string.Join('\n',
            SceneFaker.Header("start"),
            $"Before {photo} after. {SubstitutionFaker.Variable(new TaleVariableRef("greeting"))}",
            "",
            SceneFaker.Speaker(new TaleCharacterRef("player"), $"Dialogue {pixel}."),
            "",
            $"{MarkupFaker.Strong(ImageFaker.Call("photo.jpeg", "Strong camp"))} {MarkupFaker.Emphasis(ImageFaker.Call("photo.jpeg", "Emphasized camp"))}",
            "",
            MarkupFaker.Code(ImageFaker.Call("pixel.webp", "Code pixel")),
            "",
            IfFaker.Inline("true", ImageFaker.Call("photo.jpeg", "Conditional camp")),
            "",
            repeated + repeated,
            "",
            ChoiceFaker.Create("next", $"Continue {ImageFaker.Call("photo.jpeg", "Choice camp")}"),
            SceneFaker.Header("next"),
            "Next passage.",
            ImageFaker.Call("photo.jpeg", "Next camp"),
            ChoiceFaker.Create("END", "Finish"));
    }

    public ImageStoryFiles()
    {
        Directory.CreateDirectory(Path.Combine(Root, "images"));
        var manifest = new ManifestFaker("image-test", "Folder images", "start") { Undo = true };
        File.WriteAllText(Path.Combine(Root, StorySource.ManifestFileName), manifest.Build(trailingNewline: true));
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
