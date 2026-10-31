using System.Text;
using Talewright.Talemark.Compilation;

namespace Talewright.Testing;

/// <summary>
/// Ranges and weights for a generated story shaped like a real one. Choice, hub and condition defaults follow
/// <c>samples/LostRelic</c>: mostly zero or one written choice per scene, small hubs and frequent conditions.
/// Scenes average about 90 words, longer than LostRelic's 37-word demo scenes.
/// </summary>
public sealed record RealisticStoryProfile
{
    /// <summary>Each chapter has about 50 scenes, so 20 chapters is about 1,000 scenes.</summary>
    public int Chapters { get; init; } = 20;
    public int Seed { get; init; } = 57057;
    public int SegmentsPerChapter { get; init; } = 3;
    public (int Min, int Max) PathsPerBranch { get; init; } = (2, 4);
    public (int Min, int Max) PathLength { get; init; } = (1, 4);
    public (int Min, int Max) StoryletsPerHub { get; init; } = (10, 15);
    public (int Min, int Max) HubLimit { get; init; } = (4, 6);
    public int ChaptersPerAct { get; init; } = 5;
    public int RumorsPerAct { get; init; } = 3;
    public int CutSceneEveryChapters { get; init; } = 10;

    public double OnceStorylet { get; init; } = 0.7;
    public double StoryletRequires { get; init; } = 0.25;
    public double StoryletFollowUp { get; init; } = 0.2;
    public double EntryCheck { get; init; } = 0.3;
    public double BranchSkillCheck { get; init; } = 0.5;
    public double DeathOnRetry { get; init; } = 0.3;
    public double EarlyExit { get; init; } = 0.1;
    public double PathDivertOnly { get; init; } = 0.5;
    public double PathCrossOver { get; init; } = 0.25;
}

/// <summary>
/// A deterministic generated story with branches that merge at bottlenecks, hubs whose storylets loop back,
/// shared act rumors, skill check retry loops, deaths, a finale with several endings and cut (unreachable)
/// scenes. Unlike <see cref="PerformanceCorpus"/>, it measures realistic structure rather than size limits.
/// This class only decides the structure; <see cref="TaleFaker"/> writes each scene. Every chapter, cut scene,
/// act and the finale has its own seed, so changing one of them leaves the other files unchanged.
/// </summary>
public sealed class RealisticStory
{
    private static readonly string[] Skills = ["athletics", "perception", "survival"];

    private readonly RealisticStoryProfile _profile;
    private readonly TaleCast _cast;
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    private RealisticStory(RealisticStoryProfile profile)
    {
        if (profile.Chapters < 1 || profile.SegmentsPerChapter < 2 || profile.ChaptersPerAct < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "A realistic story needs chapters, acts and at least two segments.");
        }

        _profile = profile;
        _cast = TaleCast.Create(profile.Seed);
    }

    public IReadOnlyDictionary<string, string> Files => _files;
    public int SceneCount { get; private set; }
    public int CutSceneCount { get; private set; }
    public long ProseBytes { get; private set; }

    public static RealisticStory Create(RealisticStoryProfile profile)
    {
        var story = new RealisticStory(profile);
        story.Generate();
        return story;
    }

    public StorySource ToSource() => new(_files);

    public void WriteDirectory(string root)
    {
        foreach (var (path, text) in _files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text, new UTF8Encoding(false));
        }
    }

    private void Generate()
    {
        _files[StorySource.ManifestFileName] = _cast.Manifest("realistic-story", "c0.open", _profile.Seed);
        for (var chapter = 0; chapter < _profile.Chapters; chapter++)
        {
            var text = new StringBuilder(Chapter(chapter));
            if ((chapter + 1) % _profile.CutSceneEveryChapters == 0)
            {
                var fake = Faker(part: 2, chapter);
                text.Append(fake.Scene($"c{chapter}.cut", fake.Choice($"c{chapter}.open")));
                Count(fake);
                CutSceneCount++;
            }

            var act = chapter / _profile.ChaptersPerAct;
            if (chapter == _profile.Chapters - 1 || (chapter + 1) % _profile.ChaptersPerAct == 0)
            {
                text.Append(Rumors(act));
            }

            _files[$"chapters/c{chapter:D3}.tale"] = text.ToString();
        }

        _files["chapters/finale.tale"] = Finale();
    }

    private TaleFaker Faker(int part, int index) =>
        new(unchecked((_profile.Seed * 31 + part) * 1_000_003 + index), _cast);

    private void Count(TaleFaker fake)
    {
        SceneCount += fake.SceneCount;
        ProseBytes += fake.ProseBytes;
    }

    private string Chapter(int chapter)
    {
        var fake = Faker(part: 1, chapter);
        var id = $"c{chapter}";
        var text = new StringBuilder(fake.Scene($"{id}.open", $"~ var:chapter = {chapter}", TaleFaker.Divert($"{id}.s0.fork")));
        var after = chapter < _profile.Chapters - 1 ? $"c{chapter + 1}.open" : "finale";
        for (var segment = 0; segment < _profile.SegmentsPerChapter; segment++)
        {
            var next = segment == 0 ? $"{id}.town"
                : segment < _profile.SegmentsPerChapter - 1 ? $"{id}.s{segment + 1}.fork"
                : after;
            text.Append(Segment(fake, $"{id}.s{segment}", next));
        }

        text.Append(Hub(fake, id, chapter / _profile.ChaptersPerAct));
        Count(fake);
        return text.ToString();
    }

    private string Segment(TaleFaker fake, string id, string next)
    {
        var text = new StringBuilder();
        var lengths = Enumerable.Range(0, fake.Between(_profile.PathsPerBranch)).Select(_ => fake.Between(_profile.PathLength)).ToArray();
        var check = fake.Chance(_profile.BranchSkillCheck);
        var fork = lengths.Select((_, path) => check && path == lengths.Length - 1
            ? $"* [{fake.Label()}] skill-check {fake.Pick(Skills)} dc {fake.Bogus.Random.Number(10, 15)}\n" +
              $"  success: -> {id}.p{path}.0\n  failure: -> {id}.retry"
            : fake.Choice($"{id}.p{path}.0", gated: path > 0));
        text.Append(fake.Scene($"{id}.fork", [.. fork]));

        if (check)
        {
            var death = fake.Chance(_profile.DeathOnRetry);
            text.Append(death
                ? fake.Scene($"{id}.retry", fake.Choice($"{id}.fork", "Try again"), fake.Choice($"{id}.death"))
                : fake.Scene($"{id}.retry", fake.Choice($"{id}.fork", "Try again")));
            if (death)
            {
                text.Append(fake.Scene($"{id}.death", TaleFaker.Divert("END")));
            }
        }

        for (var path = 0; path < lengths.Length; path++)
        {
            for (var step = 0; step < lengths[path]; step++)
            {
                var last = step == lengths[path] - 1;
                var lines = new List<string>();
                if (!last && fake.Chance(_profile.EarlyExit))
                {
                    lines.Add($"{{if flag:alarm}}\n-> {id}.join\n{{/if}}");
                }

                var target = last ? $"{id}.join" : $"{id}.p{path}.{step + 1}";
                if (fake.Chance(_profile.PathDivertOnly))
                {
                    lines.Add(TaleFaker.Divert(target));
                }
                else
                {
                    lines.Add(fake.Choice(target));
                    if (lengths.Length > 1 && fake.Chance(_profile.PathCrossOver))
                    {
                        var other = (path + fake.Bogus.Random.Number(1, lengths.Length - 1)) % lengths.Length;
                        lines.Add(fake.Choice($"{id}.p{other}.{Math.Min(step + 1, lengths[other] - 1)}", gated: true));
                    }
                }

                text.Append(fake.Scene($"{id}.p{path}.{step}", [.. lines]));
            }
        }

        var leave = fake.Chance(0.5) ? TaleFaker.Divert(next) : fake.Choice(next);
        text.Append(fake.Chance(0.3)
            ? fake.Scene($"{id}.join", "~ clear flag:alarm", leave)
            : fake.Scene($"{id}.join", leave));
        return text.ToString();
    }

    private string Hub(TaleFaker fake, string chapter, int act)
    {
        var hub = $"{chapter}.town";
        var text = new StringBuilder(fake.Scene(
            hub,
            $"@hub any({chapter}-town, a{act}-rumor)",
            $"@hub-limit {fake.Between(_profile.HubLimit)}",
            fake.Choice($"{chapter}.s1.fork", "Move on")));

        var storylets = fake.Between(_profile.StoryletsPerHub);
        for (var index = 0; index < storylets; index++)
        {
            var id = $"{hub}.{index}";
            var lines = Storylet(fake, $"{chapter}-town");
            if (fake.Chance(_profile.EntryCheck))
            {
                lines.Add("::: skill-check perception dc 12\nsuccess: { var:gold += 5 }\nfailure: { player:hp -= 1 }\n:::");
            }

            if (fake.Chance(_profile.StoryletFollowUp))
            {
                lines.Add(fake.Choice($"{id}.after"));
                text.Append(fake.Scene(id, [.. lines])).Append(fake.Scene($"{id}.after", TaleFaker.Divert(hub)));
                continue;
            }

            lines.Add(fake.Choice(hub, "Back to camp"));
            if (fake.Chance(0.3))
            {
                lines.Add(fake.Choice(hub, gated: true));
            }

            text.Append(fake.Scene(id, [.. lines]));
        }

        return text.ToString();
    }

    private string Rumors(int act)
    {
        var fake = Faker(part: 3, act);
        var first = act * _profile.ChaptersPerAct;
        var last = Math.Min(_profile.Chapters, first + _profile.ChaptersPerAct) - 1;
        var text = new StringBuilder();
        for (var index = 0; index < _profile.RumorsPerAct; index++)
        {
            var lines = Storylet(fake, $"a{act}-rumor");
            // A rumor is offered by every hub in its act, so it returns to whichever hub the player came from.
            lines.AddRange(Enumerable.Range(first, last - first).Select(chapter => $"{{if var:chapter == {chapter}}}\n-> c{chapter}.town\n{{/if}}"));
            lines.Add(TaleFaker.Divert($"c{last}.town"));
            text.Append(fake.Scene($"a{act}.rumor.{index}", [.. lines]));
        }

        Count(fake);
        return text.ToString();
    }

    private string Finale()
    {
        var fake = Faker(part: 4, 0);
        string?[] requirements = ["var:trust >= 3", "var:gold >= 20", $"player has {_cast.Items[0].Id}", "flag:scouted", null];
        var text = new StringBuilder(fake.Scene(
            "finale",
            ["@title Finale", .. requirements.Select((requires, index) => fake.Choice($"ending.{index}", requires: requires))]));
        for (var index = 0; index < requirements.Length; index++)
        {
            text.Append(fake.Scene($"ending.{index}", TaleFaker.Divert("END")));
        }

        Count(fake);
        return text.ToString();
    }

    private List<string> Storylet(TaleFaker fake, string tag)
    {
        List<string> metadata = [$"@tags {tag}", $"@priority {fake.Bogus.Random.Number(0, 20)}"];
        if (fake.Chance(_profile.OnceStorylet))
        {
            metadata.Add("@once");
        }

        if (fake.Chance(_profile.StoryletRequires))
        {
            metadata.Add($"@requires {fake.Requirement()}");
        }

        return metadata;
    }
}
