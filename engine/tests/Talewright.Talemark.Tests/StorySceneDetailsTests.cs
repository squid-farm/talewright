using Talewright.Narrative.Templates;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Formatting;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class StorySceneDetailsTests
{
    private const string DetailScene = """
        === start
        @title Detail Exhibit
        @tags exhibit, details
        @requires player has rope
        @once
        @priority 7
        @hub any(exhibit, details)
        @hub-limit 2

        > guide: First, {player:first-name}.
        {if flag:wary}
        ~ set flag:seen
        {else}
        ~ clear flag:seen
        {/if}
        {if flag:seen}
        ~ set flag:found
        {/if}
        > player: I understand.
        > guide: Then choose.

        {if flag:seen}
        ::: skill-check player athletics dc 12
        success: { set flag:passed }
        failure: { set flag:failed }
        :::
        {/if}

        * [Take {item:rope}] (requires: player has rope) { take player rope } -> END
        * [Climb] skill-check athletics dc 13
          success: { set flag:climbed } -> END
          failure: { set flag:slipped } -> END
        * -> END
        """;

    private static DirectoryInfo RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Talewright.slnx")))
            root = root.Parent;
        return root ?? throw new DirectoryNotFoundException("Could not find the Talewright repository root.");
    }

    private static StoryDefinition Compile(string tale)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = TestStories.DefaultYaml,
            ["main.tale"] = tale,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    private static StorySceneDetails Details(StoryDefinition story, string id) =>
        StoryMap.SceneDetails(story.Scenes.Single(scene => scene.Id.Value == id));

    [Fact]
    public void Details_include_metadata_conditions_effects_choices_checks_and_speakers()
    {
        var details = Details(Compile(DetailScene), "start");

        Assert.Equal("Detail Exhibit", details.Title);
        Assert.Equal(["details", "exhibit"], details.Tags);
        Assert.Equal("player has rope", details.Requires.Text);
        Assert.True(details.Once);
        Assert.Equal(7, details.Priority);
        Assert.Equal(["exhibit", "details"], details.Hub!.Tags);
        Assert.Equal(2, details.HubLimit);

        var blockEffects = details.Effects.Where(effect => effect.Outcome is null && effect.ChoiceKey is null).ToArray();
        Assert.Equal(["flag:wary", "not flag:wary", "flag:seen"], blockEffects.Select(effect => effect.Condition.Text));
        Assert.Equal(
            details.Effects.OrderBy(effect => effect.Order).Select(effect => effect.Order),
            details.Effects.Select(effect => effect.Order));

        var check = Assert.Single(details.EntryChecks);
        Assert.Equal("athletics", check.Check.Skill.Value);
        Assert.Equal("12", ExpressionFormatter.Format(check.Check.Difficulty));
        Assert.Equal("flag:seen", check.Condition.Text);
        Assert.Single(check.Success.Effects);
        Assert.Single(check.Failure.Effects);
        Assert.Contains(details.Effects, effect => effect.Outcome == SkillCheckOutcome.Success
            && effect.Condition.Text == "flag:seen");

        var labelled = Assert.Single(details.Choices, choice => choice.Label is not null && choice.SkillCheck is null);
        Assert.Equal("player has rope", labelled.Condition.Text);
        Assert.Single(labelled.Effects);
        Assert.Contains(labelled.Label!, inline => inline is SubstitutionTemplate);
        Assert.StartsWith("start/step:", Assert.Single(labelled.DivertIds));
        Assert.Contains(details.Effects, effect => effect.ChoiceKey == labelled.Key);

        var checkedChoice = Assert.Single(details.Choices, choice => choice.SkillCheck is not null);
        Assert.Equal("athletics", checkedChoice.SkillCheck!.Skill.Value);
        Assert.Equal("13", ExpressionFormatter.Format(checkedChoice.SkillCheck.Difficulty));
        Assert.Equal(2, checkedChoice.DivertIds.Count);
        Assert.Single(checkedChoice.Success!.Effects);
        Assert.Single(checkedChoice.Failure!.Effects);
        Assert.Contains(details.Effects, effect => effect.ChoiceKey == checkedChoice.Key
            && effect.Outcome == SkillCheckOutcome.Success);

        var textless = Assert.Single(details.Choices, choice => choice.Label is null);
        Assert.Empty(textless.Effects);
        Assert.Single(textless.DivertIds);
        Assert.Equal(["guide", "player"], details.Speakers.Select(speaker => speaker.Value));
        Assert.NotEmpty(details.Prose);
        Assert.IsType<SpeechTemplate>(details.Prose[0].Template);
        Assert.Empty(details.Prose[0].Condition.Parts);
    }

    [Fact]
    public void Details_walk_long_flat_conditions_without_recursion()
    {
        var source = $"=== start\n@requires {string.Join(" and ", Enumerable.Repeat("true", 10_000))}\n-> END";

        var details = Details(Compile(source), "start");

        Assert.Equal(10_000, details.Requires.Text.Split(" and ").Length);
    }

    [Fact]
    public void Details_for_the_most_nested_LostRelic_scene_match_the_golden_snapshot()
    {
        var source = StorySource.FromDirectory(Path.Combine(RepositoryRoot().FullName, "samples", "LostRelic"));
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var details = Details(result.Story!, "dream.spiral");
        var snapshotPath = Path.Combine(
            RepositoryRoot().FullName,
            "engine", "tests", "Talewright.Talemark.Tests", "Snapshots", "lost-relic-scene-details.txt");

        Assert.Equal(File.ReadAllText(snapshotPath), Snapshot(details));
    }

    [Fact]
    public void Details_reject_a_null_scene_object()
    {
        Assert.Throws<ArgumentNullException>(() => StoryMap.SceneDetails(null!));
    }

    private static string Snapshot(StorySceneDetails details)
    {
        var lines = new List<string>
        {
            $"scene|{details.Id}|{details.Title}|tags={string.Join(",", details.Tags)}|requires={details.Requires.Text}|once={details.Once.ToString().ToLowerInvariant()}|priority={details.Priority}|hub={details.Hub}|hub-limit={details.HubLimit}",
        };
        lines.AddRange(details.Prose.Select(prose =>
            $"prose|{prose.Order}|{prose.StepId}|condition={prose.Condition.Text}|{Template(prose.Template)}"));
        lines.AddRange(details.Effects.Select(effect =>
            $"effect|{effect.Order}|{effect.StepId}|condition={effect.Condition.Text}|outcome={effect.Outcome?.ToString() ?? "-"}|choice={effect.ChoiceKey ?? "-"}|{string.Join(";", effect.Effects)}"));
        lines.AddRange(details.Choices.Select(choice =>
            $"choice|{choice.Order}|{choice.StepId}|{choice.Key}|label={(choice.Label is null ? "<textless>" : Inlines(choice.Label))}|condition={choice.Condition.Text}|effects={string.Join(";", choice.Effects)}|diverts={string.Join(",", choice.DivertIds)}|check={Check(choice.SkillCheck)}"));
        lines.AddRange(details.Choices.Where(choice => choice.Success is not null || choice.Failure is not null)
            .SelectMany(choice => new[] { choice.Success, choice.Failure }.OfType<StorySceneCheckOutcome>()
                .Select(outcome => $"choice-outcome|{choice.Key}|{outcome.Outcome}|divert={outcome.DivertId ?? "-"}|{string.Join(";", outcome.Effects)}")));
        lines.AddRange(details.EntryChecks.Select(check =>
            $"entry-check|{check.Order}|{check.StepId}|{Check(check.Check)}|condition={check.Condition.Text}"));
        lines.AddRange(details.EntryChecks.SelectMany(check => new[] { check.Success, check.Failure }).Select(outcome =>
            $"entry-outcome|{outcome.Outcome}|divert={outcome.DivertId ?? "-"}|{string.Join(";", outcome.Effects)}"));
        lines.Add($"speakers|{string.Join(",", details.Speakers)}");
        return string.Join("\n", lines) + "\n";
    }

    private static string Check(Talewright.Rules.Checks.SkillCheck? check) => check is null
        ? "-"
        : $"{check.Actor}:{check.Skill} dc {ExpressionFormatter.Format(check.Difficulty)}";

    private static string Template(TemplateBlock block) => block switch
    {
        ParagraphTemplate paragraph => $"paragraph({Inlines(paragraph.Inlines)})",
        SpeechTemplate speech => $"speech({speech.Speaker}:{Inlines(speech.Inlines)})",
        ConditionalBlockTemplate conditional =>
            $"if({ExpressionFormatter.Format(conditional.Condition)};then=[{string.Join(",", conditional.Then.Select(Template))}];else=[{string.Join(",", conditional.Else.Select(Template))}])",
        _ => throw new NotSupportedException($"Unknown template block '{block.GetType().Name}'."),
    };

    private static string Inlines(IEnumerable<TemplateInline> inlines) =>
        string.Concat(inlines.Select(Inline));

    private static string Inline(TemplateInline inline) => inline switch
    {
        TextTemplate text => text.Text,
        StyledTemplate styled => $"{styled.Style}({Inlines(styled.Children)})",
        LineBreakTemplate => "\\n",
        MediaTemplate media => $"image({media.MediaId},{media.AltText})",
        SubstitutionTemplate substitution => Substitution(substitution),
        ConditionalInlineTemplate conditional =>
            $"if({ExpressionFormatter.Format(conditional.Condition)};then={Inlines(conditional.Then)};else={Inlines(conditional.Else)})",
        _ => throw new NotSupportedException($"Unknown template inline '{inline.GetType().Name}'."),
    };

    private static string Modifier(Talewright.Narrative.Templates.ITextModifier modifier) =>
        modifier is OneManyTextModifier oneMany
            ? $"{modifier.Name}({Quote(oneMany.Singular)},{Quote(oneMany.Plural)})"
            : modifier.Name;

    private static string Substitution(SubstitutionTemplate substitution)
    {
        var modifiers = string.Join(",", substitution.Modifiers.Select(Modifier));
        return $"{{{ExpressionFormatter.Format(substitution.Value)}{(modifiers.Length == 0 ? "" : $"#{modifiers}")}}}";
    }

    private static string Quote(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
