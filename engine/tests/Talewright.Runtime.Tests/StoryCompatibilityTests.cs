using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Rules.Expressions;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public sealed class StoryCompatibilityTests
{
    [Fact]
    public void Compatible_update_restores_character_events_cursor_and_complete_redo_without_rewriting()
    {
        var original = Create(Compile());
        original.Choose("c0");
        original.Choose("c0");
        original.Back();
        var bytes = original.Save();
        var unchanged = bytes.ToArray();
        var sourceProgress = StoryHistory.DescribeSave("Original", bytes).ProgressKey;
        var updated = Compile(version: "2", prose: "Updated narrative.");

        var restored = StoryHistory.Restore(updated, bytes);

        Assert.Equal(unchanged, bytes);
        Assert.Equal(original.Seed, restored.Seed);
        Assert.Equal(original.Character!.FirstName, restored.Character!.FirstName);
        Assert.Equal(original.Character.LastName, restored.Character.LastName);
        Assert.Equal(original.Character.Pronouns, restored.Character.Pronouns);
        Assert.Equal(original.Character.Preset, restored.Character.Preset);
        Assert.Equal(original.Character.Stats, restored.Character.Stats);
        Assert.Equal(original.Events, restored.Events);
        Assert.Contains(original.Events, gameEvent => gameEvent is DiceRolled);
        Assert.Equal(original.State.GetVariable(new VarId("score")), restored.State.GetVariable(new VarId("score")));
        Assert.Equal(original.State.GetBaseStat(EntityId.Player, new StatId("hp")),
            restored.State.GetBaseStat(EntityId.Player, new StatId("hp")));
        Assert.Equal(1, restored.Cursor);
        Assert.True(restored.CanForward);
        Assert.Contains("Updated narrative.", restored.Transcript[0].Frame.Document.ToPlainText());
        Assert.Equal(sourceProgress, restored.RestoredProgressKey);
        Assert.NotEqual(sourceProgress, restored.ProgressKey);

        restored.Forward();
        original.Forward();
        Assert.Equal(original.Events, restored.Events);
        Assert.Equal(original.State.GetVariable(new VarId("score")), restored.State.GetVariable(new VarId("score")));
        Assert.Null(restored.RestoredProgressKey);
        var nextSave = SaveFormat.Read(restored.Save());
        Assert.Equal("2", nextSave.StoryVersion);
        Assert.Equal(updated.ContentFingerprint, nextSave.ContentFingerprint);
        Assert.NotNull(nextSave.ReplayEvidence);
        Assert.Equal(unchanged, bytes);
    }

    [Theory]
    [InlineData("changed-effect")]
    [InlineData("changed-check")]
    [InlineData("changed-character")]
    [InlineData("invalid-character")]
    [InlineData("changed-extension")]
    [InlineData("changed-start")]
    public void Divergent_updates_are_rejected_without_changing_the_save_or_active_history(string change)
    {
        var original = Create(Compile());
        original.Choose("c0");
        original.Choose("c0");
        original.Back();
        var bytes = original.Save();
        var unchanged = bytes.ToArray();
        var active = Create(Compile());
        var activeBefore = active.Save();
        var updated = change switch
        {
            "changed-effect" => Compile(version: "2", reward: 2),
            "changed-check" => Compile(version: "2", difficulty: 13),
            "changed-character" => Compile(version: "2", presetHp: 9),
            "invalid-character" => Compile(version: "2", allowShe: false),
            "changed-extension" => Compile(version: "2", compatibilityId: "custom-v2"),
            "changed-start" => Compile(version: "2", startEffect: 1),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var error = Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(updated, bytes));

        Assert.Contains("Version 1 of Compatibility has changed", error.Message);
        Assert.DoesNotContain("choice", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scene", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(unchanged, bytes);
        Assert.Equal(activeBefore, active.Save());
    }

    [Theory]
    [InlineData("inserted-choice")]
    [InlineData("reordered-choice")]
    [InlineData("changed-selected-label")]
    [InlineData("changed-redo-label")]
    public void Choice_insertions_reordering_and_label_changes_preserve_replay(string change)
    {
        var original = Create(Compile());
        original.Choose("c0");
        original.Choose("c0");
        original.Back();
        var bytes = original.Save();
        var updated = change switch
        {
            "inserted-choice" => Compile(version: "2", insertChoice: true),
            "reordered-choice" => Compile(version: "2", reverseInitialChoices: true),
            "changed-selected-label" => Compile(version: "2", choiceLabel: "A different decision"),
            "changed-redo-label" => Compile(version: "2", middleChoiceLabel: "A different continuation"),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        var restored = StoryHistory.Restore(updated, bytes);

        Assert.Equal(original.Events.Where(gameEvent => gameEvent is not ChoiceMade),
            restored.Events.Where(gameEvent => gameEvent is not ChoiceMade));
        if (change is not ("inserted-choice" or "reordered-choice"))
        {
            Assert.Equal(original.Events, restored.Events);
        }
        Assert.Equal(original.Cursor, restored.Cursor);
        Assert.True(restored.CanForward);
        if (change == "changed-selected-label")
        {
            Assert.Contains("A different decision", restored.Transcript[0].Chosen!.OfType<RenderedText>().Single().Text);
        }
        if (change == "changed-redo-label")
        {
            restored.Forward();
            Assert.Contains("A different continuation",
                restored.Transcript[1].Chosen!.OfType<RenderedText>().Single().Text);
        }
        if (change is "inserted-choice" or "reordered-choice")
        {
            restored.Forward();
            restored.Back();
            Assert.Equal(original.Events.Where(gameEvent => gameEvent is not ChoiceMade),
                restored.Events.Where(gameEvent => gameEvent is not ChoiceMade));
        }
    }

    [Fact]
    public void Divergent_redo_is_rejected_even_when_the_current_cursor_is_at_the_start()
    {
        var original = Create(Compile());
        original.Choose("c0");
        original.Choose("c0");
        original.Back();
        original.Back();
        var bytes = original.Save();

        Assert.Equal(0, original.Cursor);
        Assert.True(original.CanForward);
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(Compile(version: "2", reward: 2), bytes));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void Rebuilt_cursor_must_match_the_validated_timeline(bool differentEvents, bool updated)
    {
        var calls = 0;
        var vary = false;
        var predicates = new NamedPredicateRegistry().Register("Switch", _ => !vary || calls++ == 0);
        var start = differentEvents
            ? "{if @Switch}\n~ set flag:seen\n{/if}\n* [Continue] -> finish"
            : "* [Continue] (requires @Switch) -> finish\n* [Leave] -> END";
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game: { id: cursor-replay, title: Cursor replay, start: start }
                characters:
                  player: { first-name: Tester }
                """,
            ["main.tale"] = $"=== start\n{start}\n=== finish\nFinished.\n-> END",
        });
        var options = new CompilerOptions
        {
            CompatibilityId = "cursor-test-v1",
            Predicates = predicates,
        };
        var result = StoryCompiler.Compile(source, options);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var history = new StoryHistory(result.Story!, 7);
        history.Choose("c0");
        history.Back();
        var bytes = history.Save();
        var target = result.Story!;
        if (updated)
        {
            var files = source.Files.ToDictionary();
            files["main.tale"] += "\n// updated";
            var candidate = StoryCompiler.Compile(new StorySource(files), options);
            Assert.True(candidate.Success, string.Join("\n", candidate.Diagnostics));
            target = candidate.Story!;
        }

        vary = true;
        if (differentEvents)
        {
            var error = Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(target, bytes));
            Assert.Equal(2, calls);
            if (updated) Assert.StartsWith("Version 0.0.0 of Cursor replay has changed", error.Message);
        }
        else
        {
            var restored = StoryHistory.Restore(target, bytes);
            Assert.Equal(2, calls);
            Assert.Equal(history.Events, restored.Events);
        }
    }

    [Fact]
    public void Choosing_after_back_discards_redo_evidence_as_well_as_redo_choices()
    {
        var original = Create(Compile());
        original.Choose("c0");
        original.Choose("c0");
        original.Back();
        original.Choose("c1");
        Assert.False(original.CanForward);

        var restored = StoryHistory.Restore(Compile(version: "2", reward: 2), original.Save());

        Assert.False(restored.CanForward);
        Assert.Equal(original.Events, restored.Events);
        Assert.Equal(original.Cursor, restored.Cursor);
    }

    [Fact]
    public void Saves_without_evidence_cannot_be_restored_after_the_story_changes()
    {
        var oldStory = Compile();
        var original = Create(oldStory);
        original.Choose("c0");
        var legacy = SaveFormat.Write(SaveFormat.Read(original.Save()) with
        {
            ReplayEvidence = null,
            ReplayChoiceIdentities = null,
        });
        var updated = Compile(version: "2", prose: "Updated narrative.");

        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(updated, legacy));
        Assert.Null(SaveFormat.Read(legacy).ReplayEvidence);
    }

    [Fact]
    public void Changed_initial_world_defaults_are_rejected()
    {
        var original = Create(Compile());
        var updated = Compile(version: "2", initialScore: 5);

        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(updated, original.Save()));
    }

    [Fact]
    public void Tampered_evidence_seed_and_cursor_are_rejected_even_for_the_original_revision()
    {
        var original = Create(Compile());
        original.Choose("c0");
        var bytes = original.Save();
        var saved = SaveFormat.Read(bytes);
        var alteredEvidence = saved.ReplayEvidence!.ToArray();
        alteredEvidence[^1] ^= 1;

        Assert.Throws<InvalidDataException>(() =>
            StoryHistory.Restore(original.Story, SaveFormat.Write(saved with { ReplayEvidence = alteredEvidence })));
        Assert.Throws<InvalidDataException>(() =>
            StoryHistory.Restore(original.Story, SaveFormat.Write(saved with { Seed = saved.Seed + 1 })));
        Assert.Throws<InvalidDataException>(() =>
            StoryHistory.Restore(original.Story, SaveFormat.Write(saved with { Cursor = 0 })));
        Assert.Equal(bytes, original.Save());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Choice_labels_at_the_saved_cursor_do_not_invalidate_replay(bool afterAChoice)
    {
        var original = Create(Compile());
        if (afterAChoice)
        {
            original.Choose("c0");
        }

        var bytes = original.Save();
        var updated = Compile(version: "2",
            choiceLabel: afterAChoice ? "Investigate" : "A different decision",
            middleChoiceLabel: afterAChoice ? "A different decision" : "Continue");

        var restored = StoryHistory.Restore(updated, bytes);
        Assert.Equal(original.Events, restored.Events);
        Assert.Contains("A different decision",
            restored.Current.Choices.Single(choice => choice.Key == "c0").Text);
    }

    [Fact]
    public void Changed_replay_evidence_changes_progress_identity()
    {
        var history = Create(Compile());
        history.Choose("c0");
        var save = history.Save();
        var saved = SaveFormat.Read(save);
        var damaged = saved.ReplayEvidence!.ToArray();
        damaged[^1] ^= 1;
        var damagedSave = SaveFormat.Write(saved with { ReplayEvidence = damaged });
        Assert.NotEqual(history.ProgressKey, StoryHistory.DescribeSave("Damaged", damagedSave).ProgressKey);
    }

    [Fact]
    public void Duplicate_outcomes_keep_live_choice_keys_but_replay_the_first_equivalent_choice()
    {
        var firstAvailable = false;
        var predicates = new NamedPredicateRegistry().Register("FirstAvailable", _ => firstAvailable);
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game: { id: duplicate-outcomes, title: Duplicate outcomes, start: start }
                characters:
                  player: { first-name: Tester }
                """,
            ["main.tale"] = """
                === start
                * [First] (requires @FirstAvailable) -> finish
                * [Second] -> finish
                === finish
                Finished.
                -> END
                """,
        });
        var result = StoryCompiler.Compile(source, new CompilerOptions
        {
            CompatibilityId = "duplicate-outcomes-v1",
            Predicates = predicates,
        });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var history = new StoryHistory(result.Story!, 7);
        Assert.Equal(["c1"], history.Current.Choices.Select(choice => choice.Key));
        history.Choose("c1");
        var bytes = history.Save();

        firstAvailable = true;
        var restored = StoryHistory.Restore(result.Story!, bytes);

        Assert.Equal("c0", Assert.Single(restored.Events.OfType<ChoiceMade>()).ChoiceKey);
        Assert.Equal(history.State.GetBaseStat(EntityId.Player, new StatId("hp")),
            restored.State.GetBaseStat(EntityId.Player, new StatId("hp")));
    }

    [Fact]
    public void Replay_does_not_select_a_choice_from_an_inactive_conditional_branch()
    {
        var enabled = true;
        var predicates = new NamedPredicateRegistry().Register("Enabled", _ => enabled);
        var manifest = new ManifestFaker("conditional-replay", "Conditional replay", "start");
        manifest.Character("player", "Tester");
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = manifest.Build(),
            ["main.tale"] = """
                === start
                {if @Enabled}
                * [Inside] -> finish
                {/if}
                * [Outside] -> other
                === finish
                -> END
                === other
                -> END
                """,
        }), new CompilerOptions
            {
                CompatibilityId = "conditional-replay-v1",
                Predicates = predicates,
            });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var history = new StoryHistory(result.Story!, 7);
        history.Choose("c0");
        var bytes = history.Save();

        enabled = false;

        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(result.Story!, bytes));
    }

    [Fact]
    public void Initial_evidence_has_a_frozen_encoding()
    {
        var history = new StoryHistory(TestStories.Linear.Compile(), 42);
        Assert.Equal("03DAEE9310D6DF8136C34ACADD09932D87346013FCEE9CAE22919A047361FD056F",
            Convert.ToHexString(SaveFormat.Read(history.Save()).ReplayEvidence!));
    }

    [Fact]
    public void Complete_path_and_cursor_evidence_has_a_frozen_encoding()
    {
        var history = Create(Compile());
        history.Choose("c0");
        history.Choose("c0");
        history.Back();

        Assert.Equal("03D374F459D587E8BB490F7B6EC4AB3AC1E218C278520B53E06D41104D080889CA",
            Convert.ToHexString(SaveFormat.Read(history.Save()).ReplayEvidence!));
    }

    [Fact]
    public void Every_game_event_and_rendered_inline_has_evidence_serialization_metadata()
    {
        var types = typeof(GameEvent).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(GameEvent).IsAssignableFrom(type))
            .Concat(typeof(RenderedInline).Assembly.GetTypes()
                .Where(type => !type.IsAbstract && typeof(RenderedInline).IsAssignableFrom(type)))
            .ToArray();
        Assert.Equal(types.Length, types.Select(type => type.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var type in types)
        {
            Assert.NotNull(ScenarioJsonContext.Default.GetTypeInfo(type));
        }
    }

    [Theory]
    [InlineData("inserted-choices")]
    [InlineData("removed-sibling")]
    public void Author_fork_follows_the_choice_restored_by_outcome(string change)
    {
        var original = new StoryHistory(CompileNight(follow: false, home: true), 42);
        original.Choose(KeyOf(original.Current, "Stay out"));
        original.Choose(KeyOf(original.Current, "Walk home late"));
        original.Back();
        var bytes = original.Save();
        var updated = change == "inserted-choices"
            ? CompileNight(follow: true, home: true)
            : CompileNight(follow: false, home: false);

        var restored = StoryHistory.Restore(updated, bytes);
        var fork = restored.ForkAuthor();

        Assert.Equal(new SceneId("out"), restored.Current.Scene);
        Assert.Equal(restored.Current.Scene, fork.Current.Scene);
        Assert.Equal(restored.Events, fork.Events);
        Assert.Equal(restored.Cursor, fork.Cursor);
        restored.Forward();
        fork.Forward();
        Assert.Equal(restored.Events, fork.Events);
        Assert.True(fork.Current.IsEnded);

        static StoryDefinition CompileNight(bool follow, bool home)
        {
            var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
            {
                ["story.talewright.yaml"] = """
                    game: { id: night, title: Night, start: night }
                    characters:
                      player: { first-name: Ada }
                    vars: { late: 0 }
                    """,
                ["main.tale"] = string.Join('\n',
                    "=== night",
                    follow ? "* [Follow the mysterious man] -> follow" : "",
                    home ? "* [Home] -> home" : "",
                    "* [Stay out] { var:late += 1 } -> out",
                    "=== home",
                    "Home.",
                    "-> END",
                    "=== out",
                    "Out.",
                    "* [Walk home late] { var:late += 1 } -> END",
                    "=== follow",
                    "-> END"),
            }));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return result.Story!;
        }
    }

    private static string KeyOf(StoryFrame frame, string label) =>
        frame.Choices.Single(choice =>
            string.Concat(choice.Label.OfType<RenderedText>().Select(text => text.Text)) == label).Key;

    private static StoryHistory Create(StoryDefinition story)
    {
        var character = story.Creation!.Default(story.World.Characters[EntityId.Player]) with
        {
            FirstName = "Sam",
            LastName = "Vale",
            Pronouns = "she",
            Stats = new Dictionary<string, int> { ["str"] = 16 },
        };
        return new StoryHistory(story, 42, character);
    }

    private static StoryDefinition Compile(string version = "1", string prose = "Original narrative.",
        bool insertChoice = false, string choiceLabel = "Investigate", int reward = 1, int difficulty = 12,
        int presetHp = 15, bool allowShe = true, string? compatibilityId = null, int startEffect = 0,
        int initialScore = 0, string middleChoiceLabel = "Continue", bool reverseInitialChoices = false)
    {
        var manifest = new ManifestFaker("compatibility", "Compatibility", "start")
        {
            Version = version,
            CreationName = true,
        };
        manifest.Character("player", "Robin", stats: new Dictionary<string, int> { ["athletics"] = 2 });
        manifest.CreationPronouns(allowShe ? ["they", "she"] : ["they"]);
        manifest.CreationStat("str", 8, 12, 16);
        manifest.CreationPreset("explorer", "Explorer", new Dictionary<string, int> { ["str"] = 12, ["hp"] = presetHp });
        var score = manifest.Number("score", initialScore);
        var lines = new List<string> { SceneFaker.Header("start"), prose };
        if (startEffect != 0) lines.Add(EffectFaker.Line(EffectFaker.Increase(score, startEffect)));
        if (insertChoice) lines.Add(ChoiceFaker.Create("middle", "Inserted"));
        var checkedChoice = SkillCheckFaker.Choice(choiceLabel, "athletics", difficulty, "middle", "middle",
            EffectFaker.Decrease(TaleCharacterRef.Player.Field("hp"), 1),
            EffectFaker.Decrease(TaleCharacterRef.Player.Field("hp"), 2));
        var leaveChoice = ChoiceFaker.Create("END", "Leave");
        lines.AddRange(reverseInitialChoices ? [leaveChoice, checkedChoice] : [checkedChoice, leaveChoice]);
        lines.Add(SceneFaker.Header("middle"));
        lines.Add("The middle.");
        lines.Add(ChoiceFaker.Create("finish", middleChoiceLabel, effect: EffectFaker.Increase(score, reward)));
        lines.Add(ChoiceFaker.Create("END", "Quit"));
        lines.Add(SceneFaker.Header("finish"));
        lines.Add("Finished.");
        lines.Add(DivertFaker.To("END"));
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = manifest.Build(),
            ["main.tale"] = string.Join('\n', lines),
        });
        var options = compatibilityId is null ? null : new CompilerOptions { CompatibilityId = compatibilityId };
        var result = StoryCompiler.Compile(source, options);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }
}
