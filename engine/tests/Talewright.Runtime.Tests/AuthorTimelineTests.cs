using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;

namespace Talewright.Runtime.Tests;

public sealed class AuthorTimelineTests
{
    private const string Yaml = """
        id: timeline
        title: Timeline
        start: start
        undo: false
        flags: [ready, fired]
        vars: {count: 0}
        characters:
          player: {first-name: Tester, inventory: {rope: 2}}
          guide: {first-name: Guide, stats: {str: 11}}
        items:
          rope: {name: Rope, modifiers: {str: 2}}
        factions:
          guild: {name: Guild}
        triggers:
          - id: repeat
            when: flag:ready
            do: var:count += 10
            once: false
          - id: once
            when: flag:ready
            do: set flag:fired
            once: true
        """;
    private const string Tale = """
        === start
        ~ var:count += 1
        Count {var:count}. {if flag:ready}Ready.{else}Waiting.{/if}
        * [Prepared {var:count}] (requires: flag:ready) -> target
        * [Wait] -> start
        === target
        @requires not flag:ready
        ~ var:count += 2
        Target {var:count}.
        ::: skill-check athletics dc 10
        success: -> finish
        failure: -> finish
        :::
        === finish
        * [Return] -> start
        * [End] -> END
        """;

    private static StoryDefinition Compile(string tale = Tale, string yaml = Yaml)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = yaml, ["main.tale"] = tale,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    private static AuthorSession Start(StoryDefinition? story = null) =>
        AuthorSession.Create(story ?? Compile(), new("start"), captureDecisions: true, seed: 42);

    private static AuthorSetup Flag(bool value) => new() { Flags = new Dictionary<FlagId, bool> { [new("ready")] = value } };

    [Theory]
    [InlineData("flag", "flag:str")]
    [InlineData("var", "var:str")]
    [InlineData("faction", "faction:str")]
    public void Mixed_field_batches_use_distinct_kind_keys_for_character_stats(string character, string field)
    {
        var yaml = Yaml.Replace("flags: [ready, fired]", "flags: [ready, fired, str]")
            .Replace("vars: {count: 0}", "vars: {count: 0, str: 0}")
            .Replace("guide: {first-name: Guide, stats: {str: 11}}", $"{character}: {{first-name: Guide, stats: {{str: 11}}}}")
            .Replace("guild: {name: Guild}", "str: {name: Guild}");
        var session = Start(Compile(yaml: yaml));
        session.Edit(new()
        {
            Flags = new Dictionary<FlagId, bool> { [new("str")] = true },
            Variables = new Dictionary<VarId, int> { [new("str")] = 7 },
            Standings = new Dictionary<FactionId, int> { [new("str")] = -5 },
            Stats = [new(new(character), new("str"), 15)],
        });
        var changes = Assert.Single(session.Results).Changes;
        Assert.Equal(4, changes.Select(change => change.Field).Distinct().Count());
        Assert.Contains(changes, change => change.Field == field);
        Assert.Contains(changes, change => change.Field == $"stat:{character}:str"
            && change.Before == "11" && change.After == "15");
        Assert.Equal(15, session.State.GetBaseStat(new(character), new("str")));
        var events = session.Events;
        session.Back();
        session.Forward();
        Assert.Equal(events, session.Events);
    }

    [Fact]
    public void Batch_edits_every_declared_kind_across_characters_then_refreshes_without_entry()
    {
        var session = Start();
        var before = session.Events.Count;
        session.Edit(new()
        {
            Flags = new Dictionary<FlagId, bool> { [new("ready")] = true },
            Variables = new Dictionary<VarId, int> { [new("count")] = 7 },
            Standings = new Dictionary<FactionId, int> { [new("guild")] = -50 },
            Stats = [new(new("guide"), new("str"), int.MaxValue), new(EntityId.Player, new("xp"), 150)],
            Inventory = [new(new("guide"), new("rope"), 3), new(EntityId.Player, new("rope"), 0)],
        });
        Assert.Equal(17, session.State.GetVariable(new("count")));
        Assert.Equal(-50, session.State.GetStanding(new("guild")));
        Assert.Equal(session.Story.World.Ruleset.StatsById[new("str")].Max, session.State.GetBaseStat(new("guide"), new("str")));
        Assert.Equal(3, session.State.GetItemCount(new("guide"), new("rope")));
        Assert.Equal(0, session.State.GetItemCount(EntityId.Player, new("rope")));
        Assert.Equal(2, session.State.GetBaseStat(EntityId.Player, new("level")));
        Assert.Equal(1, session.State.VisitCount(new("start")));
        Assert.Equal(0, session.State.RollCount);
        Assert.DoesNotContain(session.Events.Skip(before), e => e is SceneEntered or ChoiceMade or DiceRolled);
        Assert.Contains("Count 17. Ready.", session.Current.Document.ToPlainText());
        Assert.Equal("Prepared 17", session.Current.Choices[0].Text);
        Assert.Contains(session.Decisions, d => d.Subject == "c0" && d.EventStart == session.Events.Count);
        var result = Assert.Single(session.Results);
        Assert.Equal(before, result.EventStart);
        Assert.Contains(result.Changes, value => value.Field == "var:count" && value.Before == "1" && value.After == "17");
        Assert.Equal(2, result.Events.OfType<TriggerFired>().Count());
        Assert.Single(result.Events.OfType<LevelChanged>());
        Assert.Equal(session.Events, session.Checkpoint!.Reenter().Events);
    }

    [Fact]
    public void Refresh_preserves_entry_check_outcome_pending_notes_and_rng_without_reroll()
    {
        var session = Start();
        session.Jump(new("target"));
        var originalEvents = session.Events.ToArray();
        var originalRolls = session.State.RollCount;
        session.Edit(new() { Variables = new Dictionary<VarId, int> { [new("count")] = 30 } });
        Assert.Contains("Target 30.", session.Current.Document.ToPlainText());
        Assert.Equal(originalRolls, session.State.RollCount);
        Assert.Equal(originalEvents, session.Events.Take(originalEvents.Length));
        Assert.Single(session.Events.OfType<DiceRolled>());
        Assert.Contains("check:", session.Current.Document.ToPlainText());
        Assert.Equal("continue", Assert.Single(session.Current.Choices).Key);
        session.Choose("continue");
        Assert.Equal(new SceneId("finish"), session.Current.Scene);
        Assert.Equal(originalRolls, session.State.RollCount);
    }

    [Fact]
    public void Refresh_retargets_ordinary_diverts_without_retaining_stale_actions()
    {
        var session = Start(Compile("""
            === start
            {if flag:ready}
            -> target
            {/if}
            -> finish
            === target
            * [Stay] -> target
            === finish
            * [Stay] -> finish
            """));
        session.Edit(Flag(true));
        Assert.Single(session.Events.OfType<SceneEntered>());
        session.Choose("continue");
        Assert.Equal(new SceneId("target"), session.Current.Scene);
        Assert.Equal(0, session.State.VisitCount(new("finish")));
    }

    [Fact]
    public void Hub_refresh_rebuilds_storylet_eligibility_without_fabricating_visits()
    {
        var session = Start(Compile("""
            === start
            @hub camp
            * [Wait] -> start
            === target
            @title Ready storylet
            @tags camp
            @requires flag:ready
            @once
            * [Return] -> start
            """));
        Assert.DoesNotContain(session.Current.Choices, choice => choice.Key.StartsWith("storylet:", StringComparison.Ordinal));
        session.Edit(Flag(true));
        Assert.Contains(session.Current.Choices, choice => choice.Key == "storylet:target" && choice.Text == "Ready storylet");
        Assert.Equal(0, session.State.VisitCount(new("target")));
        session.Choose("storylet:target");
        session.Jump(new("start"));
        session.Edit(Flag(false));
        session.Edit(Flag(true));
        Assert.DoesNotContain(session.Current.Choices, choice => choice.Key == "storylet:target");
        Assert.Equal(1, session.State.VisitCount(new("target")));
        Assert.Equal(2, session.State.VisitCount(new("start")));
    }

    [Fact]
    public void Refresh_retains_incoming_choice_consequences_and_entry_check_notes_once()
    {
        var session = Start(Compile(Tale.Replace("* [Prepared {var:count}] (requires: flag:ready) -> target", """
            * [Try] { give guide rope } skill-check athletics dc 1
              success: -> target
              failure: -> target
            """)));
        session.Choose("c0");
        var checks = session.Current.Document.Blocks.OfType<RenderedNote>().Count(note => note.Kind == NoteKind.Check);
        Assert.Equal(2, checks);
        session.Edit(new() { Variables = new Dictionary<VarId, int> { [new("count")] = 30 } });
        session.Edit(new() { Variables = new Dictionary<VarId, int> { [new("count")] = 40 } });
        Assert.Equal(checks, session.Current.Document.Blocks.OfType<RenderedNote>().Count(note => note.Kind == NoteKind.Check));
        Assert.Single(session.Current.Document.Blocks.OfType<RenderedNote>(), note => note.Text.Contains("Gained:", StringComparison.Ordinal));
        Assert.Equal(2, session.State.RollCount);
        Assert.Equal(1, session.State.GetItemCount(new("guide"), new("rope")));
    }

    [Fact]
    public void Preserve_jump_bypasses_eligibility_and_retains_visits_trigger_memory_and_dice()
    {
        var session = Start();
        session.Edit(Flag(true));
        session.Jump(new("target"));
        session.Jump(new("target"));
        Assert.Equal(15, session.State.GetVariable(new("count")));
        Assert.Equal(2, session.State.RollCount);
        Assert.Equal(1, session.State.VisitCount(new("start")));
        Assert.Equal(2, session.State.VisitCount(new("target")));
        Assert.Equal(2, session.Events.OfType<TriggerFired>().Count());
        Assert.Empty(session.Events.OfType<ChoiceMade>());
        session.Edit(Flag(false));
        session.Edit(Flag(true));
        Assert.Equal(25, session.State.GetVariable(new("count")));
        Assert.Equal(2, session.Events.OfType<TriggerFired>().Count(e => e.TriggerId == "repeat"));
        Assert.Single(session.Events.OfType<TriggerFired>(), e => e.TriggerId == "once");
    }

    [Fact]
    public void Navigation_replays_choices_edits_repeated_jumps_and_branches_without_player_undo()
    {
        var session = Start();
        Assert.False(session.Story.UndoEnabled);
        session.Edit(Flag(true));
        session.Choose("c0");
        session.Edit(Flag(false));
        session.Jump(new("target"));
        var events = session.Events.ToArray();
        var prose = session.Current.Document.ToPlainText();
        var decisions = session.Decisions.ToArray();
        var cursor = session.Cursor;
        for (var i = 0; i < cursor; i++) { session.Back(); }
        Assert.Equal(1, session.State.GetVariable(new("count")));
        Assert.Empty(session.Events.OfType<TriggerFired>());
        Assert.False(session.CanBack);
        Assert.Throws<InvalidOperationException>(session.Back);
        for (var i = 0; i < cursor; i++) { session.Forward(); }
        Assert.Equal(events, session.Events);
        Assert.Equivalent(decisions, session.Decisions, strict: true);
        Assert.Equal(prose, session.Current.Document.ToPlainText());
        Assert.Equal(2, session.State.RollCount);
        Assert.False(session.CanForward);
        session.Back();
        session.Jump(new("finish"));
        Assert.False(session.CanForward);
        Assert.IsType<AuthorJump>(session.Commands[^1]);
        Assert.Equal(new SceneId("finish"), session.Current.Scene);
        Assert.Equal(1, session.State.RollCount);
    }

    [Theory]
    [MemberData(nameof(AuthorSessionTests.InvalidSetup), MemberType = typeof(AuthorSessionTests))]
    public void Invalid_edit_batches_leave_frame_events_trace_checkpoint_cursor_and_redo_unchanged(AuthorSetup values)
    {
        var session = Start();
        session.Jump(new("target"));
        session.Back();
        var events = session.Events;
        var frame = session.Current;
        var decisions = session.Decisions;
        var checkpoint = session.Checkpoint;
        Assert.Throws<ArgumentException>(() => session.Edit(values));
        Assert.Same(events, session.Events);
        Assert.Same(frame, session.Current);
        Assert.Same(decisions, session.Decisions);
        Assert.Same(checkpoint, session.Checkpoint);
        Assert.Equal(0, session.Cursor);
        Assert.True(session.CanForward);
        Assert.Throws<KeyNotFoundException>(() => session.Jump(new("missing")));
        Assert.True(session.CanForward);
    }

    [Fact]
    public void Failed_cascades_or_presentation_boundaries_discard_the_candidate_including_redo_changes()
    {
        var triggers = string.Join("\n", Enumerable.Range(0, 40).Reverse().Select(i => $"""
              - id: chain-{i}
                when: flag:chain-{i}
                do: set flag:chain-{i + 1}
                once: true
            """));
        var yaml = Yaml[..Yaml.IndexOf("triggers:", StringComparison.Ordinal)]
            .Replace("flags: [ready, fired]", "flags: [ready, fired, chain-0]") + "triggers:\n" + triggers;
        var session = Start(Compile("""
            === start
            * [Stay] (requires: not flag:ready) -> start
            === bad
            ~ set flag:chain-0
            -> END
            """, yaml));
        session.Choose("c0");
        session.Back();
        var events = session.Events;
        var frame = session.Current;
        Assert.Contains("cascade", Assert.Throws<InvalidOperationException>(() => session.Edit(new()
        {
            Flags = new Dictionary<FlagId, bool> { [new("chain-0")] = true },
        })).Message);
        Assert.Contains("no live continuation", Assert.Throws<InvalidOperationException>(() => session.Edit(Flag(true))).Message);
        Assert.Contains("cascade", Assert.Throws<InvalidOperationException>(() => session.Jump(new("bad"))).Message);
        Assert.Same(events, session.Events);
        Assert.Same(frame, session.Current);
        Assert.Equal(0, session.Cursor);
        Assert.True(session.CanForward);
    }

    [Fact]
    public void Ended_runs_require_back_or_fresh_world_rather_than_mutating_ended_state()
    {
        var session = Start();
        session.Jump(new("finish"));
        session.Choose("c1");
        var events = session.Events;
        Assert.Contains("fresh-world", Assert.Throws<InvalidOperationException>(() => session.Jump(new("start"))).Message);
        Assert.Contains("fresh-world", Assert.Throws<InvalidOperationException>(() => session.Edit(Flag(true))).Message);
        Assert.Same(events, session.Events);
        session.Back();
        Assert.False(session.State.IsEnded);
        session.Forward();
        Assert.Equal(events, session.Events);
        session.JumpFresh(new("start"), seed: 7);
        Assert.Empty(session.Commands);
        Assert.False(session.CanBack);
        Assert.Equal(0, session.State.VisitCount(new("finish")));
    }

    [Fact]
    public void Reload_keeps_live_edits_jumps_and_cascades_and_checkpoint_replays_the_command_recipe()
    {
        var session = Start();
        session.Edit(Flag(true));
        session.Jump(new("target"));
        session.Edit(new() { Variables = new Dictionary<VarId, int> { [new("count")] = 50 } });
        var candidate = session.Reload(Compile(Tale.Replace("Target {var:count}.", "Updated {var:count}.")));
        Assert.Equal(session.Events, candidate.Events);
        Assert.Equal(session.Commands, candidate.Commands);
        Assert.Equal(session.Cursor, candidate.Cursor);
        Assert.Contains("Updated 50.", candidate.Current.Document.ToPlainText());
        Assert.Equal(1, candidate.State.RollCount);
        Assert.Equal(candidate.Events, candidate.Checkpoint!.Reenter().Events);
        candidate.Back();
        Assert.Equal(13, candidate.State.GetVariable(new("count")));
        candidate.Forward();
        Assert.Equal(session.Events, candidate.Events);
        Assert.Throws<AuthorReloadException>(() => session.Reload(Compile(Tale.Replace("Count {var:count}.", "Consumed change."))));
    }

    [Fact]
    public void Reload_rejects_redo_explicitly_and_does_not_drop_any_commands()
    {
        var session = Start();
        session.Edit(Flag(true));
        session.Jump(new("target"));
        session.Back();
        var events = session.Events;
        Assert.Contains("redo commands", Assert.Throws<InvalidOperationException>(() => session.Reload(Compile())).Message);
        Assert.Same(events, session.Events);
        Assert.Equal(2, session.Commands.Count);
        Assert.True(session.CanForward);
    }
}
