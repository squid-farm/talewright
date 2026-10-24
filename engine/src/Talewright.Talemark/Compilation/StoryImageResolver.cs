using System.Text.RegularExpressions;
using System.Globalization;
using Humanizer;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Compilation;

/// <summary>A resolved image path relative to the story root, with its effective alt text.</summary>
public sealed record ResolvedStoryImage(string Path, string AltText);

/// <summary>Shared image lookup, independent of image syntax, the filesystem, and image rendering.</summary>
public static partial class StoryImageResolver
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];

    /// <summary>
    /// Resolves a path relative to the story's <c>images/</c> folder.
    /// Reports errors and warnings at the image reference, returning null on error.
    /// </summary>
    public static ResolvedStoryImage? Resolve(StorySource source, string path, string? altText,
        SourceLocation location, DiagnosticBag diagnostics)
    {
        if (!IsSafePath(path))
        {
            diagnostics.Error("TW2010",
                $"Unsafe image path '{path}'. Use a relative path inside images/ with / separators, no parent traversal, and no web address.",
                location);
            return null;
        }

        var extension = System.IO.Path.GetExtension(path);
        if (extension.Length > 0 && !Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Error("TW2011",
                $"Unsupported image format '{extension}' in '{path}'. Use .png, .jpg, .jpeg, or .webp.", location);
            return null;
        }

        var matches = extension.Length > 0
            ? new[] { "images/" + path }.Where(source.ImagePaths.Contains).ToArray()
            : Extensions.Select(ext => "images/" + path + ext).Where(source.ImagePaths.Contains).ToArray();
        if (matches.Length == 0)
        {
            diagnostics.Error("TW2012", $"Image '{path}' was not found in the story's images/ folder. Paths are case-sensitive.", location);
            return null;
        }

        if (matches.Length > 1)
        {
            diagnostics.Warning("TW2013",
                $"Image '{path}' matches multiple files: {string.Join(", ", matches)}. Using '{matches[0]}'; include an extension to choose explicitly.",
                location);
        }

        if (string.IsNullOrWhiteSpace(altText))
        {
            altText = FallbackAltText(matches[0]);
            diagnostics.Warning("TW2014", $"Image '{path}' has no alt text. Using '{altText}'; supply descriptive alt text.", location);
        }

        return new ResolvedStoryImage(matches[0], altText);
    }

    private static bool IsSafePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.StartsWith('/')
        && !path.Any(c => c is '\\' or ':' or '?' or '#' || char.IsControl(c))
        && path.Split('/').All(part => part.Length > 0 && part is not "." and not "..");

    private static string FallbackAltText(string path)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            // Humanize has no culture overload and uses the current culture when splitting camel case.
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var words = Separators().Replace(name.Humanize(), " ").Trim().ToLowerInvariant();
            return words.Length == 0 ? "Image" : char.ToUpperInvariant(words[0]) + words[1..];
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();
}
