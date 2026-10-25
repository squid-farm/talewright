using System.IO.Compression;
using System.Text;

namespace Talewright.Testing;

public static class TestArchives
{
    public static byte[] Create(IEnumerable<KeyValuePair<string, string>> files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    public static byte[] Invalid(string kind) => kind switch
    {
        "zip" => Encoding.UTF8.GetBytes("This is not a ZIP."),
        "manifest" => Create([new("main.tale", "=== start\n-> END")]),
        "yaml" => Create([new("story.talewright.yaml", "title: [broken"), new("main.tale", "=== start\n-> END")]),
        "tale" => Create([new("story.talewright.yaml", TestStories.DefaultYaml),
            new("chapters/main.tale", "=== start\n@hub missing-hub\n* [Missing destination] -> missing\n=== orphan\n-> END")]),
        "file-limit" => Create([new("huge.tale", new string('x', 5 * 1024 * 1024 + 1))]),
        "count-limit" => Create(Enumerable.Range(0, 1001).Select(i => new KeyValuePair<string, string>($"{i}.tale", ""))),
        "total-limit" => Create(Enumerable.Range(0, 10).Select(i => new KeyValuePair<string, string>($"{i}.tale",
            new string('x', 5 * 1024 * 1024))).Append(new("extra.tale", "x"))),
        "upload-limit" => new byte[20 * 1024 * 1024 + 1],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown invalid archive fixture."),
    };

    public static readonly IReadOnlyDictionary<string, string> Errors = new Dictionary<string, string>
    {
        ["zip"] = "Couldn't read",
        ["manifest"] = "TW4002",
        ["yaml"] = "TW4001",
        ["tale"] = "TW2002",
        ["file-limit"] = "5 MiB",
        ["count-limit"] = "1,000 story files",
        ["total-limit"] = "50 MiB",
        ["upload-limit"] = "20971520",
    };

    public static byte[] AtUploadLimit()
    {
        byte[] Build(int padding)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, content) in TestStories.Linear.Entries)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                    writer.Write(content);
                }

                using var ignored = zip.CreateEntry("padding.bin", CompressionLevel.NoCompression).Open();
                ignored.Write(new byte[padding]);
            }

            return buffer.ToArray();
        }

        return Build(20 * 1024 * 1024 - Build(0).Length);
    }
}
