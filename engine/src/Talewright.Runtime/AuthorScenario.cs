using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Talemark.Model;

namespace Talewright.Runtime;

public sealed record AuthorScenarioImport(AuthorSession Session, bool Adopted, IReadOnlyList<string> Differences);

/// <summary>Bounded author replay recipes, distinct from player saves. No mutable state or raw event injection.</summary>
public static class AuthorScenario
{
    public const string FileExtension = ".twscenario";
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxIdentifierLength = 512;
    public const int MaxFields = 10_000;
    private const int FormatVersion = 1;
    public static string EngineCompatibility => "author-replay-v1/"
        + typeof(AuthorSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    public static byte[] Export(AuthorSession session, string extensionIdentity)
    {
        var identity = Identity(session.Story, extensionIdentity);
        var end = session.CanForward
            ? AuthorSession.RestoreTimeline(session.Story, session.StartScene, session.Setup, session.Seed,
                session.Character, session.Commands.ToArray(), session.Commands.Count, session.CaptureDecisions)
            : session;
        var data = new ScenarioData
        {
            FormatVersion = FormatVersion, Synthetic = true, Identity = identity,
            Seed = session.Seed, Target = session.StartScene.Value, Setup = ScenarioValues.From(session.Setup),
            Character = session.Character, Commands = session.Commands.Select(ScenarioCommand.From).ToArray(),
            Cursor = session.Cursor, CurrentDigest = Digest(session), EndDigest = Digest(end),
        };
        ValidateShape(data);
        using var output = new ScenarioOutput();
        JsonSerializer.Serialize(output, data, ScenarioJsonContext.Default.ScenarioData);
        return output.ToArray();
    }

    public static AuthorScenarioImport Import(StoryDefinition story, ReadOnlySpan<byte> bytes,
        string extensionIdentity, bool adoptDifferentSource = false, bool captureDecisions = true)
    {
        var data = Read(bytes);
        var expected = Identity(story, extensionIdentity);
        if (data.Identity.Engine != expected.Engine || data.Identity.Extensions != expected.Extensions)
        {
            throw new InvalidDataException("Scenario engine/tool or extension compatibility differs. Use the matching trusted engine and extensions; adoption cannot bypass this.");
        }
        var sameSource = data.Identity == expected;
        if (!sameSource && !adoptDifferentSource)
        {
            throw new InvalidDataException("Scenario story/source identity differs. Load matching source, or explicitly Adopt scenario to accepted source to create a new synthetic scenario.");
        }
        if (data.Identity.StoryId != expected.StoryId)
        {
            throw new InvalidDataException("Scenario belongs to a different story ID; cross-story adoption is unsupported.");
        }
        var differences = new List<string>();
        if (!sameSource)
        {
            differences.Add("Story version/fingerprint differs; source adoption is not exact reproduction or migration.");
        }
        var setup = data.Setup.ToSetup().Validate(story);
        var character = CharacterSelection.Validate(story, data.Character);
        story.GetScene(new(data.Target));
        var commands = data.Commands.Select(command => command.ToCommand(story)).ToArray();
        // Validate and execute redo too, before preparing the visible prefix.
        var end = AuthorSession.RestoreTimeline(story, new(data.Target), setup, data.Seed, character,
            commands, commands.Length, captureDecisions);
        var current = data.Cursor == commands.Length ? end
            : AuthorSession.RestoreTimeline(story, new(data.Target), setup, data.Seed, character, commands, data.Cursor, captureDecisions);
        if (Digest(current) != data.CurrentDigest)
        {
            differences.Add("Current state/event history, dice or presentation diverged.");
        }
        if (Digest(end) != data.EndDigest)
        {
            differences.Add("Full timeline including redo diverged.");
        }
        if (sameSource && differences.Count > 0)
        {
            throw new InvalidDataException("Scenario replay evidence does not match this source: " + string.Join(" ", differences));
        }
        return new(current, !sameSource, differences.AsReadOnly());
    }

    private static ScenarioIdentity Identity(StoryDefinition story, string extensions)
    {
        Identifier(extensions);
        Identifier(story.Id);
        Identifier(story.Version);
        if (string.IsNullOrEmpty(story.ContentFingerprint))
        {
            throw new InvalidOperationException("Portable scenarios require a story fingerprint and an explicitly trusted extension compatibility identity.");
        }
        return new(story.Id, story.Version, story.ContentFingerprint, extensions, EngineCompatibility);
    }

    private static ScenarioData Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxBytes)
        {
            throw new InvalidDataException("Scenario must be nonempty and at most 2 MiB.");
        }
        try
        {
            RejectDuplicateMembers(bytes);
            var data = JsonSerializer.Deserialize(bytes, ScenarioJsonContext.Default.ScenarioData)
                ?? throw new InvalidDataException("Scenario is empty.");
            ValidateShape(data);
            return data;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed author scenario JSON or unknown fields/commands.", ex);
        }
    }

    private static void RejectDuplicateMembers(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                objects.Push(new(StringComparer.Ordinal));
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                objects.Pop();
            }
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
            {
                throw new InvalidDataException("Scenario contains duplicate JSON members.");
            }
        }
    }

    private static void ValidateShape(ScenarioData data)
    {
        if (data.FormatVersion != FormatVersion || !data.Synthetic)
        {
            throw new InvalidDataException("Unsupported author scenario format or missing synthetic marker.");
        }
        if (data.Identity is null || data.Setup is null || data.Commands is null
            || data.Commands.Length > StoryHistory.MaxChoices || data.Cursor < 0 || data.Cursor > data.Commands.Length)
        {
            throw new InvalidDataException("Scenario identity, setup, commands or cursor are invalid.");
        }
        Identifier(data.Identity.StoryId);
        Identifier(data.Identity.StoryVersion);
        Identifier(data.Identity.Extensions);
        Identifier(data.Identity.Engine);
        Identifier(data.Identity.Fingerprint);
        Identifier(data.Target);
        Identifier(data.CurrentDigest);
        Identifier(data.EndDigest);
        data.Setup.Validate();
        foreach (var command in data.Commands)
        {
            if (command is null)
            {
                throw new InvalidDataException("Null scenario command.");
            }
            command.Validate();
        }
        if (data.Character is { } character)
        {
            if (character.Stats is null || character.Stats.Count > MaxFields)
            {
                throw new InvalidDataException("Invalid scenario character stats.");
            }
            foreach (var key in character.Stats.Keys)
            {
                Identifier(key);
            }
            if (character.Preset is { } preset)
            {
                Identifier(preset);
            }
            if (character.Pronouns is { } pronouns)
            {
                Identifier(pronouns);
            }
        }
    }

    internal static void Identifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdentifierLength || value.Any(char.IsControl))
        {
            throw new InvalidDataException("Scenario identifiers must be nonempty, at most 512 characters and contain no control characters.");
        }
    }

    private static string Digest(AuthorSession session)
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(session.Seed);
            writer.WriteNumberValue(session.State.RollCount);
            foreach (var item in session.Events)
            {
                writer.WriteStartObject();
                writer.WriteString("type", item.GetType().Name);
                writer.WritePropertyName("value");
                JsonSerializer.Serialize(writer, item, item.GetType(), ScenarioJsonContext.Default);
                writer.WriteEndObject();
            }
            var state = session.Inspect();
            JsonSerializer.Serialize(writer, new ScenarioState(state.Characters, state.Factions, state.Flags,
                state.Variables, state.Scenes, state.Triggers)
            {
                TextVariables = state.TextVariables.Count == 0 ? null : state.TextVariables,
            }, ScenarioJsonContext.Default.ScenarioState);
            writer.WriteStartArray();
            foreach (var block in session.Current.Document.Blocks)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(block.GetType().Name);
                switch (block)
                {
                    case RenderedParagraph paragraph: WriteInlines(writer, paragraph.Inlines); break;
                    case RenderedSpeech speech:
                        writer.WriteStringValue(speech.Speaker.Value);
                        writer.WriteStringValue(speech.SpeakerName);
                        WriteInlines(writer, speech.Inlines);
                        break;
                    case RenderedNote note:
                        writer.WriteNumberValue((int)note.Kind);
                        writer.WriteStringValue(note.Text);
                        break;
                    default: throw new NotSupportedException("Unknown scenario presentation block.");
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStringValue(session.Current.Scene?.Value);
            writer.WriteStringValue(session.Current.Title);
            writer.WriteBooleanValue(session.Current.IsEnded);
            foreach (var choice in session.Current.Choices)
            {
                writer.WriteStringValue(choice.Key);
                WriteInlines(writer, choice.Label);
            }
            writer.WriteEndArray();
        }
        stream.FlushFinalBlock();
        return Convert.ToHexString(hash.Hash!);
    }

    private static void WriteInlines(Utf8JsonWriter writer, IReadOnlyList<RenderedInline> inlines)
    {
        writer.WriteStartArray();
        foreach (var inline in inlines)
        {
            writer.WriteStartObject();
            writer.WriteString("type", inline.GetType().Name);
            writer.WritePropertyName("value");
            JsonSerializer.Serialize(writer, inline, inline.GetType(), ScenarioJsonContext.Default);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

internal sealed class ScenarioOutput : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        CheckSize(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckSize(buffer.Length);
        base.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        CheckSize(1);
        base.WriteByte(value);
    }

    private void CheckSize(int count)
    {
        if (Length + count > AuthorScenario.MaxBytes)
        {
            throw new InvalidDataException("Author scenario exceeds 2 MiB.");
        }
    }
}

internal sealed record ScenarioState(IReadOnlyList<AuthorCharacterInspection> Characters,
    IReadOnlyList<AuthorFactionInspection> Factions, IReadOnlyList<KeyValuePair<FlagId, bool>> Flags,
    IReadOnlyList<KeyValuePair<VarId, int>> Variables, IReadOnlyList<AuthorSceneInspection> Scenes,
    IReadOnlyList<AuthorTriggerInspection> Triggers)
{
    public IReadOnlyList<KeyValuePair<VarId, string>>? TextVariables { get; init; }
}

internal sealed record ScenarioIdentity(string StoryId, string StoryVersion, string Fingerprint, string Extensions, string Engine);

internal sealed class ScenarioData
{
    public required int FormatVersion { get; init; }
    public required bool Synthetic { get; init; }
    public required ScenarioIdentity Identity { get; init; }
    public required ulong Seed { get; init; }
    public required string Target { get; init; }
    public required ScenarioValues Setup { get; init; }
    public CharacterSelection? Character { get; init; }
    public required ScenarioCommand[] Commands { get; init; }
    public required int Cursor { get; init; }
    public required string CurrentDigest { get; init; }
    public required string EndDigest { get; init; }
}

internal sealed record ScenarioStat(string Character, string Stat, [property: JsonRequired] int Value);
internal sealed record ScenarioItem(string Character, string Item, [property: JsonRequired] int Quantity);

internal sealed class ScenarioValues
{
    public required Dictionary<string, bool> Flags { get; init; }
    public required Dictionary<string, int> Variables { get; init; }
    // A setter lets source-generated deserialization retain this default in legacy scenarios.
    public Dictionary<string, string> TextVariables { get; set; } = [];
    public required Dictionary<string, int> Standings { get; init; }
    public required ScenarioStat[] Stats { get; init; }
    public required ScenarioItem[] Inventory { get; init; }

    public static ScenarioValues From(AuthorSetup setup) => new()
    {
        Flags = setup.Flags.ToDictionary(pair => pair.Key.Value, pair => pair.Value),
        Variables = setup.Variables.ToDictionary(pair => pair.Key.Value, pair => pair.Value),
        TextVariables = setup.TextVariables.ToDictionary(pair => pair.Key.Value, pair => pair.Value),
        Standings = setup.Standings.ToDictionary(pair => pair.Key.Value, pair => pair.Value),
        Stats = setup.Stats.Select(value => new ScenarioStat(value.Character.Value, value.Stat.Value, value.Value)).ToArray(),
        Inventory = setup.Inventory.Select(value => new ScenarioItem(value.Character.Value, value.Item.Value, value.Quantity)).ToArray(),
    };

    public void Validate()
    {
        if (Flags is null || Variables is null || TextVariables is null || Standings is null || Stats is null || Inventory is null
            || (long)Flags.Count + Variables.Count + TextVariables.Count + Standings.Count + Stats.Length + Inventory.Length > AuthorScenario.MaxFields)
        {
            throw new InvalidDataException("Scenario value batches must contain at most 10,000 fields and no null collections.");
        }
        foreach (var id in Flags.Keys.Concat(Variables.Keys).Concat(TextVariables.Keys).Concat(Standings.Keys))
        {
            AuthorScenario.Identifier(id);
        }
        if (TextVariables.Values.Any(value => value is null) || TextVariables.Keys.Any(Variables.ContainsKey))
        {
            throw new InvalidDataException("Scenario text values cannot be null or share an ID with integer values.");
        }
        foreach (var stat in Stats)
        {
            if (stat is null)
            {
                throw new InvalidDataException("Null scenario stat.");
            }
            AuthorScenario.Identifier(stat.Character);
            AuthorScenario.Identifier(stat.Stat);
        }
        foreach (var item in Inventory)
        {
            if (item is null || item.Quantity < 0)
            {
                throw new InvalidDataException("Scenario inventory requires nonnegative integer quantities.");
            }
            AuthorScenario.Identifier(item.Character);
            AuthorScenario.Identifier(item.Item);
        }
    }

    public AuthorSetup ToSetup() => new()
    {
        Flags = Flags.ToDictionary(pair => new FlagId(pair.Key), pair => pair.Value),
        Variables = Variables.ToDictionary(pair => new VarId(pair.Key), pair => pair.Value),
        TextVariables = TextVariables.ToDictionary(pair => new VarId(pair.Key), pair => pair.Value),
        Standings = Standings.ToDictionary(pair => new FactionId(pair.Key), pair => pair.Value),
        Stats = Stats.Select(value => new AuthorStatValue(new(value.Character), new(value.Stat), value.Value)).ToArray(),
        Inventory = Inventory.Select(value => new AuthorItemValue(new(value.Character), new(value.Item), value.Quantity)).ToArray(),
    };
}

internal sealed class ScenarioCommand
{
    public required string Kind { get; init; }
    public string? Key { get; init; }
    public string? Target { get; init; }
    public ScenarioValues? Values { get; init; }

    public static ScenarioCommand From(AuthorCommand command) => command switch
    {
        AuthorChoice choice => new() { Kind = "choice", Key = choice.Key },
        AuthorJump jump => new() { Kind = "jump", Target = jump.Target.Value },
        AuthorEdit edit => new() { Kind = "edit", Values = ScenarioValues.From(edit.Values) },
        _ => throw new NotSupportedException("Unknown author command."),
    };

    public void Validate()
    {
        switch (Kind)
        {
            case "choice" when Key is not null && Target is null && Values is null: AuthorScenario.Identifier(Key); break;
            case "jump" when Target is not null && Key is null && Values is null: AuthorScenario.Identifier(Target); break;
            case "edit" when Values is not null && Key is null && Target is null: Values.Validate(); break;
            default: throw new InvalidDataException("Unknown or malformed scenario command.");
        }
    }

    public AuthorCommand ToCommand(StoryDefinition story)
    {
        switch (Kind)
        {
            case "choice": return new AuthorChoice(Key!);
            case "edit": return new AuthorEdit(Values!.ToSetup().Validate(story));
            case "jump":
                story.GetScene(new(Target!));
                return new AuthorJump(new(Target!));
            default: throw new InvalidDataException("Unknown scenario command.");
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ScenarioData))]
[JsonSerializable(typeof(ScenarioState))]
[JsonSerializable(typeof(RenderedText))]
[JsonSerializable(typeof(RenderedLineBreak))]
[JsonSerializable(typeof(RenderedMedia))]
[JsonSerializable(typeof(CharacterIdentityChanged))]
[JsonSerializable(typeof(StatChanged))]
[JsonSerializable(typeof(ItemsGranted))]
[JsonSerializable(typeof(ItemsRemoved))]
[JsonSerializable(typeof(StandingChanged))]
[JsonSerializable(typeof(FlagChanged))]
[JsonSerializable(typeof(VariableChanged))]
[JsonSerializable(typeof(TextVariableChanged))]
[JsonSerializable(typeof(SceneEntered))]
[JsonSerializable(typeof(ChoiceMade))]
[JsonSerializable(typeof(DiceRolled))]
[JsonSerializable(typeof(SkillCheckResolved))]
[JsonSerializable(typeof(LevelChanged))]
[JsonSerializable(typeof(TriggerFired))]
[JsonSerializable(typeof(StoryEnded))]
internal partial class ScenarioJsonContext : JsonSerializerContext;
