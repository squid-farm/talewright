using System.Text.Json;
using Talewright.Testing;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: Talewright.TestStories <output-directory>");
    return 2;
}

var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
foreach (var fixture in TestStories.All)
{
    fixture.Compile();
    fixture.WriteDirectory(Path.Combine(output, fixture.Id));
    File.WriteAllBytes(Path.Combine(output, fixture.Id + ".zip"), fixture.Zip());
    File.WriteAllBytes(Path.Combine(output, fixture.Id + "-wrapped.zip"), fixture.Zip("wrapped story/"));
}

File.WriteAllText(Path.Combine(output, "scenarios.json"),
    JsonSerializer.Serialize(TestStories.Scenarios, new JsonSerializerOptions { WriteIndented = true }));
foreach (var kind in TestArchives.Errors.Keys)
{
    File.WriteAllBytes(Path.Combine(output, $"invalid-{kind}.zip"), TestArchives.Invalid(kind));
}

File.WriteAllText(Path.Combine(output, "errors.json"), JsonSerializer.Serialize(TestArchives.Errors));
File.WriteAllBytes(Path.Combine(output, "at-upload-limit.zip"), TestArchives.AtUploadLimit());
File.WriteAllBytes(Path.Combine(output, "histoire café.zip"), TestStories.Linear.Zip());
Console.WriteLine($"Exported {TestStories.All.Count} fixtures and {TestStories.Scenarios.Count} scenarios to {output}.");
return 0;
