using System.Text;
using System.Text.Json.Nodes;
using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Tale.Compilation;
using Talewright.Tale.Model;

namespace Talewright.Runtime.Tests;

public sealed class AuthorScenarioTests
{
    private const string Extensions = "talewright-builtins-v1";
    private const string Tale = """
        === start
        Count {var:count}. {player:first-name}.
        * [Go] -> check
        * [Stay] -> start
        === check
        ::: skill-check athletics dc 10
        success: -> finish
        failure: -> finish
        :::
        === finish
        * [Return] -> start
        """;

    private static StoryDefinition Story(string tale = Tale) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                id: portable
                title: Portable
                version: 1.0.0
                start: start
                flags: [ready]
                vars: {count: 0}
                characters:
                  player: {first-name: Tester}
                  guide: {first-name: Guide}
                items:
                  rope: {name: Rope}
                factions:
                  guild: {name: Guild}
                creation:
                  name: true
                  pronouns: [she, they]
                  stats:
                    str: [8, 12, 16]
                  presets:
                    explorer: {label: Explorer, stats: {str: 12}}
                triggers:
                  - id: ready
                    when: flag:ready
                    do: var:count += 10
                    once: true
                """,
            ["main.tale"] = tale,
        })).Story ?? throw new InvalidOperationException("Invalid scenario test fixture.");

    private static AuthorSession Start(StoryDefinition? story = null)
    {
        story ??= Story();
        var character = story.Creation!.Default(story.World.Characters[EntityId.Player]) with { FirstName = "Robin" };
        return AuthorSession.Create(story, new("start"), captureDecisions: true, seed: 42, character: character,
            setup: new()
            {
                Variables = new Dictionary<VarId, int> { [new("count")] = 7 },
                Stats = [new(new("guide"), new("str"), 15)],
                Inventory = [new(EntityId.Player, new("rope"), 3)],
                Standings = new Dictionary<FactionId, int> { [new("guild")] = -50 },
            });
    }

    private static byte[] Bytes(JsonNode data) => Encoding.UTF8.GetBytes(data.ToJsonString());
    private static JsonNode Data(AuthorSession? session = null) => JsonNode.Parse(AuthorScenario.Export(session ?? Start(), Extensions))!;

    [Fact]
    public void Legacy_integer_scenarios_without_text_fields_keep_their_replay_evidence()
    {
        var source = Start();
        source.Edit(new() { Variables = new Dictionary<VarId, int> { [new("count")] = 8 } });
        var data = Data(source);
        data["setup"]!.AsObject().Remove("textVariables");
        foreach (var command in data["commands"]!.AsArray())
            if (command!["values"] is JsonObject values) values.Remove("textVariables");
        Equal(source, AuthorScenario.Import(source.Story, Bytes(data), Extensions).Session);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Roundtrip_reconstructs_creation_setup_edit_choice_jump_dice_and_redo(bool redo)
    {
        var source = Start();
        source.Edit(new() { Flags = new Dictionary<FlagId, bool> { [new("ready")] = true } });
        source.Choose("c0");
        source.Choose("continue");
        source.Jump(new("start"));
        if (redo)
        {
            source.Back();
            source.Back();
        }
        var bytes = AuthorScenario.Export(source, Extensions);
        var imported = AuthorScenario.Import(Story(), bytes, Extensions);
        Assert.False(imported.Adopted);
        Assert.Empty(imported.Differences);
        var copy = imported.Session;
        Equal(source, copy);
        Assert.Equal("Robin", copy.Character!.FirstName);
        Assert.Equal(3, copy.Setup.Inventory.Single().Quantity);
        Assert.Equal(source.Commands.Select(command => command.GetType()), copy.Commands.Select(command => command.GetType()));
        Assert.Equal(source.Cursor, copy.Cursor);
        Assert.Equal(source.CanForward, copy.CanForward);
        while (source.CanForward)
        {
            source.Forward();
            copy.Forward();
            Equal(source, copy);
        }
        Assert.Single(copy.Events.OfType<DiceRolled>());
        Assert.Single(copy.Events.OfType<TriggerFired>());
        Assert.Equal(AuthorScenario.Export(source, Extensions), AuthorScenario.Export(copy, Extensions));
        copy.Back();
        copy.Edit(new() { Variables = new Dictionary<VarId, int> { [new("count")] = 99 } });
        Assert.False(copy.CanForward);
        Assert.NotEqual(source.State.GetVariable(new("count")), copy.State.GetVariable(new("count")));
    }

    private static void Equal(AuthorSession expected, AuthorSession actual)
    {
        Assert.Equal(expected.Events, actual.Events);
        Assert.Equal(expected.Current.Document.ToPlainText(), actual.Current.Document.ToPlainText());
        Assert.Equal(expected.Current.Choices.Select(choice => (choice.Key, choice.Text)),
            actual.Current.Choices.Select(choice => (choice.Key, choice.Text)));
        Assert.Equal(expected.State.RollCount, actual.State.RollCount);
        Assert.Equal(expected.State.GetVariable(new("count")), actual.State.GetVariable(new("count")));
        Assert.Equal(expected.State.GetItemCount(EntityId.Player, new("rope")), actual.State.GetItemCount(EntityId.Player, new("rope")));
        Assert.Equal(expected.State.GetBaseStat(new("guide"), new("str")), actual.State.GetBaseStat(new("guide"), new("str")));
        Assert.Equal(expected.State.VisitCount(new("start")), actual.State.VisitCount(new("start")));
        Assert.Equal(expected.Inspect().Flags, actual.Inspect().Flags);
    }

    [Fact]
    public void Source_adoption_is_explicit_reports_divergence_and_exports_under_new_identity()
    {
        var source = Start();
        source.Choose("c1");
        source.Back();
        var original = AuthorScenario.Export(source, Extensions);
        var changed = Story(Tale.Replace("Count ", "Updated count "));
        Assert.Throws<InvalidDataException>(() => AuthorScenario.Import(changed, original, Extensions));
        var adopted = AuthorScenario.Import(changed, original, Extensions, adoptDifferentSource: true);
        Assert.True(adopted.Adopted);
        Assert.Equal(3, adopted.Differences.Count);
        Assert.True(adopted.Session.CanForward);
        var newBytes = AuthorScenario.Export(adopted.Session, Extensions);
        Assert.NotEqual(original, newBytes);
        Equal(adopted.Session, AuthorScenario.Import(changed, newBytes, Extensions).Session);
        Equal(source, AuthorScenario.Import(source.Story, original, Extensions).Session);
    }

    [Fact]
    public void Compatible_player_save_fork_preserves_original_and_pending_redo()
    {
        var story = Story();
        var history = new StoryHistory(story, 42, story.Creation!.Default(story.World.Characters[EntityId.Player]));
        history.Choose("c0");
        history.Choose("continue");
        history.Back();
        var bytes = history.Save();
        var restored = StoryHistory.Restore(story, bytes);
        var fork = restored.ForkAuthor();
        Assert.True(fork.IsSynthetic);
        Assert.True(fork.CanForward);
        Assert.Equal(restored.Events, fork.Events);
        fork.Forward();
        fork.Edit(new() { Variables = new Dictionary<VarId, int> { [new("count")] = 123 } });
        Assert.Equal(bytes, history.Save());
        Assert.Equal(bytes, restored.Save());
        Assert.Equal(0, restored.State.GetVariable(new("count")));
        var scenario = AuthorScenario.Export(fork, Extensions);
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(story, scenario));
        Assert.Throws<InvalidDataException>(() => AuthorScenario.Import(story, bytes, Extensions));
    }

    [Theory]
    [InlineData("formatVersion", "2")]
    [InlineData("formatVersion", "null")]
    [InlineData("synthetic", "false")]
    [InlineData("target", "\"missing\"")]
    [InlineData("setup", "null")]
    [InlineData("commands", "null")]
    [InlineData("commands", "[null]")]
    [InlineData("commands", "[{\"kind\":\"explode\"}]")]
    [InlineData("commands", "[{\"kind\":\"choice\",\"key\":\"c0\",\"target\":\"start\"}]")]
    [InlineData("commands", "[{\"kind\":\"choice\",\"key\":\"\"}]")]
    [InlineData("commands", "[{\"kind\":\"jump\",\"target\":\"missing\"}]")]
    [InlineData("commands", "[{\"kind\":\"choice\",\"key\":\"not-offered\"}]")]
    [InlineData("cursor", "-1")]
    [InlineData("cursor", "1")]
    [InlineData("currentDigest", "\"tampered\"")]
    [InlineData("endDigest", "\"tampered\"")]
    [InlineData("future", "true")]
    public void Malformed_or_invalid_candidates_are_rejected(string key, string value)
    {
        var session = Start();
        var events = session.Events;
        var data = Data(session);
        data[key] = JsonNode.Parse(value);
        Rejected(() => AuthorScenario.Import(session.Story, Bytes(data), Extensions, adoptDifferentSource: true));
        Assert.Same(events, session.Events);
        Assert.Empty(session.Commands);
    }

    [Theory]
    [InlineData("storyId")]
    [InlineData("extensions")]
    [InlineData("engine")]
    public void Adoption_cannot_bypass_story_or_trusted_compatibility(string field)
    {
        var data = Data();
        data["identity"]![field] = "different";
        Assert.Throws<InvalidDataException>(() => AuthorScenario.Import(Story(), Bytes(data), Extensions, adoptDifferentSource: true));
    }

    [Fact]
    public void Byte_and_identifier_limits_accept_exact_boundary_and_reject_one_beyond()
    {
        var source = Start();
        var bytes = AuthorScenario.Export(source, Extensions);
        var padded = new byte[AuthorScenario.MaxBytes];
        Array.Fill(padded, (byte)' ');
        bytes.CopyTo(padded, 0);
        Equal(source, AuthorScenario.Import(source.Story, padded, Extensions).Session);
        Assert.Throws<InvalidDataException>(() => AuthorScenario.Import(source.Story, new byte[AuthorScenario.MaxBytes + 1], Extensions));
        Assert.Throws<InvalidDataException>(() => AuthorScenario.Import(source.Story, [], Extensions));
        var identity = new string('x', AuthorScenario.MaxIdentifierLength);
        Equal(source, AuthorScenario.Import(source.Story, AuthorScenario.Export(source, identity), identity).Session);
        Assert.Throws<InvalidDataException>(() => AuthorScenario.Export(source, identity + "x"));
    }

    [Fact]
    public void Rejects_duplicate_json_members_unknown_nested_fields_null_values_and_invalid_character()
    {
        var original = Encoding.UTF8.GetString(AuthorScenario.Export(Start(), Extensions));
        Assert.Throws<InvalidDataException>(() => AuthorScenario.Import(Story(),
            Encoding.UTF8.GetBytes(original.Replace("\"cursor\":0", "\"cursor\":0,\"cursor\":0")), Extensions));
        foreach (var mutation in new Action<JsonNode>[]
        {
            data => data["character"]!["stats"]!["str"] = 999,
            data => data["character"]!["stats"] = null,
            data => data["setup"]!["flags"] = null,
            data => data["setup"]!["future"] = true,
            data => data["setup"]!["inventory"] = JsonNode.Parse("""[{"character":"player","item":"rope","quantity":-1}]"""),
            data => data["setup"]!["stats"] = JsonNode.Parse("""[{"character":"guide","stat":"str","value":2},{"character":"guide","stat":"str","value":3}]"""),
        })
        {
            var data = Data();
            mutation(data);
            Rejected(() => AuthorScenario.Import(Story(), Bytes(data), Extensions));
        }
    }

    [Fact]
    public void Full_redo_is_revalidated_even_when_cursor_is_at_origin()
    {
        var data = Data();
        data["commands"] = JsonNode.Parse("""[{"kind":"choice","key":"c1"},{"kind":"jump","target":"missing"}]""");
        Assert.Throws<KeyNotFoundException>(() => AuthorScenario.Import(Story(), Bytes(data), Extensions));
    }

    private static void Rejected(Action action)
    {
        var error = Record.Exception(action);
        Assert.True(error is InvalidDataException or ArgumentException or KeyNotFoundException or InvalidOperationException,
            $"Expected an explicit validation failure, got {error}");
    }

    [Fact]
    public void Field_count_limit_is_checked_before_declared_id_validation()
    {
        var data = Data();
        var flags = new JsonObject();
        for (var index = 0; index < AuthorScenario.MaxFields; index++)
        {
            flags.Add($"flag{index}", true);
        }
        data["setup"] = JsonNode.Parse("""{"flags":{},"variables":{},"standings":{},"stats":[],"inventory":[]}""");
        data["setup"]!["flags"] = flags;
        Assert.Contains("Unknown setup flag", Assert.Throws<ArgumentException>(() =>
            AuthorScenario.Import(Story(), Bytes(data), Extensions)).Message);
        flags.Add("one-too-many", true);
        Assert.Contains("10,000", Assert.Throws<InvalidDataException>(() =>
            AuthorScenario.Import(Story(), Bytes(data), Extensions)).Message);
    }

    [Fact]
    public void Oversized_export_fails_explicitly_without_changing_session()
    {
        var ids = Enumerable.Range(0, 5000).Select(index => $"flag-{index:D4}-" + new string('x', 490)).ToArray();
        var compiled = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = $"id: export-limit\ntitle: Export limit\nstart: start\nflags: [{string.Join(", ", ids)}]\ncharacters:\n  player: {{first-name: Tester}}\n",
            ["main.tale"] = "=== start\n* [Stay] -> start",
        }));
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        var session = AuthorSession.Create(compiled.Story!, new("start"), seed: 42,
            setup: new() { Flags = ids.ToDictionary(id => new FlagId(id), _ => true) });
        var events = session.Events;
        Assert.Contains("2 MiB", Assert.Throws<InvalidDataException>(() => AuthorScenario.Export(session, Extensions)).Message);
        Assert.Same(events, session.Events);
        Assert.Equal(0, session.Cursor);
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("\"7\"")]
    [InlineData("1.5")]
    public void Value_types_must_be_exact_signed_32_bit_integers(string value)
    {
        var data = Data();
        data["setup"]!["variables"]!["count"] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => AuthorScenario.Import(Story(), Bytes(data), Extensions));
    }

    [Theory]
    [InlineData(false, "stats", "value")]
    [InlineData(false, "inventory", "quantity")]
    [InlineData(true, "stats", "value")]
    [InlineData(true, "inventory", "quantity")]
    public void Missing_numeric_members_are_rejected_even_during_cross_source_adoption(bool edit, string collection, string member)
    {
        var data = Data();
        data["identity"]!["fingerprint"] = "different-source";
        var values = data["setup"]!;
        if (edit)
        {
            values = values.DeepClone();
            data["commands"] = new JsonArray(new JsonObject { ["kind"] = "edit", ["values"] = values });
        }
        var entry = values[collection]![0]!.AsObject();
        Assert.True(entry.Remove(member));
        var story = Story();
        Assert.Throws<InvalidDataException>(() => AuthorScenario.Import(story, Bytes(data), Extensions, adoptDifferentSource: true));

        entry[member] = 0;
        var imported = AuthorScenario.Import(story, Bytes(data), Extensions, adoptDifferentSource: true);
        Assert.True(imported.Adopted);
        Assert.Equal(edit ? 1 : 0, imported.Session.Commands.Count);
    }

    [Fact]
    public void Command_limit_accepts_10000_replayed_commands_and_rejects_10001()
    {
        var data = Data();
        data["commands"] = new JsonArray(Enumerable.Range(0, StoryHistory.MaxChoices)
            .Select(_ => (JsonNode)new JsonObject { ["kind"] = "choice", ["key"] = "c1" }).ToArray());
        var changed = Story(Tale + "\n// New source identity");
        var result = AuthorScenario.Import(changed, Bytes(data), Extensions, adoptDifferentSource: true, captureDecisions: false);
        Assert.Equal(StoryHistory.MaxChoices, result.Session.Commands.Count);
        Assert.Equal(0, result.Session.Cursor);
        var roundtrip = AuthorScenario.Import(changed, AuthorScenario.Export(result.Session, Extensions), Extensions, captureDecisions: false);
        Assert.Equal(StoryHistory.MaxChoices, roundtrip.Session.Commands.Count);
        data["commands"]!.AsArray().Add(new JsonObject { ["kind"] = "choice", ["key"] = "c1" });
        Assert.Throws<InvalidDataException>(() => AuthorScenario.Import(changed, Bytes(data), Extensions, adoptDifferentSource: true));
    }
}
