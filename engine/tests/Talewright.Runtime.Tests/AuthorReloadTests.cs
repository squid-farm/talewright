using Talewright.Core.Events;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;

namespace Talewright.Runtime.Tests;

public sealed class AuthorReloadTests
{
    private const string Yaml = """
        game:
          id: reload
          title: Reload
          start: start
        flags: [ready, rewarded]
        vars: {count: 0}
        characters:
          player: {first-name: Tester}
        triggers:
          - id: reward
            when: flag:ready
            do: "var:count += 10"
            once: false
          - id: once
            when: flag:ready
            do: "set flag:rewarded"
            once: true
        """;
    private const string Tale = """
        === start
        ~ set flag:ready
        * [Travel] -> current
        === current
        Old passage.
        ~ var:count += 1
        ::: skill-check athletics dc 10
        success: -> next
        failure: -> next
        :::
        === next
        * [Return] -> current
        """;

    private static StoryDefinition Compile(string tale = Tale, string yaml = Yaml, CompilerOptions? options = null)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = yaml, ["main.tale"] = tale,
        }), options);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    [Fact]
    public void Pronoun_properties_assign_text_and_preserve_values_across_save_and_replay()
    {
        var story = Compile("""
            === start
            ~ var:word = (player:pronoun:possessive-pronoun:independent)
            {if not (var:word != player:pronoun:possessive-pronoun:independent)}
            For {player:pronoun:object}: {var:word}.
            {/if}
            * [Continue] -> next
            === next
            {var:word}/{player:pronoun:reflexive}
            -> END
            """);
        var history = new StoryHistory(story);
        Assert.Contains("For them: theirs.", history.Current.Document.ToPlainText());
        Assert.Equal("theirs", history.State.GetTextVariable(new("word")));
        history.Choose("c0");
        Assert.Contains("theirs/themselves", history.Current.Document.ToPlainText());
        var restored = StoryHistory.Restore(story, history.Save());
        Assert.Equal(history.Events, restored.Events);
        restored.Back();
        Assert.Contains("For them: theirs.", restored.Current.Document.ToPlainText());
        restored.Forward();
        Assert.Contains("theirs/themselves", restored.Current.Document.ToPlainText());
    }

    [Fact]
    public void One_many_is_engine_owned_and_its_arguments_are_compared_during_reload()
    {
        var tale = Tale.Replace("[Travel]", "[{player:pronoun#one-many(\"Travel\", \"Travel together\")}]")
            .Replace("Old passage.", "{player:pronoun#one-many(\"has\", \"have\")} arrived.");
        var original = AuthorSession.Create(Compile(tale), new("start"), seed: 42);
        original.Choose("c0");
        var candidate = original.Reload(Compile(tale.Replace("arrived.", "returned.")));
        Assert.Contains("have returned.", candidate.Current.Document.ToPlainText());
        var error = Assert.Throws<AuthorReloadException>(() =>
            original.Reload(Compile(tale.Replace("\"Travel together\"", "\"Leave together\""))));
        Assert.Contains("previously consumed", error.Message);
    }

    [Fact]
    public void Reload_replaces_current_prose_without_duplicating_entry_visits_dice_or_triggers()
    {
        var original = AuthorSession.Create(Compile(), new("start"), captureDecisions: true, seed: 42);
        original.Choose("c0");
        var candidate = original.Reload(Compile(Tale.Replace("Old passage.", "New passage.")));
        Assert.Contains("New passage.", candidate.Current.Document.ToPlainText());
        Assert.Contains("Old passage.", original.Current.Document.ToPlainText());
        Assert.Equivalent(original.Events, candidate.Events, strict: true);
        Assert.Equal(1, candidate.State.VisitCount(new("current")));
        Assert.Equal(11, candidate.State.GetVariable(new("count")));
        Assert.Single(candidate.Events.OfType<DiceRolled>());
        Assert.Equal(2, candidate.Events.OfType<TriggerFired>().Count());
        Assert.Equivalent(original.Checkpoint!.Events, candidate.Checkpoint!.Events, strict: true);
        Assert.NotSame(original.Decisions, candidate.Decisions);
        candidate.Choose("continue");
        candidate.Choose("c0");
        Assert.Equal(12, candidate.State.GetVariable(new("count")));
        Assert.Equal(2, candidate.State.VisitCount(new("current")));
        Assert.Equal(2, candidate.Events.OfType<DiceRolled>().Count());
        Assert.Equal(2, candidate.Events.OfType<TriggerFired>().Count());
    }

    [Fact]
    public void Changed_current_entry_effects_and_check_are_applied_once_from_pre_entry_state()
    {
        var original = AuthorSession.Create(Compile(), new("start"), seed: 42);
        original.Choose("c0");
        var candidate = original.Reload(Compile(Tale.Replace("count += 1\n", "count += 3\n").Replace("dc 10", "dc 30")));
        Assert.Equal(13, candidate.State.GetVariable(new("count")));
        Assert.Equal(1, candidate.State.RollCount);
        Assert.Equal(10, candidate.Checkpoint!.State.GetVariable(new("count")));
        Assert.Equal(0, candidate.Checkpoint.State.VisitCount(new("current")));
        var again = candidate.Reload(Compile(Tale.Replace("count += 1\n", "count += 3\n").Replace("dc 10", "dc 30")));
        Assert.Equivalent(candidate.Events, again.Events, strict: true);
    }

    [Theory]
    [InlineData("label")]
    [InlineData("effect")]
    [InlineData("condition")]
    [InlineData("target")]
    public void Changed_previous_choice_meaning_is_rejected_even_when_positional_key_survives(string change)
    {
        var original = AuthorSession.Create(Compile(), new("start"), seed: 42);
        original.Choose("c0");
        var tale = change switch
        {
            "label" => Tale.Replace("[Travel]", "[Different meaning]"),
            "effect" => Tale.Replace("~ set flag:ready", "~ set flag:ready\n~ var:count += 1"),
            "condition" => Tale.Replace("[Travel]", "[Travel] (requires: flag:ready)"),
            _ => Tale.Replace("[Travel] -> current", "[Travel] -> next"),
        };
        var events = original.Events;
        var ex = Assert.Throws<AuthorReloadException>(() => original.Reload(Compile(tale)));
        Assert.Contains("previously consumed", ex.Message);
        Assert.Same(events, original.Events);
        Assert.Equal(11, original.State.GetVariable(new("count")));
    }

    [Theory]
    [InlineData("~ set flag:brandnew")]
    [InlineData("~ var:brandnew = 1")]
    public void Tale_auto_declarations_require_explicit_restart(string effect)
    {
        var original = AuthorSession.Create(Compile(), new("current"), seed: 42);
        var ex = Assert.Throws<AuthorReloadException>(() => original.Reload(Compile(Tale.Replace("Old passage.", effect))));
        Assert.Contains("auto-declarations", ex.Message);
    }

    [Fact]
    public void Changed_schema_or_trigger_definitions_are_rejected()
    {
        var original = AuthorSession.Create(Compile(), new("current"), seed: 42);
        foreach (var yaml in new[]
        {
            Yaml.Replace("first-name: Tester", "first-name: Someone"),
            Yaml.Replace("count: 0", "count: 1"),
            Yaml.Replace("count += 10", "count += 9"),
        })
        {
            Assert.Throws<AuthorReloadException>(() => original.Reload(Compile(yaml: yaml)));
        }
    }

    [Fact]
    public void Deleted_target_and_missing_boundary_preserve_the_original_run()
    {
        var original = AuthorSession.Create(Compile(), new("current"), seed: 42);
        Assert.Throws<KeyNotFoundException>(() => original.Reload(Compile("=== start\n-> END")));
        var ended = AuthorSession.Create(Compile("=== start\n* [End] -> END"), new("start"), seed: 42);
        ended.Choose("c0");
        Assert.Null(ended.Checkpoint);
        Assert.Throws<AuthorReloadException>(() => ended.Reload(ended.Story));
        Assert.True(ended.Current.IsEnded);
    }

    [Fact]
    public void Runtime_failure_in_new_entry_keeps_last_good_run_and_trace()
    {
        var triggers = string.Join("\n", Enumerable.Range(0, 40).Reverse().Select(i => $"""
              - id: chain-{i}
                when: flag:chain-{i}
                do: set flag:chain-{i + 1}
                once: true
            """));
        var yaml = Yaml[..Yaml.IndexOf("triggers:", StringComparison.Ordinal)]
            .Replace("flags: [ready, rewarded]", "flags: [ready, rewarded, chain-0]") + "triggers:\n" + triggers;
        var original = AuthorSession.Create(Compile(yaml: yaml), new("current"), captureDecisions: true, seed: 42);
        var events = original.Events;
        var decisions = original.Decisions;
        var broken = Compile(Tale.Replace("~ var:count += 1", "~ set flag:chain-0"), yaml);
        Assert.Contains("cascade", Assert.Throws<InvalidOperationException>(() => original.Reload(broken)).Message);
        Assert.Same(events, original.Events);
        Assert.Same(decisions, original.Decisions);
    }

    [Fact]
    public void Custom_extensions_are_explicitly_rejected_even_with_a_shared_predicate_registry()
    {
        var registry = new NamedPredicateRegistry().Register("Trusted", _ => true);
        var story = Compile(options: new() { Predicates = registry });
        var session = AuthorSession.Create(story, new("current"), seed: 42);
        Assert.Contains("trusted rebuild", Assert.Throws<AuthorReloadException>(() => session.Reload(story)).Message);
    }

    [Theory]
    [InlineData("custom", false)]
    [InlineData("upper", false)]
    [InlineData("custom", true)]
    [InlineData("upper", true)]
    public void Custom_text_modifiers_are_rejected_before_replaying_callbacks(string name, bool replaceCallback)
    {
        var originalCalls = 0;
        var replacementCalls = 0;
        var modifiers = TextModifierRegistry.CreateDefault().Register(new DelegateTextModifier(name, (text, _) =>
        {
            originalCalls++;
            return text;
        }));
        var tale = Tale.Replace("~ set flag:ready", $"~ set flag:ready\n{{player:first-name#{name}}}");
        var story = Compile(tale, options: new() { Modifiers = modifiers });
        var session = AuthorSession.Create(story, new("start"), seed: 42);
        session.Choose("c0");
        var callsBeforeReload = originalCalls;
        Assert.True(callsBeforeReload > 0);
        if (replaceCallback)
        {
            modifiers = TextModifierRegistry.CreateDefault().Register(new DelegateTextModifier(name, (text, _) =>
            {
                replacementCalls++;
                return text;
            }));
        }
        var candidate = Compile(tale.Replace("Old passage.", "Updated."), options: new() { Modifiers = modifiers });
        var events = session.Events;
        var ex = Assert.Throws<AuthorReloadException>(() => session.Reload(candidate));
        Assert.Contains("custom text modifiers", ex.Message);
        Assert.Equal(callsBeforeReload, originalCalls);
        Assert.Equal(0, replacementCalls);
        Assert.Same(events, session.Events);
    }

    [Theory]
    [InlineData("{if flag:ready}**{player:first-name#custom}**{/if}")]
    [InlineData("* [Hidden {player:first-name#custom}] (requires: flag:ready) -> END")]
    [InlineData("> player: {player:first-name#custom}")]
    public void Candidate_only_custom_modifiers_are_rejected_even_in_unexecuted_templates(string content)
    {
        var calls = 0;
        var modifiers = TextModifierRegistry.CreateDefault().Register(new DelegateTextModifier("custom", (text, _) =>
        {
            calls++;
            return text;
        }));
        const string tale = "=== start\n* [Next] -> current\n=== current\nOriginal.\n* [Finish] -> END";
        var session = AuthorSession.Create(Compile(tale), new("start"), seed: 42);
        var candidate = Compile(tale.Replace("Original.", content), options: new() { Modifiers = modifiers });
        Assert.Throws<AuthorReloadException>(() => session.Reload(candidate));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Engine_owned_builtin_modifiers_from_separate_registries_remain_reloadable()
    {
        var tale = Tale.Replace("~ set flag:ready", "~ set flag:ready\n{player:first-name#upper}");
        var session = AuthorSession.Create(Compile(tale), new("start"), seed: 42);
        session.Choose("c0");
        var candidate = session.Reload(Compile(tale.Replace("Old passage.", "Updated {player:first-name#lower}.")));
        Assert.Contains("Updated tester.", candidate.Current.Document.ToPlainText());
        Assert.Equivalent(session.Events, candidate.Events, strict: true);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("priority")]
    [InlineData("requires")]
    [InlineData("unavailable")]
    [InlineData("once")]
    public void Selected_hub_choice_semantics_are_verified_before_replaying_its_key(string change)
    {
        const string tale = """
            === start
            @hub camp
            Choose.
            === current
            @tags camp
            @title Selected title
            Old passage.
            * [Finish] -> END
            """;
        var session = AuthorSession.Create(Compile(tale), new("start"), seed: 42);
        session.Choose("storylet:current");
        var candidateTale = change switch
        {
            "title" => tale.Replace("Selected title", "Different title"),
            "priority" => tale.Replace("@tags camp", "@tags camp\n@priority 1"),
            "requires" => tale.Replace("@tags camp", "@tags camp\n@requires not flag:ready"),
            "unavailable" => tale.Replace("@tags camp", "@tags camp\n@requires flag:ready"),
            _ => tale.Replace("@tags camp", "@tags camp\n@once"),
        };
        var events = session.Events;
        Assert.Contains("previously consumed", Assert.Throws<AuthorReloadException>(
            () => session.Reload(Compile(candidateTale))).Message);
        Assert.Same(events, session.Events);
        var updated = session.Reload(Compile(tale.Replace("Old passage.", "Updated body.")));
        Assert.Contains("Updated body.", updated.Current.Document.ToPlainText());
        Assert.Equivalent(events, updated.Events, strict: true);
    }

    [Fact]
    public void Synthetic_fresh_jump_recipe_is_reconstructed_instead_of_normal_start_history()
    {
        var session = AuthorSession.Create(Compile(), new("start"), seed: 42);
        session.JumpFresh(new("current"), new AuthorSetup
        {
            Variables = new Dictionary<Talewright.Core.Identity.VarId, int> { [new("count")] = 7 },
        }, seed: 123);
        var candidate = session.Reload(Compile(Tale.Replace("Old passage.", "Updated.")));
        Assert.Equal(8, candidate.State.GetVariable(new("count")));
        Assert.Equal(0, candidate.State.VisitCount(new("start")));
        Assert.Equal(123UL, candidate.Seed);
    }
}
