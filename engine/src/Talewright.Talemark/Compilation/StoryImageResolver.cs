using System.Text.RegularExpressions;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Compilation;

/// <summary>A resolved image path relative to the story root, with its effective alt text.</summary>
public sealed record ResolvedStoryImage(string Path, string AltText);

/// <summary>Shared image lookup, independent of image syntax, the filesystem, and image rendering.</summary>
public static partial class StoryImageResolver
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];

    /// <summary>Safe, supported image paths relative to images/, in ordinal order. Reads no image bytes.</summary>
    public static IEnumerable<string> CompletionPaths(StorySource source) =>
        source.ImagePaths.Where(path => path.StartsWith("images/", StringComparison.Ordinal))
            .Select(path => path["images/".Length..])
            .Where(path => IsSafePath(path) && Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// Resolves a path relative to the story's <c>images/</c> folder.
    /// Reports errors and warnings at the image reference, returning null on error.
    /// </summary>
    public static ResolvedStoryImage? Resolve(StorySource source, string path, string? altText,
        SourceLocation location, DiagnosticBag diagnostics,
        MediaAccessibility accessibility = MediaAccessibility.Descriptive)
    {
        if (accessibility is not (MediaAccessibility.Descriptive or MediaAccessibility.Decorative))
            throw new ArgumentOutOfRangeException(nameof(accessibility));

        if (!IsSafePath(path))
        {
            diagnostics.Error(CompilerDiagnosticCodes.UnsafeImagePath,
                $"Unsafe image path '{path}'. Use a relative path inside images/ with / separators, no parent traversal, and no web address.",
                location);
            return null;
        }

        var extension = System.IO.Path.GetExtension(path);
        if (extension.Length > 0 && !Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Error(CompilerDiagnosticCodes.UnsupportedImageFormat,
                $"Unsupported image format '{extension}' in '{path}'. Use .png, .jpg, .jpeg, or .webp.", location);
            return null;
        }

        var matches = extension.Length > 0
            ? new[] { "images/" + path }.Where(source.ImagePaths.Contains).ToArray()
            : Extensions.Select(ext => "images/" + path + ext).Where(source.ImagePaths.Contains).ToArray();
        if (matches.Length == 0)
        {
            diagnostics.Error(CompilerDiagnosticCodes.MissingImage, $"Image '{path}' was not found in the story's images/ folder. Paths are case-sensitive.", location);
            return null;
        }

        if (matches.Length > 1)
        {
            diagnostics.Warning(CompilerDiagnosticCodes.AmbiguousImagePath,
                $"Image '{path}' matches multiple files: {string.Join(", ", matches)}. Using '{matches[0]}'; include an extension to choose explicitly.",
                location);
        }

        if (accessibility == MediaAccessibility.Decorative)
        {
            altText = "";
        }
        else if (string.IsNullOrWhiteSpace(altText))
        {
            altText = FallbackAltText(matches[0]);
            diagnostics.Warning(CompilerDiagnosticCodes.MissingImageAlt, $"Image '{path}' has no alt text. Using '{altText}'; supply descriptive alt text.", location);
        }

        return new ResolvedStoryImage(matches[0], altText);
    }

    internal static bool IsSafePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.StartsWith('/')
        && !path.Any(c => c is '\\' or ':' or '?' or '#' || char.IsControl(c))
        && path.Split('/').All(part => part.Length > 0 && part is not "." and not "..");

    private static string FallbackAltText(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var words = Separators().Replace(name, " ").Trim();
        return words.Length == 0 ? "Image" : words;
    }

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();
}
