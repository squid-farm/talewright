using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console.Cli;
using Talewright.Authoring;
using Talewright.Talemark.Compilation;

namespace Talewright.Cli;

public sealed class NewStorySettings : CliSettings
{
    [CommandArgument(0, "[folder]")]
    public string Folder { get; set; } = ".";

    [CommandOption("--title <TITLE>")]
    public string? Title { get; set; }

    [CommandOption("--id <ID>")]
    public string? Id { get; set; }
}

public sealed class NewStoryCommand : Command<NewStorySettings>
{
    private const string SceneFileName = "story.tale";

    private static readonly Regex InvalidIdCharacters = new("[^a-z0-9]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public override int Execute(CommandContext context, NewStorySettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var writers = (CommandWriters)context.Data!;
        string root;
        try
        {
            root = Path.GetFullPath(settings.Folder);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return CommandLine.Failure("TWCLI002", $"Couldn't resolve story folder '{settings.Folder}': {ex.Message}",
                false, writers.Output, writers.Error);
        }

        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        var id = settings.Id ?? DeriveStoryId(folderName);
        if (!StoryLintRules.IsStoryIdStyle(id))
        {
            var message = settings.Id is null
                ? $"Couldn't derive a valid story ID from folder '{folderName}'. Supply one with --id."
                : $"Story ID '{id}' must use lowercase kebab-case (letters and numbers separated by single hyphens).";
            return CommandLine.Failure("TWCLI001", message, false, writers.Output, writers.Error);
        }

        var title = settings.Title ?? DeriveTitle(folderName.Length > 0 ? folderName : id);
        if (string.IsNullOrWhiteSpace(title))
        {
            return CommandLine.Failure("TWCLI001", "Story title cannot be empty.", false,
                writers.Output, writers.Error);
        }

        var manifestPath = Path.Combine(root, StorySource.ManifestFileName);
        var scenePath = Path.Combine(root, SceneFileName);
        var createdFiles = new List<string>(2);

        try
        {
            Directory.CreateDirectory(root);

            if (File.Exists(manifestPath) || Directory.Exists(manifestPath))
            {
                return CommandLine.Failure("TWCLI002",
                    $"Story folder '{root}' already contains '{StorySource.ManifestFileName}'; no files were changed.",
                    false, writers.Output, writers.Error);
            }

            if (Directory.EnumerateFiles(root, "*.tale", SearchOption.AllDirectories).Any())
            {
                return CommandLine.Failure("TWCLI002",
                    $"Story folder '{root}' already contains a .tale file; no files were changed.",
                    false, writers.Output, writers.Error);
            }

            if (File.Exists(scenePath) || Directory.Exists(scenePath))
            {
                return CommandLine.Failure("TWCLI002",
                    $"Story folder '{root}' already contains '{SceneFileName}'; no files were changed.",
                    false, writers.Output, writers.Error);
            }

            WriteNewFile(manifestPath, CreateManifest(id, title), createdFiles);
            WriteNewFile(scenePath, CreateScenes(), createdFiles);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            var cleanupFailure = RemoveCreatedFiles(createdFiles);
            var cleanupMessage = cleanupFailure is null
                ? ""
                : $" Cleanup also failed: {cleanupFailure}";
            return CommandLine.Failure("TWCLI002",
                $"Couldn't create story in '{root}': {ex.Message}.{cleanupMessage}",
                false, writers.Output, writers.Error);
        }

        writers.Output.WriteLine("Created story files:");
        writers.Output.WriteLine($"  {manifestPath}");
        writers.Output.WriteLine($"  {scenePath}");
        writers.Output.WriteLine();
        writers.Output.WriteLine("Next steps:");
        writers.Output.WriteLine($"  talewright run {QuotePath(root)}");
        writers.Output.WriteLine($"  Open {QuotePath(root)} in VS Code.");
        return 0;
    }

    private static string DeriveStoryId(string folderName) =>
        InvalidIdCharacters.Replace(folderName.ToLowerInvariant(), "-").Trim('-');

    private static string DeriveTitle(string folderName)
    {
        var words = folderName.Replace('-', ' ').Replace('_', ' ').Trim().ToLowerInvariant();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words);
    }

    private static string CreateManifest(string id, string title) => string.Join('\n',
        "game:",
        $"  id: {id}",
        $"  title: {QuoteYaml(title)}",
        "  version: 0.1.0",
        "  start: start",
        "",
        "characters:",
        "  player:",
        "    first-name: Player",
        "");

    private static string CreateScenes() => string.Join('\n',
        "=== start",
        "@title A New Beginning",
        "",
        "Your story begins here.",
        "",
        "* [Continue] -> ending",
        "",
        "=== ending",
        "@title The End",
        "",
        "Your story ends here.",
        "",
        "-> END",
        "");

    private static string QuoteYaml(string value)
    {
        var quoted = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    quoted.Append("\\\"");
                    break;
                case '\\':
                    quoted.Append("\\\\");
                    break;
                case '\0':
                    quoted.Append("\\0");
                    break;
                case '\b':
                    quoted.Append("\\b");
                    break;
                case '\t':
                    quoted.Append("\\t");
                    break;
                case '\n':
                    quoted.Append("\\n");
                    break;
                case '\v':
                    quoted.Append("\\v");
                    break;
                case '\f':
                    quoted.Append("\\f");
                    break;
                case '\r':
                    quoted.Append("\\r");
                    break;
                case '\u001b':
                    quoted.Append("\\e");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        quoted.Append("\\u")
                            .Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        quoted.Append(character);
                    }
                    break;
            }
        }

        return quoted.Append('"').ToString();
    }

    private static void WriteNewFile(string path, string content, ICollection<string> createdFiles)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        createdFiles.Add(path);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string? RemoveCreatedFiles(IReadOnlyList<string> createdFiles)
    {
        var failures = new List<string>();
        for (var index = createdFiles.Count - 1; index >= 0; index--)
        {
            try
            {
                File.Delete(createdFiles[index]);
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                failures.Add($"{createdFiles[index]}: {ex.Message}");
            }
        }

        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    private static string QuotePath(string path) => $"\"{path.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static bool IsFileSystemError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException;
}
