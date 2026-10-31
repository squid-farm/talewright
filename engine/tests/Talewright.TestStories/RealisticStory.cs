using System.Text;
using Bogus;
using Bogus.DataSets;
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
    public (int Min, int Max) WordsPerScene { get; init; } = (30, 150);

    public double OnceStorylet { get; init; } = 0.7;
    public double StoryletRequires { get; init; } = 0.25;
    public double StoryletFollowUp { get; init; } = 0.2;
    public double EntryCheck { get; init; } = 0.3;
    public double BranchSkillCheck { get; init; } = 0.5;
    public double DeathOnRetry { get; init; } = 0.3;
    public double EarlyExit { get; init; } = 0.1;
    public double ChoiceRequires { get; init; } = 0.2;
    public double ChoiceEffects { get; init; } = 0.2;
    public double PathDivertOnly { get; init; } = 0.5;
    public double PathCrossOver { get; init; } = 0.25;
    public double Substitution { get; init; } = 0.35;
    public double SpeakerLine { get; init; } = 0.4;
    public double Bold { get; init; } = 0.15;
    public double InlineIf { get; init; } = 0.1;
}

/// <summary>
/// A deterministic generated story with branches that merge at bottlenecks, hubs whose storylets loop back,
/// shared act rumors, skill check retry loops, deaths, a finale with several endings and cut (unreachable)
/// scenes. Unlike <see cref="PerformanceCorpus"/>, it measures realistic structure rather than size limits.
/// </summary>
public sealed class RealisticStory
{
    private static readonly string[] Requirements =
        ["player:dex >= 12", "player has rope", "var:gold >= 5", "flag:scouted", "not flag:alarm"];
    private static readonly string[] Effects =
        ["var:gold += 5", "set flag:scouted", "set flag:alarm", "give player rope", "var:trust += 1", "player:hp -= 1"];
    private static readonly string[] Substitutions =
        ["{player:first-name}", "{guide:first-name}", "{var:gold}", "{item:rope}"];
    private static readonly string[] Skills = ["athletics", "perception", "survival"];

    private readonly RealisticStoryProfile _profile;
    private readonly Randomizer _random;
    private readonly Lorem _lorem;
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private StringBuilder _text = new();

    private RealisticStory(RealisticStoryProfile profile)
    {
        if (profile.Chapters < 1 || profile.SegmentsPerChapter < 2 || profile.ChaptersPerAct < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "A realistic story needs chapters, acts and at least two segments.");
        }

        _profile = profile;
        _random = new Randomizer(profile.Seed);
        _lorem = new Lorem("en") { Random = _random };
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
        _files[StorySource.ManifestFileName] = $$"""
            game:
              id: realistic-story
              title: Realistic story
              version: 1.0.0
              start: c0.open
              seed: {{_profile.Seed}}
              ruleset:
                base: generic
            flags: [scouted, alarm]
            vars: {chapter: 0, gold: 0, trust: 0}
            items:
              rope: { name: coil of rope }
            characters:
              player:
                first-name: Tester
                stats: { dex: 12 }
              guide:
                first-name: Ada
                stats: { survival: 3 }
            """;

        for (var chapter = 0; chapter < _profile.Chapters; chapter++)
        {
            _text = new StringBuilder();
            Chapter(chapter);
            var act = chapter / _profile.ChaptersPerAct;
            if (chapter == _profile.Chapters - 1 || (chapter + 1) % _profile.ChaptersPerAct == 0)
            {
                Rumors(act);
            }

            _files[$"chapters/c{chapter:D3}.tale"] = _text.ToString();
        }

        _text = new StringBuilder();
        Finale();
        _files["chapters/finale.tale"] = _text.ToString();
    }

    private void Chapter(int chapter)
    {
        var id = $"c{chapter}";
        Scene($"{id}.open", $"@title {Label()}");
        _text.Append("~ var:chapter = ").Append(chapter).Append('\n');
        Prose();
        Divert($"{id}.s0.fork");

        var segments = _profile.SegmentsPerChapter;
        var after = chapter < _profile.Chapters - 1 ? $"c{chapter + 1}.open" : "finale";
        for (var segment = 0; segment < segments; segment++)
        {
            var next = segment == 0 ? $"{id}.town" : segment < segments - 1 ? $"{id}.s{segment + 1}.fork" : after;
            Segment($"{id}.s{segment}", next);
        }

        Hub(id, chapter / _profile.ChaptersPerAct, $"{id}.s1.fork");
        if ((chapter + 1) % _profile.CutSceneEveryChapters == 0)
        {
            Scene($"{id}.cut", $"@title {Label()}");
            Prose();
            Choice("Back", $"{id}.open", conditional: false);
            CutSceneCount++;
        }
    }

    private void Segment(string id, string next)
    {
        var lengths = Enumerable.Range(0, Number(_profile.PathsPerBranch)).Select(_ => Number(_profile.PathLength)).ToArray();
        var check = _random.Bool((float)_profile.BranchSkillCheck);
        Scene($"{id}.fork");
        Prose();
        for (var path = 0; path < lengths.Length; path++)
        {
            if (check && path == lengths.Length - 1)
            {
                _text.Append("* [").Append(Label()).Append("] skill-check ").Append(_random.ArrayElement(Skills))
                    .Append(" dc ").Append(_random.Number(10, 15)).Append('\n')
                    .Append("  success: -> ").Append(id).Append(".p").Append(path).Append(".0\n")
                    .Append("  failure: -> ").Append(id).Append(".retry\n");
            }
            else
            {
                Choice(Label(), $"{id}.p{path}.0", conditional: path > 0);
            }
        }

        if (check)
        {
            Scene($"{id}.retry", $"@title {Label()}");
            Prose();
            Choice("Try again", $"{id}.fork", conditional: false);
            if (_random.Bool((float)_profile.DeathOnRetry))
            {
                Choice(Label(), $"{id}.death", conditional: false);
                Scene($"{id}.death", $"@title {Label()}");
                Prose();
                Divert("END");
            }
        }

        for (var path = 0; path < lengths.Length; path++)
        {
            for (var step = 0; step < lengths[path]; step++)
            {
                var target = step == lengths[path] - 1 ? $"{id}.join" : $"{id}.p{path}.{step + 1}";
                Scene($"{id}.p{path}.{step}");
                Prose();
                if (step < lengths[path] - 1 && _random.Bool((float)_profile.EarlyExit))
                {
                    _text.Append("{if flag:alarm}\n-> ").Append(id).Append(".join\n{/if}\n");
                }

                if (_random.Bool((float)_profile.PathDivertOnly))
                {
                    Divert(target);
                    continue;
                }

                Choice(Label(), target, conditional: false);
                if (lengths.Length > 1 && _random.Bool((float)_profile.PathCrossOver))
                {
                    var other = (path + _random.Number(1, lengths.Length - 1)) % lengths.Length;
                    Choice(Label(), $"{id}.p{other}.{Math.Min(step + 1, lengths[other] - 1)}", conditional: true);
                }
            }
        }

        Scene($"{id}.join");
        if (_random.Bool(0.3f))
        {
            _text.Append("~ clear flag:alarm\n");
        }

        Prose();
        if (_random.Bool())
        {
            Divert(next);
        }
        else
        {
            Choice(Label(), next, conditional: false);
        }
    }

    private void Hub(string chapter, int act, string exit)
    {
        var hub = $"{chapter}.town";
        Scene(hub, $"@title {Label()}", $"@hub any({chapter}-town, a{act}-rumor)", $"@hub-limit {Number(_profile.HubLimit)}");
        Prose();
        Choice("Move on", exit, conditional: false);

        var storylets = Number(_profile.StoryletsPerHub);
        for (var index = 0; index < storylets; index++)
        {
            var id = $"{hub}.{index}";
            Storylet(id, $"{chapter}-town");
            if (_random.Bool((float)_profile.EntryCheck))
            {
                _text.Append("::: skill-check perception dc 12\nsuccess: { var:gold += 5 }\nfailure: { player:hp -= 1 }\n:::\n");
            }

            Prose();
            if (_random.Bool((float)_profile.StoryletFollowUp))
            {
                Choice(Label(), $"{id}.after", conditional: false);
                Scene($"{id}.after");
                Prose();
                Divert(hub);
                continue;
            }

            Choice("Back to camp", hub, conditional: false);
            if (_random.Bool(0.3f))
            {
                Choice(Label(), hub, conditional: true);
            }
        }
    }

    private void Rumors(int act)
    {
        var first = act * _profile.ChaptersPerAct;
        var last = Math.Min(_profile.Chapters, first + _profile.ChaptersPerAct) - 1;
        for (var index = 0; index < _profile.RumorsPerAct; index++)
        {
            Storylet($"a{act}.rumor.{index}", $"a{act}-rumor");
            Prose();
            // A rumor is offered by every hub in its act, so it returns to whichever hub the player came from.
            for (var chapter = first; chapter < last; chapter++)
            {
                _text.Append("{if var:chapter == ").Append(chapter).Append("}\n-> c").Append(chapter).Append(".town\n{/if}\n");
            }

            Divert($"c{last}.town");
        }
    }

    private void Finale()
    {
        Scene("finale", "@title Finale");
        Prose();
        string[] requirements = ["var:trust >= 3", "var:gold >= 20", "player has rope", "flag:scouted"];
        for (var index = 0; index < requirements.Length; index++)
        {
            _text.Append("* [").Append(Label()).Append("] (requires: ").Append(requirements[index])
                .Append(") -> ending.").Append(index).Append('\n');
        }

        Choice(Label(), $"ending.{requirements.Length}", conditional: false);
        for (var index = 0; index <= requirements.Length; index++)
        {
            Scene($"ending.{index}", $"@title {Label()}");
            Prose();
            Divert("END");
        }
    }

    private void Storylet(string id, string tag)
    {
        var metadata = new List<string> { $"@title {Label()}", $"@tags {tag}", $"@priority {_random.Number(0, 20)}" };
        if (_random.Bool((float)_profile.OnceStorylet))
        {
            metadata.Add("@once");
        }

        if (_random.Bool((float)_profile.StoryletRequires))
        {
            metadata.Add($"@requires {_random.ArrayElement(Requirements)}");
        }

        Scene(id, [.. metadata]);
    }

    private void Scene(string id, params string[] metadata)
    {
        if (_text.Length > 0)
        {
            _text.Append('\n');
        }

        _text.Append("=== ").Append(id).Append('\n');
        foreach (var line in metadata)
        {
            _text.Append(line).Append('\n');
        }

        _text.Append('\n');
        SceneCount++;
    }

    private void Prose()
    {
        var start = _text.Length;
        var words = Number(_profile.WordsPerScene);
        while (words > 0)
        {
            var sentences = _random.Number(2, 4);
            for (var sentence = 0; sentence < sentences && words > 0; sentence++)
            {
                var count = Math.Min(words, _random.Number(6, 14));
                words -= count;
                _text.Append(Sentence(count)).Append(' ');
            }

            if (_random.Bool((float)_profile.InlineIf))
            {
                _text.Append("{if flag:scouted}").Append(Sentence(6)).Append("{/if} ");
            }

            _text.Length--;
            _text.Append("\n\n");
        }

        if (_random.Bool((float)_profile.SpeakerLine))
        {
            _text.Append("> guide: ").Append(Sentence(_random.Number(5, 12))).Append("\n\n");
        }

        ProseBytes += _text.Length - start;
    }

    private string Sentence(int words)
    {
        var parts = _lorem.Words(words);
        if (_random.Bool((float)_profile.Substitution))
        {
            parts[_random.Number(0, parts.Length - 1)] = _random.ArrayElement(Substitutions);
        }

        if (_random.Bool((float)_profile.Bold))
        {
            var index = _random.Number(0, parts.Length - 1);
            parts[index] = $"**{parts[index]}**";
        }

        var text = string.Join(' ', parts);
        return char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }

    private void Choice(string label, string target, bool conditional)
    {
        _text.Append("* [").Append(label).Append(']');
        if (conditional && _random.Bool((float)_profile.ChoiceRequires))
        {
            _text.Append(" (requires: ").Append(_random.ArrayElement(Requirements)).Append(')');
        }

        if (_random.Bool((float)_profile.ChoiceEffects))
        {
            _text.Append(" { ").Append(_random.ArrayElement(Effects)).Append(" }");
        }

        _text.Append(" -> ").Append(target).Append('\n');
    }

    private void Divert(string target) => _text.Append("-> ").Append(target).Append('\n');

    private string Label()
    {
        var text = string.Join(' ', _lorem.Words(_random.Number(2, 4)));
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private int Number((int Min, int Max) range) => _random.Number(range.Min, range.Max);
}
